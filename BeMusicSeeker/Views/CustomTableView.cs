using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public sealed class CustomTableFirstRenderCompletedEventArgs : EventArgs
{
    internal CustomTableFirstRenderCompletedEventArgs(int rowCount, int visibleRowCount, int visibleColumnCount, int visibleCellCount, long firstRenderMs, long renderWorkMs, double textCacheHitRate)
    {
        RowCount = rowCount;
        VisibleRowCount = visibleRowCount;
        VisibleColumnCount = visibleColumnCount;
        VisibleCellCount = visibleCellCount;
        FirstRenderMs = firstRenderMs;
        RenderWorkMs = renderWorkMs;
        TextCacheHitRate = textCacheHitRate;
    }

    public int RowCount { get; }

    public int VisibleRowCount { get; }

    public int VisibleColumnCount { get; }

    public int VisibleCellCount { get; }

    public long FirstRenderMs { get; }

    public long RenderWorkMs { get; }

    public double TextCacheHitRate { get; }
}

public sealed class CustomTableView : Grid
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IList),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(IList<CustomTableColumn>),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnColumnsChanged));

    public static readonly DependencyProperty ColumnsSettingsProperty = DependencyProperty.Register(
        nameof(ColumnsSettings),
        typeof(dataGridColumnsSettings),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, OnColumnsSettingsChanged));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight),
        typeof(double),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(19d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutMetricChanged));

    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight),
        typeof(double),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(22d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutMetricChanged));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedIndexChanged));

    public static readonly DependencyProperty SortColumnNameProperty = DependencyProperty.Register(
        nameof(SortColumnName),
        typeof(string),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSortStateChanged));

    public static readonly DependencyProperty SortDirectionProperty = DependencyProperty.Register(
        nameof(SortDirection),
        typeof(ListSortDirection?),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSortStateChanged));

    private readonly CustomTableSurface surface;
    private readonly ScrollBar verticalScrollBar;
    private readonly CustomTableSelectionModel selectionModel = new CustomTableSelectionModel();
    private readonly List<INotifyPropertyChanged> subscribedColumnLayouts = new List<INotifyPropertyChanged>();
    private INotifyCollectionChanged itemsCollectionChanged;
    private long itemsAppliedTimestamp;
    private bool firstRenderLogged = true;
    private bool updatingSelectedIndexFromSelection;

    public CustomTableView()
    {
        ClipToBounds = true;
        Focusable = true;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        surface = new CustomTableSurface(this);
        verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = SystemParameters.VerticalScrollBarWidth,
            Minimum = 0d,
            SmallChange = 3d
        };
        verticalScrollBar.ValueChanged += VerticalScrollBarValueChanged;
        Children.Add(surface);
        Children.Add(verticalScrollBar);
        SetColumn(surface, 0);
        SetColumn(verticalScrollBar, 1);
        SizeChanged += delegate
        {
            UpdateScrollBar();
            surface.InvalidateVisual();
        };
        PreviewMouseLeftButtonDown += CustomTableViewPreviewMouseLeftButtonDown;
        PreviewMouseRightButtonDown += CustomTableViewPreviewMouseRightButtonDown;
        PreviewKeyDown += CustomTableViewPreviewKeyDown;
        IsVisibleChanged += delegate
        {
            if (IsVisible)
            {
                MarkItemsApplied();
                UpdateScrollBar();
                surface.InvalidateVisual();
            }
        };
    }

    public event EventHandler<CustomTableFirstRenderCompletedEventArgs> FirstRenderCompleted;

    public event EventHandler<CustomTableSortRequestedEventArgs> SortRequested;

    public event EventHandler<CustomTableSelectionChangedEventArgs> SelectionChanged;

    public event EventHandler<CustomTableRowRequestedEventArgs> RowActivated;

    public event EventHandler<CustomTableRowRequestedEventArgs> RowContextMenuRequested;

    public event EventHandler<CustomTableHeaderRequestedEventArgs> HeaderContextMenuRequested;

    public IList ItemsSource
    {
        get => (IList)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IList<CustomTableColumn> Columns
    {
        get => (IList<CustomTableColumn>)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public dataGridColumnsSettings ColumnsSettings
    {
        get => (dataGridColumnsSettings)GetValue(ColumnsSettingsProperty);
        set => SetValue(ColumnsSettingsProperty, value);
    }

    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public double HeaderHeight
    {
        get => (double)GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public string SortColumnName
    {
        get => (string)GetValue(SortColumnNameProperty);
        set => SetValue(SortColumnNameProperty, value);
    }

    public ListSortDirection? SortDirection
    {
        get => (ListSortDirection?)GetValue(SortDirectionProperty);
        set => SetValue(SortDirectionProperty, value);
    }

    internal int FirstVisibleRowIndex => (int)Math.Max(0d, Math.Floor(verticalScrollBar.Value));

    internal IReadOnlyList<CustomTableColumn> VisibleColumns => Columns as IReadOnlyList<CustomTableColumn> ?? Columns?.ToArray() ?? Array.Empty<CustomTableColumn>();

    internal int RowCount => ItemsSource?.Count ?? 0;

    internal double SurfaceWidth => surface.ActualWidth;

    internal double SurfaceHeight => surface.ActualHeight;

    internal bool IsRowSelected(int rowIndex)
    {
        return selectionModel.IsSelected(rowIndex);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.DetachCollectionChanged(e.OldValue as INotifyCollectionChanged);
        view.AttachCollectionChanged(e.NewValue as INotifyCollectionChanged);
        view.MarkItemsApplied();
        view.CoerceSelectionToCurrentRows();
        view.UpdateScrollBar();
        view.surface.InvalidateVisual();
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.UpdateScrollBar();
        view.surface.InvalidateVisual();
    }

    private static void OnColumnsSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.DetachColumnLayoutHandlers();
        view.AttachColumnLayoutHandlers(e.NewValue as dataGridColumnsSettings);
        view.RebuildColumns();
    }

    private static void OnLayoutMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.UpdateScrollBar();
        view.surface.InvalidateVisual();
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        if (!view.updatingSelectedIndexFromSelection)
        {
            int selectedIndex = (int)e.NewValue;
            if (selectedIndex < 0 || view.RowCount > 0)
            {
                view.selectionModel.SetItemCount(view.RowCount);
                view.selectionModel.SyncCurrentIndex(selectedIndex);
            }
        }
        view.surface.InvalidateVisual();
    }

    private static void OnSortStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CustomTableView)d).surface.InvalidateVisual();
    }

    private void AttachCollectionChanged(INotifyCollectionChanged collection)
    {
        itemsCollectionChanged = collection;
        if (itemsCollectionChanged != null)
        {
            itemsCollectionChanged.CollectionChanged += ItemsSourceCollectionChanged;
        }
    }

    private void DetachCollectionChanged(INotifyCollectionChanged collection)
    {
        if (collection != null)
        {
            collection.CollectionChanged -= ItemsSourceCollectionChanged;
        }
        if (ReferenceEquals(itemsCollectionChanged, collection))
        {
            itemsCollectionChanged = null;
        }
    }

    private void ItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        MarkItemsApplied();
        CoerceSelectionToCurrentRows();
        UpdateScrollBar();
        surface.InvalidateVisual();
    }

    private void AttachColumnLayoutHandlers(dataGridColumnsSettings settings)
    {
        if (settings == null)
        {
            return;
        }
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layoutCandidate in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            if (layoutCandidate is INotifyPropertyChanged layout)
            {
                layout.PropertyChanged += ColumnLayoutPropertyChanged;
                subscribedColumnLayouts.Add(layout);
            }
        }
    }

    private void DetachColumnLayoutHandlers()
    {
        foreach (INotifyPropertyChanged layout in subscribedColumnLayouts)
        {
            layout.PropertyChanged -= ColumnLayoutPropertyChanged;
        }
        subscribedColumnLayouts.Clear();
    }

    private void ColumnLayoutPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        Columns = CustomTableColumnFactory.CreateMainColumns(ColumnsSettings).ToArray();
        MarkItemsApplied();
    }

    private void CoerceSelectionToCurrentRows()
    {
        selectionModel.SetItemCount(RowCount);
        if (SelectedIndex >= 0 && SelectedIndex < RowCount && !selectionModel.IsSelected(SelectedIndex))
        {
            selectionModel.SyncCurrentIndex(SelectedIndex);
            return;
        }
        if (SelectedIndex != selectionModel.CurrentIndex)
        {
            UpdateSelectedIndexFromSelectionModel();
        }
    }

    private void MarkItemsApplied()
    {
        itemsAppliedTimestamp = Stopwatch.GetTimestamp();
        firstRenderLogged = false;
    }

    private void VerticalScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        surface.InvalidateVisual();
    }

    private void UpdateScrollBar()
    {
        int rowCount = RowCount;
        int viewportRows = CalculateViewportRowCapacity();
        verticalScrollBar.ViewportSize = viewportRows;
        verticalScrollBar.LargeChange = Math.Max(1, viewportRows);
        verticalScrollBar.Maximum = Math.Max(0, rowCount - viewportRows);
        verticalScrollBar.Visibility = rowCount > viewportRows ? Visibility.Visible : Visibility.Collapsed;
        if (verticalScrollBar.Value > verticalScrollBar.Maximum)
        {
            verticalScrollBar.Value = verticalScrollBar.Maximum;
        }
    }

    internal int CalculateViewportRowCapacity()
    {
        double rowHeight = Math.Max(1d, RowHeight);
        double bodyHeight = Math.Max(0d, surface.ActualHeight - HeaderHeight);
        return Math.Max(1, (int)Math.Ceiling(bodyHeight / rowHeight));
    }

    public IReadOnlyList<object> GetSelectedRowsSnapshot()
    {
        IList rows = ItemsSource;
        if (rows == null || rows.Count == 0)
        {
            return Array.Empty<object>();
        }
        return selectionModel.SelectedIndices
            .Where(index => index >= 0 && index < rows.Count)
            .Select(index => rows[index])
            .Where(row => row != null)
            .ToArray();
    }

    public void ClearSelection()
    {
        if (selectionModel.Clear())
        {
            UpdateSelectedIndexFromSelectionModel();
            RaiseSelectionChanged();
            surface.InvalidateVisual();
        }
    }

    internal CustomTableHitTestResult HitTestTable(Point surfacePoint)
    {
        IReadOnlyList<CustomTableColumn> columns = VisibleColumns;
        if (surfacePoint.X < 0d || surfacePoint.Y < 0d || surfacePoint.X >= surface.ActualWidth || surfacePoint.Y >= surface.ActualHeight)
        {
            return CreateEmptyHit();
        }
        bool hasColumn = TryResolveColumn(columns, surfacePoint.X, out CustomTableColumn column, out int columnIndex, out double columnX);
        if (surfacePoint.Y < HeaderHeight)
        {
            double headerColumnWidth = hasColumn ? Math.Min(column.Width, Math.Max(0d, surface.ActualWidth - columnX)) : surface.ActualWidth;
            return new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, column, columnIndex, new Rect(hasColumn ? columnX : 0d, 0d, headerColumnWidth, HeaderHeight));
        }
        if (!hasColumn)
        {
            return CreateEmptyHit();
        }
        double columnWidth = Math.Min(column.Width, Math.Max(0d, surface.ActualWidth - columnX));
        double rowHeight = Math.Max(1d, RowHeight);
        int rowIndex = FirstVisibleRowIndex + (int)Math.Floor((surfacePoint.Y - HeaderHeight) / rowHeight);
        IList rows = ItemsSource;
        if (rows == null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return CreateEmptyHit();
        }
        double rowY = HeaderHeight + (rowIndex - FirstVisibleRowIndex) * rowHeight;
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, columnIndex, new Rect(columnX, rowY, columnWidth, rowHeight));
    }

    private void CustomTableViewPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, verticalScrollBar) && !IsDescendantOfVerticalScrollBar(e.OriginalSource as DependencyObject))
        {
            Focus();
        }
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.Header)
        {
            RequestSort(hit.Column);
            e.Handled = true;
            return;
        }
        if (hit.Kind == CustomTableHitKind.Cell)
        {
            bool changed = ApplyMouseSelection(hit.RowIndex);
            if (changed)
            {
                RaiseSelectionChanged();
            }
            if (e.ClickCount >= 2)
            {
                RowActivated?.Invoke(this, new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true));
            }
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None && selectionModel.Clear())
        {
            UpdateSelectedIndexFromSelectionModel();
            RaiseSelectionChanged();
            surface.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void CustomTableViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.Header)
        {
            HeaderContextMenuRequested?.Invoke(this, new CustomTableHeaderRequestedEventArgs(hit, openAtMousePosition: true));
            e.Handled = true;
            return;
        }
        if (hit.Kind == CustomTableHitKind.Cell)
        {
            Focus();
            if (selectionModel.SelectForRightClick(hit.RowIndex))
            {
                UpdateSelectedIndexFromSelectionModel();
                RaiseSelectionChanged();
                surface.InvalidateVisual();
            }
            RowContextMenuRequested?.Invoke(this, new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true));
            e.Handled = true;
        }
    }

    private void CustomTableViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Apps:
            case Key.F10 when (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift:
                e.Handled = true;
                CustomTableHitTestResult contextHit = CreateSelectedRowHit();
                if (contextHit.Kind == CustomTableHitKind.Cell)
                {
                    RowContextMenuRequested?.Invoke(this, new CustomTableRowRequestedEventArgs(contextHit, openAtMousePosition: false));
                }
                break;
            case Key.Return:
                e.Handled = true;
                CustomTableHitTestResult selectedHit = CreateSelectedRowHit();
                if (selectedHit.Kind == CustomTableHitKind.Cell)
                {
                    RowActivated?.Invoke(this, new CustomTableRowRequestedEventArgs(selectedHit, openAtMousePosition: false));
                }
                break;
        }
    }

    private bool ApplyMouseSelection(int rowIndex)
    {
        selectionModel.SetItemCount(RowCount);
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool changed = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
            ? selectionModel.SelectRange(rowIndex)
            : (modifiers & ModifierKeys.Control) == ModifierKeys.Control
                ? selectionModel.Toggle(rowIndex)
                : selectionModel.SelectSingle(rowIndex);
        UpdateSelectedIndexFromSelectionModel();
        surface.InvalidateVisual();
        return changed;
    }

    private void RequestSort(CustomTableColumn column)
    {
        if (column == null || string.IsNullOrWhiteSpace(column.SortMemberPath))
        {
            return;
        }
        ListSortDirection newDirection = string.Equals(SortColumnName, column.SortMemberPath, StringComparison.Ordinal) && SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        SetCurrentValue(SortColumnNameProperty, column.SortMemberPath);
        SetCurrentValue(SortDirectionProperty, newDirection);
        surface.InvalidateVisual();
        SortRequested?.Invoke(this, new CustomTableSortRequestedEventArgs(column, newDirection));
    }

    private void UpdateSelectedIndexFromSelectionModel()
    {
        updatingSelectedIndexFromSelection = true;
        try
        {
            SetCurrentValue(SelectedIndexProperty, selectionModel.CurrentIndex);
        }
        finally
        {
            updatingSelectedIndexFromSelection = false;
        }
    }

    private void RaiseSelectionChanged()
    {
        IList rows = ItemsSource;
        object selectedRow = rows != null && selectionModel.CurrentIndex >= 0 && selectionModel.CurrentIndex < rows.Count ? rows[selectionModel.CurrentIndex] : null;
        SelectionChanged?.Invoke(this, new CustomTableSelectionChangedEventArgs(selectionModel.CurrentIndex, selectedRow, GetSelectedRowsSnapshot()));
    }

    private CustomTableHitTestResult CreateSelectedRowHit()
    {
        IList rows = ItemsSource;
        int rowIndex = selectionModel.CurrentIndex;
        if (rows == null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return CreateEmptyHit();
        }
        IReadOnlyList<CustomTableColumn> columns = VisibleColumns;
        CustomTableColumn column = columns.Count == 0 ? null : columns[0];
        Rect cellRect = column == null ? Rect.Empty : new Rect(0d, HeaderHeight + (rowIndex - FirstVisibleRowIndex) * Math.Max(1d, RowHeight), Math.Min(column.Width, surface.ActualWidth), Math.Max(1d, RowHeight));
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, column == null ? -1 : 0, cellRect);
    }

    private bool TryResolveColumn(IReadOnlyList<CustomTableColumn> columns, double x, out CustomTableColumn column, out int columnIndex, out double columnX)
    {
        double currentX = 0d;
        for (int i = 0; i < columns.Count; i++)
        {
            CustomTableColumn candidate = columns[i];
            double width = candidate.Width;
            if (x >= currentX && x < currentX + width)
            {
                column = candidate;
                columnIndex = i;
                columnX = currentX;
                return true;
            }
            currentX += width;
        }
        column = null;
        columnIndex = -1;
        columnX = 0d;
        return false;
    }

    private static CustomTableHitTestResult CreateEmptyHit()
    {
        return new CustomTableHitTestResult(CustomTableHitKind.Empty, -1, null, null, -1, Rect.Empty);
    }

    private bool IsDescendantOfVerticalScrollBar(DependencyObject source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, verticalScrollBar))
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    internal void NotifySurfaceRendered(int visibleRowCount, int visibleColumnCount, long renderWorkMs, double textCacheHitRate)
    {
        if (firstRenderLogged || !IsVisible || itemsAppliedTimestamp <= 0L)
        {
            return;
        }
        firstRenderLogged = true;
        long firstRenderMs = (Stopwatch.GetTimestamp() - itemsAppliedTimestamp) * 1000L / Stopwatch.Frequency;
        int visibleCellCount = TableFirstVisibleMetrics.CalculateVisibleCellCount(visibleRowCount, visibleColumnCount);
        FirstRenderCompleted?.Invoke(this, new CustomTableFirstRenderCompletedEventArgs(RowCount, visibleRowCount, visibleColumnCount, visibleCellCount, firstRenderMs, renderWorkMs, textCacheHitRate));
    }
}

internal sealed class CustomTableSurface : FrameworkElement
{
    private static readonly Brush HeaderBackgroundBrush = CreateBrush(Color.FromRgb(0xF6, 0xF7, 0xF8));
    private static readonly Brush RowBackgroundBrush = CreateBrush(Colors.White);
    private static readonly Brush AlternatingRowBackgroundBrush = CreateBrush(Color.FromRgb(0xF1, 0xF4, 0xF7));
    private static readonly Brush WarningRowBackgroundBrush = CreateBrush(Color.FromRgb(0xFD, 0xE4, 0xE4));
    private static readonly Brush SelectedRowBackgroundBrush = CreateBrush(Colors.DodgerBlue);
    private static readonly Brush SortGlyphBrush = CreateBrush(Color.FromRgb(0x45, 0x4A, 0x50));
    private static readonly Pen CellBorderPen = CreatePen(Color.FromRgb(0xE7, 0xE9, 0xEC));
    private static readonly Pen HeaderBorderPen = CreatePen(Color.FromRgb(0xC8, 0xCC, 0xD1));
    private static readonly Typeface NormalTypeface = new Typeface(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new Typeface(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private readonly CustomTableView owner;
    private readonly CustomTableTextLayoutCache textLayoutCache = new CustomTableTextLayoutCache();
    private int renderTextCacheHits;
    private int renderTextCacheMisses;

    internal CustomTableSurface(CustomTableView owner)
    {
        this.owner = owner;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        Stopwatch renderStopwatch = Stopwatch.StartNew();
        renderTextCacheHits = 0;
        renderTextCacheMisses = 0;
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0d || height <= 0d)
        {
            owner.NotifySurfaceRendered(0, 0, 0L, -1d);
            return;
        }
        IReadOnlyList<CustomTableColumn> columns = owner.VisibleColumns;
        DrawBackground(drawingContext, width, height);
        DrawHeader(drawingContext, columns, width);
        int visibleRowCount = DrawRows(drawingContext, columns, width, height);
        renderStopwatch.Stop();
        owner.NotifySurfaceRendered(
            visibleRowCount,
            CountColumnsWithinSurface(columns, width),
            renderStopwatch.ElapsedMilliseconds,
            CustomTableTextLayoutCache.CalculateHitRate(renderTextCacheHits, renderTextCacheMisses));
    }

    private void DrawBackground(DrawingContext drawingContext, double width, double height)
    {
        drawingContext.DrawRectangle(RowBackgroundBrush, null, new Rect(0d, 0d, width, height));
    }

    private void DrawHeader(DrawingContext drawingContext, IReadOnlyList<CustomTableColumn> columns, double width)
    {
        double headerHeight = owner.HeaderHeight;
        drawingContext.DrawRectangle(HeaderBackgroundBrush, null, new Rect(0d, 0d, width, headerHeight));
        double x = 0d;
        foreach (CustomTableColumn column in columns)
        {
            double columnWidth = column.Width;
            if (x >= width)
            {
                break;
            }
            Rect cellRect = new Rect(x, 0d, Math.Min(columnWidth, width - x), headerHeight);
            DrawCellText(drawingContext, column.Header, cellRect, CustomTableScoreBrushProvider.DefaultForeground, TextAlignment.Center, useBoldText: false);
            DrawSortGlyph(drawingContext, column, cellRect);
            drawingContext.DrawLine(HeaderBorderPen, new Point(x + columnWidth - 0.5d, 0d), new Point(x + columnWidth - 0.5d, headerHeight));
            x += columnWidth;
        }
        drawingContext.DrawLine(HeaderBorderPen, new Point(0d, headerHeight - 0.5d), new Point(width, headerHeight - 0.5d));
    }

    private int DrawRows(DrawingContext drawingContext, IReadOnlyList<CustomTableColumn> columns, double width, double height)
    {
        IList rows = owner.ItemsSource;
        int rowCount = rows?.Count ?? 0;
        if (rowCount == 0)
        {
            return 0;
        }
        double rowHeight = Math.Max(1d, owner.RowHeight);
        double y = owner.HeaderHeight;
        int firstRowIndex = owner.FirstVisibleRowIndex;
        int visibleRowCapacity = owner.CalculateViewportRowCapacity();
        int lastRowExclusive = Math.Min(rowCount, firstRowIndex + visibleRowCapacity);
        int drawnRows = 0;
        for (int rowIndex = firstRowIndex; rowIndex < lastRowExclusive && y < height; rowIndex++)
        {
            object row = rows[rowIndex];
            bool selected = owner.IsRowSelected(rowIndex);
            Brush rowBackground = selected
                ? SelectedRowBackgroundBrush
                : CustomTableColumnFactory.HasHighlightedWarning(row)
                    ? WarningRowBackgroundBrush
                    : (rowIndex & 1) == 1
                        ? AlternatingRowBackgroundBrush
                        : RowBackgroundBrush;
            Rect rowRect = new Rect(0d, y, width, Math.Min(rowHeight, height - y));
            drawingContext.DrawRectangle(rowBackground, null, rowRect);
            DrawRowCells(drawingContext, columns, row, selected, width, y, rowHeight);
            drawingContext.DrawLine(CellBorderPen, new Point(0d, y + rowHeight - 0.5d), new Point(width, y + rowHeight - 0.5d));
            y += rowHeight;
            drawnRows++;
        }
        return drawnRows;
    }

    private void DrawRowCells(DrawingContext drawingContext, IReadOnlyList<CustomTableColumn> columns, object row, bool selected, double width, double y, double rowHeight)
    {
        double x = 0d;
        foreach (CustomTableColumn column in columns)
        {
            double columnWidth = column.Width;
            if (x >= width)
            {
                break;
            }
            Rect cellRect = new Rect(x, y, Math.Min(columnWidth, width - x), rowHeight);
            Brush foreground = selected ? CustomTableScoreBrushProvider.SelectedForeground : column.GetForeground(row);
            DrawCellText(drawingContext, column.GetText(row), cellRect, foreground, column.Alignment, column.UseBoldText);
            drawingContext.DrawLine(CellBorderPen, new Point(x + columnWidth - 0.5d, y), new Point(x + columnWidth - 0.5d, y + rowHeight));
            x += columnWidth;
        }
    }

    private void DrawCellText(DrawingContext drawingContext, string text, Rect cellRect, Brush foreground, TextAlignment alignment, bool useBoldText)
    {
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 2d || cellRect.Height <= 1d)
        {
            return;
        }
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maxTextWidth = Math.Max(1d, cellRect.Width - 4d);
        double maxTextHeight = Math.Max(1d, cellRect.Height);
        FormattedText formattedText = textLayoutCache.GetOrCreate(
            text,
            maxTextWidth,
            maxTextHeight,
            alignment,
            useBoldText,
            foreground,
            pixelsPerDip,
            CultureInfo.CurrentUICulture,
            useBoldText ? BoldTypeface : NormalTypeface,
            11d,
            out bool cacheHit);
        if (cacheHit)
        {
            renderTextCacheHits++;
        }
        else
        {
            renderTextCacheMisses++;
        }
        double x = cellRect.X + 2d;
        double y = cellRect.Y + Math.Max(0d, (cellRect.Height - formattedText.Height) / 2d);
        drawingContext.PushClip(new RectangleGeometry(cellRect));
        drawingContext.DrawText(formattedText, new Point(x, y));
        drawingContext.Pop();
    }

    private void DrawSortGlyph(DrawingContext drawingContext, CustomTableColumn column, Rect cellRect)
    {
        if (column == null || string.IsNullOrWhiteSpace(column.SortMemberPath) || !string.Equals(column.SortMemberPath, owner.SortColumnName, StringComparison.Ordinal) || !owner.SortDirection.HasValue || cellRect.Width < 12d || cellRect.Height < 8d)
        {
            return;
        }
        double centerX = cellRect.Right - 8d;
        double centerY = cellRect.Top + cellRect.Height / 2d;
        bool ascending = owner.SortDirection == ListSortDirection.Ascending;
        Point p1 = ascending ? new Point(centerX, centerY - 3d) : new Point(centerX, centerY + 3d);
        Point p2 = ascending ? new Point(centerX - 4d, centerY + 2d) : new Point(centerX - 4d, centerY - 2d);
        Point p3 = ascending ? new Point(centerX + 4d, centerY + 2d) : new Point(centerX + 4d, centerY - 2d);
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(p1, isFilled: true, isClosed: true);
            context.LineTo(p2, isStroked: true, isSmoothJoin: false);
            context.LineTo(p3, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(SortGlyphBrush, null, geometry);
    }

    private static int CountColumnsWithinSurface(IReadOnlyList<CustomTableColumn> columns, double width)
    {
        double x = 0d;
        int count = 0;
        foreach (CustomTableColumn column in columns)
        {
            if (x >= width)
            {
                break;
            }
            x += column.Width;
            count++;
        }
        return count;
    }

    private static Brush CreateBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color)
    {
        Pen pen = new Pen(CreateBrush(color), 1d);
        pen.Freeze();
        return pen;
    }
}
