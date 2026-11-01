using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            scroll.SetScrollPercent(100d, 100d);
            Assert.AreEqual(100d, scroll.HorizontalScrollPercent, 0.001d);
            Assert.AreEqual(100d, scroll.VerticalScrollPercent, 0.001d);
            Assert.IsTrue(table.HorizontalOffset > 0d);
            Assert.IsTrue(table.FirstVisibleRowIndex > 90);
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
            table.CellEditEnded += (_, request) => owner.RequestCellEditEnded(request.Row, request.EditPropertyName, request.Text, request.Commit);
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
    private sealed class RecordingSource : IList, INotifyCollectionChanged, ICustomTableAutomationSource
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
        public object? this[int index] { get => tokens[index].Row ??= factory(tokens[index].Key); set => throw new NotSupportedException(); }
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
}
