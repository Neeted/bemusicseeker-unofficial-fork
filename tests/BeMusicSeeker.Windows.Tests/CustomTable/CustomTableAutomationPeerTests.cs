using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


/// <remarks>実CustomTableのproviderから選択・表示範囲・TextBoxへ接続します。sourceのdomain IDは各実sourceのケース、外部UIA通知受信・本番保存・再起動は後続Desktopシナリオが担います。</remarks>
[TestClass]
// 共通WPF Applicationと所有Windowの表示・フォーカスをケースの終了まで所有します。
[DoNotParallelize]
public sealed class CustomTableAutomationPeerTests
{
    /// <summary>21万行の画面外検索と全選択provider取得が投影済みkey集合・scroll・選択を暗黙変更せず、可視行と返却placeholderだけを同じ親へ接続し、保持providerが後発要求・scroll後も同じ対象を指すことを確認します。可視行の再照会回数は固定しません。</summary>
    [TestMethod]
    public void VirtualRows_SearchSelectionAndRealizeKeepProjectionLimitedToRequestedAndVisibleRows()
    {
        var source = new RecordingSource(210_000);
        WithTable(source, (table, root) =>
        {
            ISelectionProvider selection = Pattern<ISelectionProvider>(root, PatternInterface.Selection);
            IItemContainerProvider container = Pattern<IItemContainerProvider>(root, PatternInterface.ItemContainer);
            Assert.IsTrue(selection.CanSelectMultiple);
            Assert.IsFalse(selection.IsSelectionRequired);
            Assert.IsNull(root.GetPattern(PatternInterface.Grid));
            Assert.IsNull(root.GetPattern(PatternInterface.Table));
            Assert.AreEqual(AutomationControlType.List, root.GetAutomationControlType());
            int[] projectedKeys = source.GetProjectedKeysSnapshot();
            int firstVisible = table.FirstVisibleRowIndex;
            IRawElementProviderSimple farProvider = container.FindItemByProperty(null, AutomationElementIdentifiers.AutomationIdProperty.Id, "record:180000");
            AutomationPeer far = Peer(farProvider);
            IRawElementProviderSimple fartherProvider = container.FindItemByProperty(null, AutomationElementIdentifiers.AutomationIdProperty.Id, "record:200000");
            AutomationPeer farther = Peer(fartherProvider);
            CollectionAssert.AreEquivalent(projectedKeys, source.GetProjectedKeysSnapshot());
            Assert.AreEqual(firstVisible, table.FirstVisibleRowIndex);
            Assert.AreSame(far, root.GetChildren().Single(child => child.GetAutomationId() == "record:180000"));
            Assert.AreSame(farther, root.GetChildren().Single(child => child.GetAutomationId() == "record:200000"));
            Assert.AreSame(root, far.GetParent()); Assert.AreSame(root, farther.GetParent());
            Assert.AreEqual(string.Empty, far.GetName());
            Assert.AreEqual(string.Empty, farther.GetName());
            CollectionAssert.AreEquivalent(projectedKeys, source.GetProjectedKeysSnapshot());
            Assert.AreEqual(0, selection.GetSelection().Length);
            Assert.IsNull(container.FindItemByProperty(farProvider, AutomationElementIdentifiers.AutomationIdProperty.Id, "record:180000"));
            Pattern<ISelectionItemProvider>(far, PatternInterface.SelectionItem).Select();
            Pattern<ISelectionItemProvider>(farther, PatternInterface.SelectionItem).AddToSelection();
            projectedKeys = source.GetProjectedKeysSnapshot();
            IRawElementProviderSimple[] selectedProviders = selection.GetSelection();
            CollectionAssert.AreEquivalent(new[] { "record:180000", "record:200000" }, selectedProviders.Select(value => Peer(value).GetAutomationId()).ToArray());
            foreach (IRawElementProviderSimple provider in selectedProviders)
            {
                AutomationPeer selectedPeer = Peer(provider);
                Assert.IsTrue(root.GetChildren().Contains(selectedPeer));
                Assert.AreSame(root, selectedPeer.GetParent());
            }
            CollectionAssert.AreEquivalent(projectedKeys, source.GetProjectedKeysSnapshot(), "選択provider取得だけでは新しい行を投影しません。");
            Pattern<ISelectionItemProvider>(far, PatternInterface.SelectionItem).RemoveFromSelection();
            Assert.IsFalse(table.IsRowSelected(180000));
            Assert.IsTrue(table.IsRowSelected(200000));
            Pattern<IVirtualizedItemProvider>(far, PatternInterface.VirtualizedItem).Realize();
            Assert.IsFalse(far.IsOffscreen());
            Assert.AreEqual("value 180000", far.GetName());
            Assert.IsTrue(table.FirstVisibleRowIndex <= 180000);
            AutomationPeer[] visibleRows = root.GetChildren().Where(child => child.GetAutomationControlType() == AutomationControlType.ListItem && !child.IsOffscreen()).ToArray();
            Assert.IsTrue(visibleRows.Length is > 0 and < 20);
            Assert.IsTrue(visibleRows.Any(child => child.GetAutomationId() == "record:180000"));
            Assert.IsTrue(source.ProjectedCount < 100, "全行実体化は要求していません。");
            Pattern<IScrollItemProvider>(farther, PatternInterface.ScrollItem).ScrollIntoView();
            Assert.IsFalse(farther.IsOffscreen());
            Assert.IsTrue(table.IsRowSelected(200000));
            Assert.AreEqual("record:180000", far.GetAutomationId());
            Assert.AreSame(root, far.GetParent());
            Pattern<IVirtualizedItemProvider>(far, PatternInterface.VirtualizedItem).Realize();
            Assert.IsFalse(far.IsOffscreen());
            GC.KeepAlive(farProvider); GC.KeepAlive(fartherProvider); GC.KeepAlive(selectedProviders);
        });
    }

    /// <summary>両軸Scrollの百分率・表示割合が実offsetへ反映され、画面座標で得た可視セルが実hit-testの同じ行・列を指すことを確認します。</summary>
    [TestMethod]
    public void BothAxisScroll_UpdatesOffsetsAndVisibleCellBounds()
    {
        WithTable(new RecordingSource(100), (table, root) =>
        {
            IScrollProvider scroll = Pattern<IScrollProvider>(root, PatternInterface.Scroll);
            Assert.IsTrue(scroll.HorizontallyScrollable);
            Assert.IsTrue(scroll.VerticallyScrollable);
            Assert.IsTrue(scroll.HorizontalViewSize is > 0d and < 100d);
            Assert.IsTrue(scroll.VerticalViewSize is > 0d and < 100d);
            // スクロール前に子のキャッシュを満たしておき、配送を待たない照会が最新の可視範囲を返すことを後で確かめる。
            AutomationPeer rowBefore = root.GetChildren().First(child => child.GetAutomationControlType() == AutomationControlType.ListItem);
            Assert.AreEqual("record:0", rowBefore.GetAutomationId());
            Assert.AreEqual(1, rowBefore.GetChildren().Count);
            scroll.SetScrollPercent(100d, 100d);
            Assert.AreEqual(100d, scroll.HorizontalScrollPercent, 0.001d);
            Assert.AreEqual(100d, scroll.VerticalScrollPercent, 0.001d);
            Assert.IsTrue(table.HorizontalOffset > 0d);
            Assert.IsTrue(table.FirstVisibleRowIndex > 90);
            string[] idsAfter = [.. root.GetChildren().Where(child => child.GetAutomationControlType() == AutomationControlType.ListItem).Select(child => child.GetAutomationId())];
            Assert.IsFalse(idsAfter.Contains("record:0"), "配送前の照会でも、スクロール後の可視の行だけを子として返す。");
            Assert.IsTrue(idsAfter.Contains("record:99"));
            Assert.AreEqual(0, rowBefore.GetChildren().Count, "可視範囲を外れた行は、配送前の照会でも子を持たない。");
            AutomationPeer row = root.GetChildren().First(child => child.GetAutomationControlType() == AutomationControlType.ListItem);
            AutomationPeer cell = row.GetChildren().Single();
            Assert.AreEqual("cell:Second", cell.GetAutomationId());
            Rect rect = cell.GetBoundingRectangle();
            Assert.IsTrue(rect.Width > 0d);
            CustomTableHitTestResult hit = table.HitTestTable(table.PointFromScreen(new Point(rect.Left + rect.Width / 2d, rect.Top + rect.Height / 2d)));
            Assert.AreEqual(table.FirstVisibleRowIndex, hit.RowIndex);
            Assert.AreEqual("Second", hit.Column.Id);
            scroll.Scroll(ScrollAmount.SmallDecrement, ScrollAmount.SmallDecrement);
            Assert.IsTrue(scroll.HorizontalScrollPercent < 100d);
            Assert.IsTrue(scroll.VerticalScrollPercent < 100d);
            int before = table.FirstVisibleRowIndex;
            double offset = table.HorizontalOffset;
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scroll.SetScrollPercent(0d, 101d));
            Assert.AreEqual(before, table.FirstVisibleRowIndex);
            Assert.AreEqual(offset, table.HorizontalOffset);
            var sorts = new List<CustomTableSortRequestedEventArgs>();
            table.SortRequested += (_, request) => sorts.Add(request);
            AutomationPeer header = root.GetChildren().Single(child => child.GetAutomationControlType() == AutomationControlType.HeaderItem);
            Assert.AreEqual("header:Second", header.GetAutomationId());
            Assert.IsTrue(header.GetBoundingRectangle().Width > 0d);
            Pattern<IInvokeProvider>(header, PatternInterface.Invoke).Invoke();
            Assert.AreEqual("value", sorts.Single().SortMemberPath);
            Assert.AreSame(table.Columns[1], sorts.Single().Column);
        }, [Column("Title", 400), Column("Second", 400)]);
    }

    /// <summary>SetValueは実TextBox・開始・確定の各通知を一回通し、pureなreadonly照会と開始拒否が編集状態や値を変えないことを確認します。</summary>
    [TestMethod]
    public void CellValue_UsesRealEditorAndExistingGateWithoutSavingQueryState()
    {
        var source = new RecordingSource(2);
        WithTable(source, (table, root) =>
        {
            var owner = new MainChartListViewModel();
            table.DataContext = owner;
            object row = source[0] ?? throw new AssertFailedException("一行目がありません。");
            object other = source[1] ?? throw new AssertFailedException("二行目がありません。");
            bool accepted = true;
            bool refuseActualStart = false;
            owner.CellEditBeginningRequested += (_, request) => request.Accepted = accepted;
            var started = new List<MainChartListCellEditContext>();
            var ended = new List<MainChartListCellEditEndedEventArgs>();
            owner.CellEditStarted += (_, context) => started.Add(context);
            owner.CellEditEndedRequested += (_, request) => ended.Add(request);
            table.CellEditBeginning += (_, request) => request.Cancel = refuseActualStart || !owner.TryBeginCellEdit(request.Row, request.EditPropertyName);
            table.CellEditStarted += (_, request) => owner.NotifyCellEditStarted(request.Row, request.EditPropertyName);
            table.CellEditEnded += (_, request) => owner.RequestCellEditEnded(request.Row, request.EditPropertyName, request.Text, request.Commit, request.OriginalText);
            Assert.IsTrue(owner.TryBeginCellEdit(row, "Value"));
            accepted = false;
            Assert.IsFalse(owner.CanBeginCellEdit(other, "Value"));
            owner.NotifyCellEditStarted(row, "Value");
            Assert.AreSame(row, started.Single().Row, "否認された照会で既存の受付を消しません。");
            owner.RequestCellEditEnded(row, "Value", "cancel", false);
            started.Clear(); ended.Clear(); accepted = true;
            AutomationPeer cell = Cell(root, "record:0", "cell:Title");
            IValueProvider value = Pattern<IValueProvider>(cell, PatternInterface.Value);
            Assert.IsFalse(value.IsReadOnly);
            Assert.IsFalse(value.IsReadOnly);
            Assert.AreEqual(0, started.Count);
            Assert.AreEqual(0, ended.Count);
            TextBox? actualEditor = null;
            table.CellEditStarted += (_, _) => actualEditor = table.Children.OfType<Canvas>().Single().Children.OfType<TextBox>().Single();
            value.SetValue("updated value");
            Assert.IsNotNull(actualEditor);
            Assert.AreEqual("updated value", actualEditor.Text);
            Assert.AreEqual(1, started.Count);
            Assert.AreEqual(1, ended.Count);
            Assert.AreSame(row, ended[0].Context.Row);
            Assert.AreEqual("Value", ended[0].Context.PropertyName);
            Assert.AreEqual("updated value", ended[0].Text);
            Assert.IsTrue(ended[0].Commit);
            Assert.AreEqual("value 0", value.Value, "providerはモデルを直接書き換えません。");
            Assert.AreEqual(0, cell.GetChildren().Count);
            accepted = false;
            Assert.IsTrue(value.IsReadOnly);
            Assert.ThrowsExactly<InvalidOperationException>(() => value.SetValue("forbidden"));
            Assert.AreEqual(1, ended.Count);
            accepted = true; refuseActualStart = true;
            Assert.IsFalse(value.IsReadOnly);
            Assert.ThrowsExactly<InvalidOperationException>(() => value.SetValue("rejected at start"));
            Assert.AreEqual(1, started.Count);
            Assert.AreEqual(1, ended.Count);
            table.DataContext = null;
        });
    }

    /// <summary>F2で開いた標準TextBoxのValueはdraftだけを変え、Escapeは取消、Enterは同じ行・列へ一回の確定値を送ることを確認します。</summary>
    [TestMethod]
    public void ChildTextBoxValue_RemainsDraftUntilEnterAndEscapeCancels()
    {
        WithTable(new RecordingSource(1), (table, root) =>
        {
            AutomationPeer row = root.GetChildren().Single(child => child.GetAutomationControlType() == AutomationControlType.ListItem);
            Pattern<ISelectionItemProvider>(row, PatternInterface.SelectionItem).Select();
            var completed = new List<CustomTableCellEditEndedEventArgs>();
            table.CellEditEnded += (_, request) => completed.Add(request);
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AutomationPeer cell = row.GetChildren().Single();
            foreach (bool commit in new[] { false, true })
            {
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                AutomationPeer editor = cell.GetChildren().Single();
                Assert.AreEqual("CellEditor", editor.GetAutomationId());
                Assert.AreEqual("Title", editor.GetName());
                Pattern<IValueProvider>(editor, PatternInterface.Value).SetValue("draft text");
                Assert.AreEqual("value 0", Pattern<IValueProvider>(cell, PatternInterface.Value).Value);
                Assert.IsTrue(table.HandleKeyDown(commit ? Key.Enter : Key.Escape, ModifierKeys.None));
                Assert.AreEqual(0, cell.GetChildren().Count);
                Assert.AreEqual(commit, completed.Last().Commit);
                Assert.AreEqual("draft text", completed.Last().Text);
                Assert.AreEqual("Value", completed.Last().EditPropertyName);
                Assert.AreEqual(0, completed.Last().RowIndex);
            }
            Assert.AreEqual(2, completed.Count);
        });
    }

    /// <summary>sortと同scopeの再投影では同じidentityの値・選択を保ち、削除・同domain IDの別identity・別scopeへ移った旧row/cellの操作と定数metadata照会が隣接行を変更せず失敗することを確認します。</summary>
    [TestMethod]
    public void RetainedProviders_FollowIdentityAcrossSortAndFailAfterRemovalReplacementOrScopeChange()
    {
        foreach (string mutation in new[] { "remove", "replace", "scope" })
        {
            var source = new RecordingSource(4);
            WithTable(source, (table, root) =>
            {
                IItemContainerProvider container = Pattern<IItemContainerProvider>(root, PatternInterface.ItemContainer);
                AutomationPeer row = Peer(container.FindItemByProperty(null, AutomationElementIdentifiers.AutomationIdProperty.Id, "record:0"));
                AutomationPeer cell = row.GetChildren().Single();
                ISelectionItemProvider selection = Pattern<ISelectionItemProvider>(row, PatternInterface.SelectionItem);
                IValueProvider value = Pattern<IValueProvider>(cell, PatternInterface.Value);
                Assert.AreEqual(AutomationControlType.ListItem, row.GetAutomationControlType());
                Assert.AreEqual(AutomationControlType.Custom, cell.GetAutomationControlType());
                Assert.IsFalse(string.IsNullOrEmpty(row.GetClassName()));
                Assert.IsFalse(string.IsNullOrEmpty(cell.GetClassName()));
                selection.Select();
                source.Reverse();
                Assert.AreEqual("record:0", row.GetAutomationId());
                Assert.AreEqual("value 0", value.Value);
                Assert.IsTrue(selection.IsSelected);
                Assert.AreEqual(3, table.SelectedIndex);
                table.ItemsSource = source.WithSameScope();
                Assert.AreEqual("value 0", value.Value);
                source = (RecordingSource)table.ItemsSource;
                if (mutation == "scope") { table.ItemsSource = new RecordingSource(4); }
                else if (mutation == "replace") { source.ReplaceTokenAt(3); }
                else { source.RemoveTokenAt(3); }
                table.ClearSelection();
                Assert.ThrowsExactly<ElementNotAvailableException>(() => selection.Select());
                Assert.ThrowsExactly<ElementNotAvailableException>(() => Pattern<IScrollItemProvider>(row, PatternInterface.ScrollItem).ScrollIntoView());
                Assert.ThrowsExactly<ElementNotAvailableException>(() => value.SetValue("must not reach next row"));
                Assert.ThrowsExactly<ElementNotAvailableException>(() => _ = value.Value);
                foreach (AutomationPeer oldPeer in new[] { row, cell })
                {
                    (string Property, Action Read)[] metadata =
                    [
                        ("ClassName", () => oldPeer.GetClassName()),
                        ("ControlType", () => oldPeer.GetAutomationControlType()),
                        ("Name", () => oldPeer.GetName()),
                        ("AutomationId", () => oldPeer.GetAutomationId()),
                        ("AcceleratorKey", () => oldPeer.GetAcceleratorKey()),
                        ("AccessKey", () => oldPeer.GetAccessKey()),
                        ("HelpText", () => oldPeer.GetHelpText()),
                        ("ItemStatus", () => oldPeer.GetItemStatus()),
                        ("ItemType", () => oldPeer.GetItemType()),
                        ("LabeledBy", () => oldPeer.GetLabeledBy()),
                        ("Orientation", () => oldPeer.GetOrientation()),
                        ("IsPassword", () => oldPeer.IsPassword()),
                        ("IsRequiredForForm", () => oldPeer.IsRequiredForForm())
                    ];
                    foreach ((string property, Action read) in metadata)
                    {
                        Assert.ThrowsExactly<ElementNotAvailableException>(read, $"{mutation}: {property}");
                    }
                }
                Assert.AreEqual(0, Pattern<ISelectionProvider>(root, PatternInterface.Selection).GetSelection().Length);
                AutomationPeer survivor = root.GetChildren().First(peer => peer.GetAutomationControlType() == AutomationControlType.ListItem);
                Assert.IsFalse(string.IsNullOrEmpty(survivor.GetClassName()));
                Assert.AreEqual(mutation == "scope" ? "value 0" : "value 3",
                    (table.ItemsSource[0] as TestRow ?? throw new AssertFailedException("生存行がありません。")).Value);
            });
        }
    }

    /// <summary>実列factoryのTAGは常時readonly、MEMOはplaylist詳細だけで編集可能、外部同期ONではMEMO以外をreadonlyとすることを確認します。</summary>
    [TestMethod]
    public void RealPlaylistColumns_ExposeMemoOnlyDuringExternalSyncAndNeverEditTag()
    {
        foreach (bool sync in new[] { false, true })
        {
            var entry = new BMSTableEntry();
            var playlist = new BMSTable { is_external_sync = sync, entries = [entry] };
            PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, null).CreateViewRow();
            var settings = new CustomTableColumnSettings();
            settings.Tag.Visibility = settings.Memo.Visibility = settings.EntryLevel.Visibility = Visibility.Visible;
            settings.Tag.Width = settings.Memo.Width = settings.EntryLevel.Width = 70;
            CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => column.Id is "Tag" or "Memo" or "EntryLevel")];
            WithTable(new RecordingSource(1, _ => row), (_, root) =>
            {
                Assert.IsNull(root.GetChildren().Single(child => child.GetAutomationId() == "header:Memo").GetPattern(PatternInterface.Invoke),
                    "並べ替えを持たない列見出しにInvokeを公開しません。");
                Assert.IsTrue(Pattern<IValueProvider>(Cell(root, "record:0", "cell:Tag"), PatternInterface.Value).IsReadOnly);
                Assert.IsFalse(Pattern<IValueProvider>(Cell(root, "record:0", "cell:Memo"), PatternInterface.Value).IsReadOnly);
                Assert.AreEqual(sync, Pattern<IValueProvider>(Cell(root, "record:0", "cell:EntryLevel"), PatternInterface.Value).IsReadOnly);
                Assert.AreSame(playlist, entry.parent);
            }, columns);
        }
        var librarySettings = new CustomTableColumnSettings();
        librarySettings.Memo.Visibility = Visibility.Visible;
        WithTable(new RecordingSource(1, _ => LibraryChartRow.FromChartFile(ChartTestValues.Empty())), (_, root) =>
        {
            Assert.IsTrue(Pattern<IValueProvider>(Cell(root, "record:0", "cell:Memo"), PatternInterface.Value).IsReadOnly);
        }, [.. CustomTableColumnFactory.CreateMainColumns(librarySettings).Where(column => column.Id == "Memo")]);
    }

    /// <summary>UI Automationのクライアントが接続していない（窓口が作られていない）表で、移動・選択・構造・値の各操作をしても、窓口を作らず、通知の配送も予約しないことを確認します。</summary>
    /// <remarks>操作のたびに窓口を作る誤りを検出します。窓口がなければ通知を予約しないことは、静的レビューで確認します。</remarks>
    [TestMethod]
    public void WithoutPeer_OperationsNeverCreateThePeerOrScheduleDelivery()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var table = new CustomTableView { Width = 300, Height = 150, HeaderHeight = 20, RowHeight = 25, Columns = [Column("Title", 120), Column("Second", 120)], ItemsSource = new RecordingSource(100) };
            table.Measure(new Size(300, 150));
            table.Arrange(new Rect(0, 0, 300, 150));
            table.UpdateLayout();
            try
            {
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                void AssertNoPeer(string step)
                {
                    table.UpdateLayout();
                    TestUiDispatcherHost.ProcessQueuedBackground();
                    Assert.IsNull(UIElementAutomationPeer.FromElement(table), step);
                }

                vertical.Value = 20;
                AssertNoPeer("スクロール");
                table.SelectedIndex = 22;
                AssertNoPeer("選択");
                Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
                AssertNoPeer("現在セルの移動");
                table.Columns = [Column("Title", 200)];
                AssertNoPeer("構造（列）");
                table.ItemsSource = new RecordingSource(50);
                AssertNoPeer("構造（行集合）");
                table.RefreshDisplay();
                AssertNoPeer("値");
                table.HandleKeyDown(Key.F2, ModifierKeys.None);
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                AssertNoPeer("編集欄の出入り");
            }
            finally { table.HandleKeyDown(Key.Escape, ModifierKeys.None); table.ItemsSource = null; }
        });
    }

    /// <summary>窓口があり、クライアントが表の子だけを照会して行の子は照会していない状態で、可視行が重なって残る小さなスクロールと行の高さの変更を行っても、入力の処理中（描画と配送の前）に行集合へ添字アクセスしないことを確認します。</summary>
    /// <remarks>スクロールや寸法の変更のたびに、照会されていない可視行の子（セル）を作って値を読む誤りを検出します。</remarks>
    [TestMethod]
    public void ScrollAndMetricChange_DoNotReadRowsWhoseChildrenWereNotQueried()
    {
        var source = new RecordingSource(100);
        WithTable(source, (table, root) =>
        {
            Assert.IsTrue(root.GetChildren().Any(child => child.GetAutomationControlType() == AutomationControlType.ListItem));
            ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
            int AccessTotal() => Enumerable.Range(0, 100).Sum(source.AccessCount);

            source.ResetAccessCounts();
            vertical.Value = 1;
            Assert.AreEqual(0, AccessTotal(), "スクロールの処理中に行集合を読む。");
            table.RowHeight = 24;
            Assert.AreEqual(0, AccessTotal(), "行の高さの変更の処理中に行集合を読む。");
            TestUiDispatcherHost.ProcessQueuedBackground();
        });
    }

    /// <summary>
    /// 通知を再描画の要求と結び付けず、変更の種類ごとに配送の時点で一回へまとめることを確認します。要求済みの行を画面外へ出した後も、スクロール・選択の変更・現在セルの移動の配送では
    /// その行の値を読まず、値の変更（表示更新）の配送だけが読み、一回の画面更新の間に三回の表示更新をしても配置と描画の中では配送せず、配送後の読取りは一回分と等しくなります。
    /// </summary>
    /// <remarks>スクロールや選択のたびに要求済みの行の値を読む誤り、描画の要求ごとに通知処理へ進む誤り、配置と描画の中での通知、値の通知を画面更新の単位へまとめない誤りを検出します。</remarks>
    [TestMethod]
    public void Notifications_AreSeparatedByKindAndCoalescedPerUpdate()
    {
        var source = new RecordingSource(100);
        WithTable(source, (table, root) =>
        {
            // 要求済みの行0のセルを取得して保持する。保持する間は窓口の要求済みの行として残る。
            AutomationPeer row0 = root.GetChildren().Single(child => child.GetAutomationId() == "record:0");
            AutomationPeer cell0 = row0.GetChildren().First(child => child.GetAutomationId() == "cell:Title");
            Assert.IsNotNull(cell0);
            TestUiDispatcherHost.ProcessQueuedBackground();
            ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);

            // 行0を画面外へ出す。スクロールの配送は、スクロールの割合だけを扱い、行の値を読まない。
            source.ResetAccessCounts();
            vertical.Value = 50;
            TestUiDispatcherHost.ProcessQueuedBackground();
            Assert.AreEqual(0, source.AccessCount(0), "スクロールの配送で要求済みの行の値を読む。");

            // 選択の変更と現在セルの移動の配送も、行0の値を読まない。
            table.SelectedIndex = 52;
            TestUiDispatcherHost.ProcessQueuedBackground();
            Assert.AreEqual(0, source.AccessCount(0), "選択の配送で要求済みの行の値を読む。");
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            TestUiDispatcherHost.ProcessQueuedBackground();
            Assert.AreEqual(0, source.AccessCount(0), "現在セルの移動で要求済みの行の値を読む。");

            // 値の変更（表示更新）の配送だけが、要求済みの行の値を読む。
            source.ResetAccessCounts();
            table.RefreshDisplay();
            TestUiDispatcherHost.ProcessQueuedBackground();
            int single = source.AccessCount(0);
            Assert.IsTrue(single > 0, "表示更新の配送は、要求済みのセルの値を読む。");

            // 一回の画面更新の間に三回の表示更新をしても、配置と描画の中では配送せず、配送は一回に畳まれる。
            source.ResetAccessCounts();
            table.RefreshDisplay();
            table.RefreshDisplay();
            table.RefreshDisplay();
            table.UpdateLayout();
            Assert.AreEqual(0, source.AccessCount(0), "配置と描画の中で通知を配送している。");
            TestUiDispatcherHost.ProcessQueuedBackground();
            Assert.AreEqual(single, source.AccessCount(0), "三回の表示更新は一回の配送へまとまる。");

            GC.KeepAlive(row0);
            GC.KeepAlive(cell0);
        }, [Column("Title", 120), Column("Second", 120)]);
    }

    /// <summary>
    /// 行グループの提供元を持つ表の、グループの先頭でない行（行2・行1）のセルが、その行の値・トグル状態（チェックボックス）・選択状態（ラジオボタン）・有効状態を公開し、
    /// 操作が <c>CellActionRequested</c> を一度だけ、操作した行と列で送ることを確認します。無効のセルは <c>IsEnabled=false</c> で、操作は例外になり要求を送りません。
    /// 結合列のセルは、各行がその行の値を公開し、編集の対象になりません。提供元を持たない表の同じ列は、従来どおり値だけを公開し、トグルを公開せず、操作可否の選択子で無効にもなりません。
    /// </summary>
    /// <remarks>
    /// セルをグループの先頭行の値で公開する誤り、トグル・選択のパターンや種類（CheckBox・RadioButton）を公開しない誤り、操作で要求を送らない・二回送る誤り、
    /// 無効のセルで要求を送る・有効と公開する誤り、結合列を編集可能にする誤り、提供元のない表の公開内容を変える誤りを検出します。
    /// </remarks>
    [TestMethod]
    public void RowGroupCells_ExposeToggleSelectionAndEnabledStateOfTheirOwnRow()
    {
        CustomTableColumn[] Columns() =>
        [
            Column("Title", 80),
            new CustomTableColumn("Merged", "Merged", new CustomTableColumnSettings.ColumnLayout { Width = 80, Visibility = Visibility.Visible }, 1, "value", TextAlignment.Left,
                row => ((TestRow)row).Value, editPropertyName: "Value", mergesRowGroup: true),
            new CustomTableColumn("Pick", "Pick", new CustomTableColumnSettings.ColumnLayout { Width = 40, Visibility = Visibility.Visible }, 2, null, TextAlignment.Center,
                _ => string.Empty, cellKind: CustomTableCellKind.CheckBox, checkedSelector: _ => true, mergesRowGroup: true),
            new CustomTableColumn("Keep", "Keep", new CustomTableColumnSettings.ColumnLayout { Width = 40, Visibility = Visibility.Visible }, 3, null, TextAlignment.Center,
                _ => string.Empty, cellKind: CustomTableCellKind.RadioButton, checkedSelector: row => ((TestRow)row).Value == "value 2"),
            new CustomTableColumn("Off", "Off", new CustomTableColumnSettings.ColumnLayout { Width = 40, Visibility = Visibility.Visible }, 4, null, TextAlignment.Center,
                _ => string.Empty, cellKind: CustomTableCellKind.CheckBox, checkedSelector: _ => true, enabledSelector: _ => false),
        ];
        var requests = new List<(int Row, string Column)>();
        void Record(object? sender, CustomTableCellActionRequestedEventArgs args) => requests.Add((args.RowIndex, args.Column!.Id));

        WithTable(new GroupedRecordingSource(40, 4), (table, root) =>
        {
            table.CellActionRequested += Record;
            try
            {
                AutomationPeer pick = Cell(root, "record:2", "cell:Pick");
                Assert.AreEqual(AutomationControlType.CheckBox, pick.GetAutomationControlType());
                IToggleProvider toggle = Pattern<IToggleProvider>(pick, PatternInterface.Toggle);
                Assert.AreEqual(ToggleState.On, toggle.ToggleState);
                Assert.IsTrue(pick.IsEnabled());
                Assert.IsNull(pick.GetPattern(PatternInterface.SelectionItem));
                Assert.IsNotNull(pick.GetPattern(PatternInterface.Value));
                toggle.Toggle();
                CollectionAssert.AreEqual(new[] { (2, "Pick") }, requests, "Toggleは操作した行と列の要求を一度だけ送る");
                Assert.AreEqual(ToggleState.On, toggle.ToggleState, "状態の更新は利用先の責任で、部品は値を変えない");

                AutomationPeer keep2 = Cell(root, "record:2", "cell:Keep");
                AutomationPeer keep1 = Cell(root, "record:1", "cell:Keep");
                Assert.AreEqual(AutomationControlType.RadioButton, keep2.GetAutomationControlType());
                ISelectionItemProvider selected = Pattern<ISelectionItemProvider>(keep2, PatternInterface.SelectionItem);
                ISelectionItemProvider unselected = Pattern<ISelectionItemProvider>(keep1, PatternInterface.SelectionItem);
                Assert.IsTrue(selected.IsSelected);
                Assert.IsFalse(unselected.IsSelected);
                Assert.IsNull(keep2.GetPattern(PatternInterface.Toggle));
                requests.Clear();
                unselected.Select();
                CollectionAssert.AreEqual(new[] { (1, "Keep") }, requests, "Selectは操作した行と列の要求を一度だけ送る");
                selected.Select();
                CollectionAssert.AreEqual(new[] { (1, "Keep"), (2, "Keep") }, requests);
                Assert.ThrowsExactly<InvalidOperationException>(() => selected.RemoveFromSelection());

                requests.Clear();
                AutomationPeer off = Cell(root, "record:2", "cell:Off");
                Assert.IsFalse(off.IsEnabled(), "操作可否がfalseのセルは無効");
                Assert.AreEqual(ToggleState.On, Pattern<IToggleProvider>(off, PatternInterface.Toggle).ToggleState);
                Assert.ThrowsExactly<ElementNotEnabledException>(() => Pattern<IToggleProvider>(off, PatternInterface.Toggle).Toggle());
                Assert.AreEqual(0, requests.Count, "無効のセルは要求を送らない");

                // 結合列のセルも行ごとで、その行の値を公開し、編集できない。
                IValueProvider merged0 = Pattern<IValueProvider>(Cell(root, "record:0", "cell:Merged"), PatternInterface.Value);
                IValueProvider merged2 = Pattern<IValueProvider>(Cell(root, "record:2", "cell:Merged"), PatternInterface.Value);
                Assert.AreEqual("value 0", merged0.Value);
                Assert.AreEqual("value 2", merged2.Value);
                Assert.IsTrue(merged2.IsReadOnly, "結合列は編集の対象外");
                Assert.IsFalse(Pattern<IValueProvider>(Cell(root, "record:2", "cell:Title"), PatternInterface.Value).IsReadOnly, "通常の列は編集できる");
            }
            finally { table.CellActionRequested -= Record; }
        }, Columns());

        // 提供元のない表では、同じ列が従来どおり値だけを公開する。
        WithTable(new RecordingSource(40), (table, root) =>
        {
            foreach (string id in new[] { "Pick", "Keep", "Off" })
            {
                AutomationPeer cell = Cell(root, "record:2", "cell:" + id);
                Assert.AreEqual(AutomationControlType.Custom, cell.GetAutomationControlType(), id);
                Assert.IsNull(cell.GetPattern(PatternInterface.Toggle), id);
                Assert.IsNull(cell.GetPattern(PatternInterface.SelectionItem), id);
                Assert.IsNotNull(cell.GetPattern(PatternInterface.Value), id);
                Assert.IsTrue(cell.IsEnabled(), id);
            }
            Assert.IsFalse(Pattern<IValueProvider>(Cell(root, "record:2", "cell:Merged"), PatternInterface.Value).IsReadOnly, "提供元がなければ通常の列");
        }, Columns());
    }

    private static void WithTable(RecordingSource source, Action<CustomTableView, AutomationPeer> action, CustomTableColumn[]? columns = null)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var table = new CustomTableView { Width = 300, Height = 150, HeaderHeight = 20, RowHeight = 25, Columns = columns ?? [Column("Title", 240)], ItemsSource = source };
            var window = new Window { Width = 340, Height = 200, Content = table, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                IntPtr handle = new WindowInteropHelper(window).Handle;
                // native root接続をUI threadと別に要求し、標準のDispatcher配送待機で完了と例外を回収する。
                Task<AutomationElement> nativeRoot = Task.Run(() => AutomationElement.FromHandle(handle));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(nativeRoot, "所有表WindowのUI Automation接続");
                _ = nativeRoot.GetAwaiter().GetResult();
                AutomationPeer root = UIElementAutomationPeer.CreatePeerForElement(table) ?? throw new AssertFailedException("表peerがありません。");
                action(table, root);
            }
            finally { table.HandleKeyDown(Key.Escape, ModifierKeys.None); table.ItemsSource = null; table.DataContext = null; }
        });
    }

    private static CustomTableColumn Column(string id, int width) => new(id, id,
        new CustomTableColumnSettings.ColumnLayout { Width = width, Visibility = Visibility.Visible },
        0, "value", TextAlignment.Left, row => ((TestRow)row).Value, editPropertyName: "Value");

    private static T Pattern<T>(AutomationPeer peer, PatternInterface pattern) where T : class
        => peer.GetPattern(pattern) as T ?? throw new AssertFailedException($"{peer.GetAutomationId()} に {pattern} がありません。");

    private static AutomationPeer Cell(AutomationPeer root, string rowId, string cellId)
        => root.GetChildren().Single(child => child.GetAutomationId() == rowId).GetChildren().Single(child => child.GetAutomationId() == cellId);

    private static AutomationPeer Peer(IRawElementProviderSimple? provider)
        => provider == null ? throw new AssertFailedException("行providerがありません。") : ProviderAccess.GetPeer(provider);

    private sealed class ProviderAccess(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        internal static AutomationPeer GetPeer(IRawElementProviderSimple provider) => new ProviderAccess(new FrameworkElement()).PeerFromProvider(provider);
    }

    private sealed class TestRow(int key)
    {
        internal string Value { get; } = "value " + key.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>消費側の非実体化を記録します。実sourceのdomain ID・生存識別の保証には使いません。</summary>
    private class RecordingSource : IList, INotifyCollectionChanged, ICustomTableAutomationSource
    {
        private sealed class Token(int key) { internal int Key { get; } = key; internal object? Row; }
        private readonly List<Token> tokens;
        private readonly Dictionary<object, int> indices = new(ReferenceEqualityComparer.Instance);
        private readonly Func<int, object> factory;
        private readonly object scope;
        internal int ProjectedCount => tokens.Count(token => token.Row != null);

        /// <summary>indexer・factory・UIAプロパティを呼ばず、既に投影した生存keyだけを捕捉します。</summary>
        internal int[] GetProjectedKeysSnapshot() => tokens.Where(token => token.Row != null).Select(token => token.Key).ToArray();

        internal RecordingSource(int count, Func<int, object>? factory = null)
            : this(Enumerable.Range(0, count).Select(key => new Token(key)).ToList(), new object(), factory ?? (key => new TestRow(key))) { }
        private RecordingSource(List<Token> tokens, object scope, Func<int, object> factory)
        { this.tokens = tokens; this.scope = scope; this.factory = factory; Reindex(); }
        internal RecordingSource WithSameScope() => new([.. tokens], scope, factory);
        internal void Reverse() => Change(tokens.Reverse);
        internal void RemoveTokenAt(int index) => Change(() => tokens.RemoveAt(index));
        internal void ReplaceTokenAt(int index) => Change(() => tokens[index] = new Token(tokens[index].Key));
        private void Reindex() { indices.Clear(); for (int i = 0; i < tokens.Count; i++) { indices.Add(tokens[i], i); } }
        private void Change(Action action)
        {
            SelectionUpdateStarting?.Invoke(this, EventArgs.Empty);
            try { action(); Reindex(); CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); }
            finally { SelectionUpdateCompleted?.Invoke(this, EventArgs.Empty); }
        }
        public CustomTableAutomationItem? GetAutomationItem(int index) => index < 0 || index >= Count ? null : new(tokens[index], "record:" + tokens[index].Key.ToString(CultureInfo.InvariantCulture));
        public int IndexOfAutomationIdentity(object identity) => identity != null && indices.TryGetValue(identity, out int index) ? index : -1;
        public int FindAutomationItem(string id, int after) => tokens.FindIndex(Math.Min(Count, Math.Max(0, after + 1)), token => string.Equals("record:" + token.Key.ToString(CultureInfo.InvariantCulture), id, StringComparison.Ordinal));
        public object? GetSelectionIdentity(int index) => GetAutomationItem(index)?.Identity;
        public int IndexOfSelectionIdentity(object identity) => IndexOfAutomationIdentity(identity);
        public bool SharesSelectionIdentityScope(ICustomTableSelectionIdentitySource other) => other is RecordingSource source && ReferenceEquals(scope, source.scope);
        public event EventHandler? SelectionUpdateStarting;
        public event EventHandler? SelectionUpdateCompleted;
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public int Count => tokens.Count;
        private readonly Dictionary<int, int> accessCounts = [];
        /// <summary>indexerで読んだ回数を位置ごとに返します。識別だけを使う照会は数えません。</summary>
        internal int AccessCount(int index) => accessCounts.GetValueOrDefault(index);
        internal void ResetAccessCounts() => accessCounts.Clear();
        public object? this[int index]
        {
            get { accessCounts[index] = accessCounts.GetValueOrDefault(index) + 1; return tokens[index].Row ??= factory(tokens[index].Key); }
            set => throw new NotSupportedException();
        }
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public IEnumerator GetEnumerator() => throw new NotSupportedException("全行列挙は禁止です。");
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? value) => throw new NotSupportedException();
        public int IndexOf(object? value) => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
    }

    /// <summary>連続した同じ大きさのグループを返す、行グループの提供元を持つ行集合です。提供元を持たない <see cref="RecordingSource"/> とは別の型にして、既存のケースの表を提供元なしのままにします。</summary>
    private sealed class GroupedRecordingSource(int count, int groupSize) : RecordingSource(count), ICustomTableRowGroupSource
    {
        public CustomTableRowGroup GetRowGroup(int rowIndex)
        {
            int first = rowIndex / groupSize * groupSize;
            return new CustomTableRowGroup(first, Math.Min(groupSize, Count - first), rowIndex / groupSize);
        }
    }
}
