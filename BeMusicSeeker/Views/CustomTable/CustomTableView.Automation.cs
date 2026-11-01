using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public sealed partial class CustomTableView
{
    private CustomTableAutomationPeer automationPeer;

    /// <summary>描画だけの表へ、可視範囲と明示要求の対象だけを持つUI Automation窓口を接続します。</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => automationPeer ??= new CustomTableAutomationPeer(this);

    private void NotifyAutomationChanged(string reason)
    {
        // 並べ替え途中の一時的な不在ではなく、識別復元を終えた最終集合で失効を判断する。
        if (automationPeer != null && !selectionIdentities.HasValue)
        {
            automationPeer.NotifyChanged(reason);
        }
    }

    /// <summary>既存の編集能力を照会します。実開始イベントや編集受付の保存は行いません。</summary>
    private bool CanEditAutomationCell(int rowIndex, CustomTableColumn column)
    {
        if (!IsEnabled || column == null || string.IsNullOrWhiteSpace(column.EditPropertyName)) { return false; }
        object row = ItemsSource[rowIndex];
        if (column.EditPropertyName == "memo" && row is not PlaylistDetailRow) { return false; }
        return DataContext switch
        {
            MainChartListViewModel main => main.CanBeginCellEdit(row, column.EditPropertyName),
            PlaylistWorkspaceViewModel workspace when row is PlaylistSummaryRow summary
                => workspace.CanBeginSummaryPropertyEdit(summary, column.EditPropertyName),
            _ when row is PlaylistDetailRow => GridRowResolver.CanEditPlaylistCell(row, column.EditPropertyName),
            _ => true
        };
    }

    private Rect AutomationRowBounds(int index)
    {
        double y = HeaderHeight + (index - FirstVisibleRowIndex) * RowHeight;
        var bounds = new Rect(0d, y, Math.Max(0d, horizontalScrollBar.ViewportSize), Math.Max(0d, RowHeight));
        bounds.Intersect(new Rect(0d, HeaderHeight, Math.Max(0d, horizontalScrollBar.ViewportSize),
            Math.Max(0d, SurfaceHeight - HeaderHeight - (horizontalScrollBar.Visibility == Visibility.Visible ? ScrollBarThickness : 0d))));
        return bounds;
    }

    private Rect AutomationColumnBounds(CustomTableColumn column, int? rowIndex)
    {
        foreach (CustomTableColumnLayoutEntry entry in GetColumnLayoutSnapshot().Entries)
        {
            if (!ReferenceEquals(entry.Column, column)) { continue; }
            var bounds = new Rect(entry.ScreenX, rowIndex.HasValue
                ? HeaderHeight + (rowIndex.Value - FirstVisibleRowIndex) * RowHeight : 0d,
                Math.Max(0d, entry.Width), rowIndex.HasValue ? Math.Max(0d, RowHeight) : Math.Max(0d, HeaderHeight));
            bounds.Intersect(rowIndex.HasValue ? AutomationRowBounds(rowIndex.Value)
                : new Rect(0d, 0d, Math.Max(0d, horizontalScrollBar.ViewportSize), Math.Max(0d, HeaderHeight)));
            return bounds;
        }
        return Rect.Empty;
    }

    private void SelectAutomationRow(int index, bool? add)
    {
        selectionModel.SetItemCount(RowCount);
        bool changed = !add.HasValue ? selectionModel.SelectSingle(index)
            : add.Value == selectionModel.IsSelected(index) ? false : selectionModel.Toggle(index);
        UpdateSelectedIndexFromSelectionModel();
        EnsureCurrentCellForCurrentRow();
        if (changed) { RaiseSelectionChanged(); }
        RequestRedraw("selection");
    }

    /// <summary>論理行の識別を外部providerへ対応付けます。全行peerや実体行の事前作成はしません。</summary>
    private sealed class CustomTableAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider, IScrollProvider, IItemContainerProvider
    {
        private readonly Dictionary<object, WeakReference<CustomTableRowAutomationPeer>> requestedRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, WeakReference<IRawElementProviderSimple>> requestedProviders = new(ReferenceEqualityComparer.Instance);
        private readonly List<CustomTableRowAutomationPeer> pendingProviderRows = [];
        private readonly Dictionary<string, CustomTableHeaderAutomationPeer> headers = new(StringComparer.Ordinal);
        private bool notifying;
        private double lastHorizontalPercent = ScrollPatternIdentifiers.NoScroll;
        private double lastVerticalPercent = ScrollPatternIdentifiers.NoScroll;

        /// <summary>表の描画・選択・スクロールの既存所有者を参照します。</summary>
        internal CustomTableAutomationPeer(CustomTableView table) : base(table) { Table = table; }

        /// <summary>状態と実editorを所有する表です。</summary>
        internal CustomTableView Table { get; }

        protected override string GetClassNameCore() => nameof(CustomTableView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        public override object GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Selection or PatternInterface.Scroll or PatternInterface.ItemContainer => this,
            _ => base.GetPattern(patternInterface)
        };

        protected override List<AutomationPeer> GetChildrenCore()
        {
            List<AutomationPeer> children = [];
            foreach (CustomTableColumnLayoutEntry entry in Table.GetColumnLayoutSnapshot().VisibleEntries)
            {
                if (!headers.TryGetValue(entry.Column.Id, out CustomTableHeaderAutomationPeer header))
                {
                    header = new CustomTableHeaderAutomationPeer(this, entry.Column.Id);
                    headers.Add(entry.Column.Id, header);
                }
                children.Add(header);
            }
            int last = Math.Min(Table.RowCount, Table.FirstVisibleRowIndex + Table.CalculateDrawableRowCapacity());
            HashSet<CustomTableRowAutomationPeer> rows = [];
            for (int index = Table.FirstVisibleRowIndex; index < last; index++)
            {
                if (RowAt(index) is CustomTableRowAutomationPeer row) { rows.Add(row); }
            }
            foreach ((object identity, WeakReference<IRawElementProviderSimple> provider) in requestedProviders)
            {
                if (provider.TryGetTarget(out _) && requestedRows.TryGetValue(identity, out WeakReference<CustomTableRowAutomationPeer> weak)
                    && weak.TryGetTarget(out CustomTableRowAutomationPeer row) && row.IsAvailable()) { rows.Add(row); }
            }
            foreach (CustomTableRowAutomationPeer row in pendingProviderRows)
            {
                if (row.IsAvailable()) { rows.Add(row); }
            }
            children.AddRange(rows.OrderBy(row => row.Index));
            return children;
        }

        /// <summary>要求位置の寿命識別だけを読み、行投影を作らず既存の生存peerを返します。</summary>
        internal CustomTableRowAutomationPeer RowAt(int index)
        {
            if (Table.ItemsSource is not ICustomTableAutomationSource source
                || source.GetAutomationItem(index) is not CustomTableAutomationItem item) { return null; }
            if (requestedRows.TryGetValue(item.Identity, out WeakReference<CustomTableRowAutomationPeer> weak)
                && weak.TryGetTarget(out CustomTableRowAutomationPeer existing) && existing.IsAvailable()) { return existing; }
            var peer = new CustomTableRowAutomationPeer(this, source, item.Identity);
            requestedRows[item.Identity] = new WeakReference<CustomTableRowAutomationPeer>(peer);
            return peer;
        }

        /// <summary>論理peerをWPFのproviderへ変換します。</summary>
        internal IRawElementProviderSimple Provider(AutomationPeer peer) => ProviderFromPeer(peer);

        /// <summary>今回返す対象を一括して親へ接続します。返却後の寿命判定はWPFの強いchildren cacheではなくproviderの弱参照を使います。</summary>
        private IRawElementProviderSimple[] CreateRowProviders(CustomTableRowAutomationPeer[] rows)
        {
            pendingProviderRows.AddRange(rows);
            try
            {
                ResetChildrenCache();
                var result = new IRawElementProviderSimple[rows.Length];
                for (int index = 0; index < rows.Length; index++)
                {
                    IRawElementProviderSimple provider = ProviderFromPeer(rows[index])
                        ?? throw new InvalidOperationException("The table automation peer is not connected to its window.");
                    result[index] = provider;
                    requestedProviders[rows[index].Identity] = new WeakReference<IRawElementProviderSimple>(provider);
                }
                return result;
            }
            finally
            {
                // 接続したplaceholderを直後に除去しない。次の要求または表示更新で生存providerを照合する。
                pendingProviderRows.Clear();
            }
        }

        private void PruneRequests()
        {
            foreach (object identity in requestedProviders.Keys.ToArray())
            {
                if (!requestedProviders[identity].TryGetTarget(out _)
                    || !requestedRows.TryGetValue(identity, out WeakReference<CustomTableRowAutomationPeer> weak)
                    || !weak.TryGetTarget(out CustomTableRowAutomationPeer row) || !row.IsAvailable())
                { requestedProviders.Remove(identity); }
            }
            foreach (object identity in requestedRows.Keys.ToArray())
            {
                if (!requestedRows[identity].TryGetTarget(out CustomTableRowAutomationPeer row) || !row.IsAvailable())
                { requestedRows.Remove(identity); requestedProviders.Remove(identity); }
            }
        }

        /// <summary>列IDを現在の列定義へ解決します。隠れた列を別列へ補完しません。</summary>
        internal CustomTableColumn Column(string id) => Table.VisibleColumns.FirstOrDefault(column => string.Equals(column.Id, id, StringComparison.Ordinal));

        /// <summary>要求済みpeerだけを照合し、死んだweak参照の識別キーを回収して実変更を通知します。</summary>
        internal void NotifyChanged(string reason)
        {
            if (notifying) { return; }
            notifying = true;
            try
            {
                PruneRequests();
                foreach (WeakReference<CustomTableRowAutomationPeer> weak in requestedRows.Values.ToArray())
                {
                    if (weak.TryGetTarget(out CustomTableRowAutomationPeer row)) { row.NotifyChanged(); }
                }
                foreach (string columnId in headers.Keys.Where(id => Column(id) == null).ToArray()) { headers.Remove(columnId); }
                ResetChildrenCache();
                InvalidatePeer();
                if (reason == "selection") { RaiseAutomationEvent(AutomationEvents.SelectionPatternOnInvalidated); }
                double horizontal = HorizontalScrollPercent;
                double vertical = VerticalScrollPercent;
                if (horizontal != lastHorizontalPercent)
                {
                    RaisePropertyChangedEvent(ScrollPatternIdentifiers.HorizontalScrollPercentProperty, lastHorizontalPercent, horizontal);
                    lastHorizontalPercent = horizontal;
                }
                if (vertical != lastVerticalPercent)
                {
                    RaisePropertyChangedEvent(ScrollPatternIdentifiers.VerticalScrollPercentProperty, lastVerticalPercent, vertical);
                    lastVerticalPercent = vertical;
                }
            }
            finally { notifying = false; }
        }

        bool ISelectionProvider.CanSelectMultiple => true;
        bool ISelectionProvider.IsSelectionRequired => false;

        IRawElementProviderSimple[] ISelectionProvider.GetSelection()
        {
            PruneRequests();
            return CreateRowProviders(Table.selectionModel.SelectedIndices.Select(RowAt).Where(row => row != null).ToArray());
        }

        IRawElementProviderSimple IItemContainerProvider.FindItemByProperty(IRawElementProviderSimple startAfter, int propertyId, object value)
        {
            PruneRequests();
            if (Table.ItemsSource is not ICustomTableAutomationSource source) { return null; }
            int after = -1;
            if (startAfter != null)
            {
                if (PeerFromProvider(startAfter) is not CustomTableRowAutomationPeer row || !ReferenceEquals(row.Root, this))
                { throw new ArgumentException("startAfter"); }
                after = row.Index;
            }
            int index;
            if (propertyId == 0) { index = after + 1; }
            else if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id && value is string id)
            { index = source.FindAutomationItem(id, after); }
            else { throw new ArgumentException("propertyId"); }
            CustomTableRowAutomationPeer found = RowAt(index);
            IRawElementProviderSimple[] providers = CreateRowProviders(found == null ? [] : [found]);
            return providers.Length == 0 ? null : providers[0];
        }

        /// <summary>実際に水平移動できる場合だけtrueです。</summary>
        public bool HorizontallyScrollable => Table.horizontalScrollBar.Visibility == Visibility.Visible;
        /// <summary>実際に垂直移動できる場合だけtrueです。</summary>
        public bool VerticallyScrollable => Table.verticalScrollBar.Visibility == Visibility.Visible;
        /// <summary>既存水平offsetの移動可能範囲に対する百分率です。</summary>
        public double HorizontalScrollPercent => Percent(Table.horizontalScrollBar, HorizontallyScrollable);
        /// <summary>既存先頭行位置の移動可能範囲に対する百分率です。</summary>
        public double VerticalScrollPercent => Percent(Table.verticalScrollBar, VerticallyScrollable);
        /// <summary>水平の有効viewportが全幅に占める百分率です。</summary>
        public double HorizontalViewSize => ViewSize(Table.horizontalScrollBar, HorizontallyScrollable);
        /// <summary>完全表示行容量が全行数に占める百分率です。</summary>
        public double VerticalViewSize => ViewSize(Table.verticalScrollBar, VerticallyScrollable);

        private static double Percent(ScrollBar bar, bool scrollable) => scrollable && bar.Maximum > 0d
            ? 100d * bar.Value / bar.Maximum : ScrollPatternIdentifiers.NoScroll;

        private static double ViewSize(ScrollBar bar, bool scrollable) => scrollable
            ? 100d * bar.ViewportSize / (bar.Maximum + bar.ViewportSize) : 100d;

        void IScrollProvider.Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        {
            EnsureEnabled();
            ValidateAmount(horizontalAmount, HorizontallyScrollable);
            ValidateAmount(verticalAmount, VerticallyScrollable);
            Move(Table.horizontalScrollBar, horizontalAmount);
            Move(Table.verticalScrollBar, verticalAmount);
        }

        private static void ValidateAmount(ScrollAmount amount, bool scrollable)
        {
            if (!Enum.IsDefined(amount)) { throw new ArgumentOutOfRangeException(nameof(amount)); }
            if (!scrollable && amount != ScrollAmount.NoAmount) { throw new InvalidOperationException(); }
        }

        private static void Move(ScrollBar bar, ScrollAmount amount)
        {
            double delta = amount switch
            {
                ScrollAmount.LargeDecrement => -bar.LargeChange,
                ScrollAmount.SmallDecrement => -bar.SmallChange,
                ScrollAmount.LargeIncrement => bar.LargeChange,
                ScrollAmount.SmallIncrement => bar.SmallChange,
                _ => 0d
            };
            bar.Value = Math.Clamp(bar.Value + delta, 0d, bar.Maximum);
        }

        void IScrollProvider.SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            EnsureEnabled();
            ValidatePercent(horizontalPercent, HorizontallyScrollable);
            ValidatePercent(verticalPercent, VerticallyScrollable);
            if (horizontalPercent != ScrollPatternIdentifiers.NoScroll) { Table.horizontalScrollBar.Value = Table.horizontalScrollBar.Maximum * horizontalPercent / 100d; }
            if (verticalPercent != ScrollPatternIdentifiers.NoScroll) { Table.verticalScrollBar.Value = Table.verticalScrollBar.Maximum * verticalPercent / 100d; }
        }

        private static void ValidatePercent(double value, bool scrollable)
        {
            if (value == ScrollPatternIdentifiers.NoScroll) { return; }
            if (!double.IsFinite(value) || value < 0d || value > 100d) { throw new ArgumentOutOfRangeException(nameof(value)); }
            if (!scrollable) { throw new InvalidOperationException(); }
        }

        /// <summary>無効な表からの操作要求をUI Automationの無効状態として拒否します。</summary>
        internal void EnsureEnabled()
        {
            if (!Table.IsEnabled) { throw new ElementNotEnabledException(); }
        }
    }

    /// <summary>描画された論理要素の画面座標と操作可能性を、表の実レイアウトへ結び付けます。</summary>
    private abstract class CustomTableLogicalAutomationPeer(CustomTableAutomationPeer root) : AutomationPeer
    {
        /// <summary>論理要素の接続先と操作対象を所有する表peerです。</summary>
        internal CustomTableAutomationPeer Root { get; } = root;
        /// <summary>表の実レイアウトで切り取った、表内DIP単位の領域です。</summary>
        protected abstract Rect Bounds { get; }
        /// <summary>対象の寿命・列を照合し、消失済みのprovider操作を拒否します。</summary>
        protected abstract void CheckAvailable();
        protected override Rect GetBoundingRectangleCore()
        {
            CheckAvailable();
            Rect bounds = Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0d || bounds.Height <= 0d || !Root.Table.IsVisible
                || PresentationSource.FromVisual(Root.Table) == null) { return Rect.Empty; }
            return new Rect(Root.Table.PointToScreen(bounds.TopLeft), Root.Table.PointToScreen(bounds.BottomRight));
        }
        protected override Point GetClickablePointCore()
        {
            Rect bounds = GetBoundingRectangleCore();
            return bounds.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(bounds.Left + bounds.Width / 2d, bounds.Top + bounds.Height / 2d);
        }
        protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;
        protected override bool IsEnabledCore() { CheckAvailable(); return Root.Table.IsEnabled; }
        protected override bool IsKeyboardFocusableCore() { CheckAvailable(); return true; }
        protected override bool HasKeyboardFocusCore() { CheckAvailable(); return Root.Table.IsKeyboardFocusWithin; }
        protected override void SetFocusCore() { CheckAvailable(); Root.EnsureEnabled(); Root.Table.Focus(); }
        protected override bool IsControlElementCore() { CheckAvailable(); return true; }
        protected override bool IsContentElementCore() { CheckAvailable(); return true; }
        protected override string GetAcceleratorKeyCore() { CheckAvailable(); return string.Empty; }
        protected override string GetAccessKeyCore() { CheckAvailable(); return string.Empty; }
        protected override string GetHelpTextCore() { CheckAvailable(); return string.Empty; }
        protected override string GetItemStatusCore() { CheckAvailable(); return string.Empty; }
        protected override string GetItemTypeCore() { CheckAvailable(); return string.Empty; }
        protected override AutomationPeer GetLabeledByCore() { CheckAvailable(); return null; }
        protected override AutomationOrientation GetOrientationCore() { CheckAvailable(); return AutomationOrientation.None; }
        protected override bool IsPasswordCore() { CheckAvailable(); return false; }
        protected override bool IsRequiredForFormCore() { CheckAvailable(); return false; }
    }

    /// <summary>行の寿命識別を毎操作で照合し、並べ替え後も同じ対象だけを操作します。</summary>
    private sealed class CustomTableRowAutomationPeer : CustomTableLogicalAutomationPeer, ISelectionItemProvider, IScrollItemProvider, IVirtualizedItemProvider
    {
        private ICustomTableAutomationSource source;
        private readonly object identity;
        private readonly Dictionary<string, CustomTableCellAutomationPeer> cells = new(StringComparer.Ordinal);
        private bool unavailable;
        private bool wasSelected;

        /// <summary>providerの弱参照をドメイン文字列と区別して対応付ける生存識別です。</summary>
        internal object Identity => identity;

        /// <summary>同じscopeの生存識別だけを捕捉し、添字・domain IDは現在sourceから取得します。</summary>
        internal CustomTableRowAutomationPeer(CustomTableAutomationPeer root, ICustomTableAutomationSource source, object identity) : base(root)
        {
            this.source = source;
            this.identity = identity;
            wasSelected = root.Table.IsRowSelected(Index);
        }

        /// <summary>捕捉した対象の現在位置です。消失または別scopeへの移動後は再利用しません。</summary>
        internal int Index
        {
            get
            {
                if (!IsAvailable()) { throw new ElementNotAvailableException(); }
                return source.IndexOfAutomationIdentity(identity);
            }
        }

        /// <summary>同scopeかつ同じ生存対象がある場合だけ参照sourceを引き継ぎます。</summary>
        internal bool IsAvailable()
        {
            if (unavailable) { return false; }
            if (Root.Table.ItemsSource is not ICustomTableAutomationSource current
                || (!ReferenceEquals(source, current) && !source.SharesSelectionIdentityScope(current))
                || current.IndexOfAutomationIdentity(identity) < 0)
            {
                unavailable = true;
                source = null;
                return false;
            }
            source = current;
            return true;
        }

        protected override void CheckAvailable() { _ = Index; }
        protected override Rect Bounds => Root.Table.AutomationRowBounds(Index);
        protected override string GetAutomationIdCore()
        {
            int index = Index;
            return source.GetAutomationItem(index)?.AutomationId ?? throw new ElementNotAvailableException();
        }
        protected override string GetNameCore()
        {
            // 画面外placeholderの名前照会だけでは行を生成しない。Realize後は実際の表示名を返す。
            Rect bounds = Bounds;
            if (!Root.Table.IsVisible || bounds.IsEmpty || bounds.Width <= 0d || bounds.Height <= 0d) { return string.Empty; }
            object row = Root.Table.ItemsSource[Index];
            CustomTableColumn title = Root.Table.VisibleColumns.FirstOrDefault(column => column.Id is "Title" or "Name");
            return title?.GetText(row) ?? GridRowResolver.GetDisplayTitle(row);
        }
        protected override string GetClassNameCore() { CheckAvailable(); return "CustomTableRow"; }
        protected override AutomationControlType GetAutomationControlTypeCore() { CheckAvailable(); return AutomationControlType.ListItem; }
        public override object GetPattern(PatternInterface patternInterface)
        {
            CheckAvailable();
            return patternInterface is PatternInterface.SelectionItem or PatternInterface.ScrollItem or PatternInterface.VirtualizedItem ? this : null;
        }
        protected override List<AutomationPeer> GetChildrenCore()
        {
            CheckAvailable();
            if (Bounds.IsEmpty) { return []; }
            List<AutomationPeer> children = [];
            foreach (CustomTableColumnLayoutEntry entry in Root.Table.GetColumnLayoutSnapshot().VisibleEntries)
            {
                if (!cells.TryGetValue(entry.Column.Id, out CustomTableCellAutomationPeer cell))
                {
                    cell = new CustomTableCellAutomationPeer(this, entry.Column.Id);
                    cells.Add(entry.Column.Id, cell);
                }
                children.Add(cell);
            }
            return children;
        }
        /// <summary>実選択の変更と、要求済みセルの値・子editorの変更を公開します。</summary>
        internal void NotifyChanged()
        {
            bool selected = Root.Table.IsRowSelected(Index);
            if (selected != wasSelected)
            {
                RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, wasSelected, selected);
                RaiseAutomationEvent(selected ? AutomationEvents.SelectionItemPatternOnElementAddedToSelection : AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection);
                wasSelected = selected;
            }
            foreach (string columnId in cells.Keys.ToArray())
            {
                if (Root.Column(columnId) == null) { cells[columnId].Invalidate(); cells.Remove(columnId); }
                else { cells[columnId].NotifyChanged(); }
            }
            ResetChildrenCache();
        }

        bool ISelectionItemProvider.IsSelected => Root.Table.IsRowSelected(Index);
        IRawElementProviderSimple ISelectionItemProvider.SelectionContainer { get { CheckAvailable(); return Root.Provider(Root); } }
        void ISelectionItemProvider.Select() => Select(null);
        void ISelectionItemProvider.AddToSelection() => Select(true);
        void ISelectionItemProvider.RemoveFromSelection() => Select(false);
        void IScrollItemProvider.ScrollIntoView() => Realize();
        void IVirtualizedItemProvider.Realize() => Realize();

        private void Select(bool? add)
        {
            CheckAvailable();
            Root.EnsureEnabled();
            Root.Table.CommitActiveEdit();
            Root.Table.SelectAutomationRow(Index, add);
        }

        private void Realize()
        {
            CheckAvailable();
            Root.EnsureEnabled();
            Root.Table.CommitActiveEdit();
            Root.Table.EnsureRowVisible(Index);
            _ = Root.Table.ItemsSource[Index];
            Root.Table.RequestRedraw("automation_realize");
        }
    }

    /// <summary>列IDと生存行を使い、表示値と既存TextBox経由の一回の編集要求を提供します。</summary>
    private sealed class CustomTableCellAutomationPeer : CustomTableLogicalAutomationPeer, IValueProvider
    {
        private readonly CustomTableRowAutomationPeer row;
        private readonly string columnId;
        private bool unavailable;
        private string lastValue;
        private bool lastReadOnly;

        /// <summary>可視セルの初期値とpureな編集可否を捕捉し、プロパティの事前照会なしでも後続変更を通知します。</summary>
        internal CustomTableCellAutomationPeer(CustomTableRowAutomationPeer row, string columnId) : base(row.Root)
        {
            this.row = row;
            this.columnId = columnId;
            lastValue = ReadValue();
            lastReadOnly = ReadIsReadOnly();
        }

        private CustomTableColumn Column
        {
            get
            {
                _ = row.Index;
                if (unavailable || Root.Column(columnId) is not CustomTableColumn column) { unavailable = true; throw new ElementNotAvailableException(); }
                return column;
            }
        }
        protected override void CheckAvailable() { _ = Column; }
        /// <summary>消えた列の旧セルを、同IDの後発列にも再利用しません。</summary>
        internal void Invalidate() => unavailable = true;
        protected override Rect Bounds => Root.Table.AutomationColumnBounds(Column, row.Index);
        protected override string GetAutomationIdCore() { CheckAvailable(); return "cell:" + columnId; }
        protected override string GetNameCore() => Column.Header ?? string.Empty;
        protected override string GetClassNameCore() { CheckAvailable(); return "CustomTableCell"; }
        protected override AutomationControlType GetAutomationControlTypeCore() { CheckAvailable(); return AutomationControlType.Custom; }
        public override object GetPattern(PatternInterface patternInterface) { CheckAvailable(); return patternInterface == PatternInterface.Value ? this : null; }
        protected override List<AutomationPeer> GetChildrenCore()
        {
            CustomTableView table = Root.Table;
            CheckAvailable();
            if (table.activeEditor != null && table.activeEditHit?.RowIndex == row.Index
                && string.Equals(table.activeEditHit.Column?.Id, columnId, StringComparison.Ordinal)
                && UIElementAutomationPeer.CreatePeerForElement(table.activeEditor) is AutomationPeer editor) { return [editor]; }
            return [];
        }
        // 読取りで通知用snapshotを進めない。更新通知より先に現在値を照会されても変更を取りこぼさない。
        bool IValueProvider.IsReadOnly => ReadIsReadOnly();
        string IValueProvider.Value => ReadValue();
        private bool ReadIsReadOnly() => !Root.Table.CanEditAutomationCell(row.Index, Column);
        private string ReadValue() => Root.Table.GetCellValue(Root.Table.ItemsSource[row.Index], Column, out _).Text;
        void IValueProvider.SetValue(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            CustomTableColumn column = Column;
            Root.EnsureEnabled();
            if (!Root.Table.CanEditAutomationCell(row.Index, column)) { throw new InvalidOperationException(); }
            CustomTableView table = Root.Table;
            // 既存editorの終端で同期整列が起こり得るため、終端後に同じ寿命識別から位置を取り直す。
            table.CommitActiveEdit();
            column = Column;
            table.EnsureRowVisible(row.Index);
            CustomTableColumnLayoutEntry entry = table.GetColumnLayoutSnapshot().Entries.First(item => ReferenceEquals(item.Column, column));
            table.EnsureColumnVisible(entry, 0);
            if (!table.TryCreateCellHit(row.Index, columnId, out CustomTableHitTestResult hit) || !table.BeginCellEdit(hit, null))
            { throw new InvalidOperationException(); }
            if (table.activeEditor == null) { throw new ElementNotAvailableException(); }
            table.activeEditor.Text = value;
            table.CommitActiveEdit();
        }
        /// <summary>生成時または前回通知時からの表示値・readonlyの変化と、実editorの出入りを通知します。</summary>
        internal void NotifyChanged()
        {
            string beforeValue = lastValue;
            string afterValue = ReadValue();
            lastValue = afterValue;
            if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            { RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, beforeValue, afterValue); }
            bool beforeReadOnly = lastReadOnly;
            bool afterReadOnly = ReadIsReadOnly();
            lastReadOnly = afterReadOnly;
            if (beforeReadOnly != afterReadOnly)
            { RaisePropertyChangedEvent(ValuePatternIdentifiers.IsReadOnlyProperty, beforeReadOnly, afterReadOnly); }
            ResetChildrenCache();
        }
    }

    /// <summary>実クリックと同じ列見出しの位置・名称・並べ替え要求を公開します。</summary>
    private sealed class CustomTableHeaderAutomationPeer(CustomTableAutomationPeer root, string columnId) : CustomTableLogicalAutomationPeer(root), IInvokeProvider
    {
        private CustomTableColumn Column => Root.Column(columnId) ?? throw new ElementNotAvailableException();
        protected override void CheckAvailable() { _ = Column; }
        protected override Rect Bounds => Root.Table.AutomationColumnBounds(Column, null);
        protected override string GetAutomationIdCore() { CheckAvailable(); return "header:" + columnId; }
        protected override string GetNameCore() => Column.Header ?? string.Empty;
        protected override string GetClassNameCore() { CheckAvailable(); return "CustomTableHeader"; }
        protected override AutomationControlType GetAutomationControlTypeCore() { CheckAvailable(); return AutomationControlType.HeaderItem; }
        protected override List<AutomationPeer> GetChildrenCore() { CheckAvailable(); return []; }
        public override object GetPattern(PatternInterface patternInterface)
        {
            CheckAvailable();
            return patternInterface == PatternInterface.Invoke && !string.IsNullOrWhiteSpace(Column.SortMemberPath) ? this : null;
        }
        void IInvokeProvider.Invoke() { Root.EnsureEnabled(); Root.Table.RequestSort(Column); }
    }
}
