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
    private const double ColumnResizeHitTestMargin = 4d;

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
    private readonly ScrollBar horizontalScrollBar;
    private readonly ToolTip cellToolTip;
    private readonly CustomTableSelectionModel selectionModel = new CustomTableSelectionModel();
    private readonly List<INotifyPropertyChanged> subscribedColumnLayouts = new List<INotifyPropertyChanged>();
    private INotifyCollectionChanged itemsCollectionChanged;
    private long itemsAppliedTimestamp;
    private bool firstRenderLogged = true;
    private bool updatingSelectedIndexFromSelection;
    private bool isResizingColumn;
    private dataGridColumnsSettings.dataGridColumnlayouts resizingColumnLayout;
    private int resizingColumnMinWidth;
    private int resizingColumnMaxWidth;
    private double resizingStartMouseX;
    private int resizingStartWidth;
    private int toolTipRowIndex = -1;
    private string toolTipColumnId;

    public CustomTableView()
    {
        ClipToBounds = true;
        Focusable = true;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        horizontalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Height = SystemParameters.HorizontalScrollBarHeight,
            Minimum = 0d,
            SmallChange = 16d
        };
        cellToolTip = new ToolTip
        {
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint
        };
        verticalScrollBar.ValueChanged += VerticalScrollBarValueChanged;
        horizontalScrollBar.ValueChanged += HorizontalScrollBarValueChanged;
        Children.Add(surface);
        Children.Add(verticalScrollBar);
        Children.Add(horizontalScrollBar);
        SetColumn(surface, 0);
        SetRow(surface, 0);
        SetColumn(verticalScrollBar, 1);
        SetRow(verticalScrollBar, 0);
        SetColumn(horizontalScrollBar, 0);
        SetRow(horizontalScrollBar, 1);
        SizeChanged += delegate
        {
            UpdateScrollBars();
            surface.InvalidateVisual();
        };
        PreviewMouseLeftButtonDown += CustomTableViewPreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += CustomTableViewPreviewMouseLeftButtonUp;
        PreviewMouseRightButtonDown += CustomTableViewPreviewMouseRightButtonDown;
        PreviewMouseMove += CustomTableViewPreviewMouseMove;
        PreviewMouseWheel += CustomTableViewPreviewMouseWheel;
        PreviewKeyDown += CustomTableViewPreviewKeyDown;
        MouseLeave += delegate
        {
            CloseCellToolTip();
        };
        LostMouseCapture += delegate
        {
            EndColumnResize();
        };
        IsVisibleChanged += delegate
        {
            if (IsVisible)
            {
                MarkItemsApplied();
                UpdateScrollBars();
                surface.InvalidateVisual();
            }
            else
            {
                CloseCellToolTip();
                EndColumnResize();
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

    internal double HorizontalOffset => Math.Max(0d, horizontalScrollBar.Value);

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
        view.UpdateScrollBars();
        view.surface.InvalidateVisual();
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.UpdateScrollBars();
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
        view.UpdateScrollBars();
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
        UpdateScrollBars();
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
        RebuildColumns(!string.Equals(e.PropertyName, nameof(dataGridColumnsSettings.dataGridColumnlayouts.Width), StringComparison.Ordinal));
    }

    private void RebuildColumns(bool markItemsApplied = true)
    {
        Columns = CustomTableColumnFactory.CreateMainColumns(ColumnsSettings).ToArray();
        if (markItemsApplied)
        {
            MarkItemsApplied();
        }
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
        CloseCellToolTip();
        surface.InvalidateVisual();
    }

    private void HorizontalScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CloseCellToolTip();
        surface.InvalidateVisual();
    }

    private void UpdateScrollBars()
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
        double extentWidth = CustomTableColumnLayout.CalculateExtentWidth(VisibleColumns);
        double viewportWidth = Math.Max(0d, surface.ActualWidth);
        horizontalScrollBar.ViewportSize = viewportWidth;
        horizontalScrollBar.LargeChange = Math.Max(1d, viewportWidth);
        horizontalScrollBar.Maximum = Math.Max(0d, extentWidth - viewportWidth);
        horizontalScrollBar.Visibility = extentWidth > viewportWidth + 0.5d ? Visibility.Visible : Visibility.Collapsed;
        if (horizontalScrollBar.Value > horizontalScrollBar.Maximum)
        {
            horizontalScrollBar.Value = horizontalScrollBar.Maximum;
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
        double horizontalOffset = HorizontalOffset;
        double tableX = surfacePoint.X + horizontalOffset;
        if (surfacePoint.Y < HeaderHeight)
        {
            if (CustomTableColumnLayout.TryResolveResizeColumn(columns, surfacePoint.X, horizontalOffset, surface.ActualWidth, ColumnResizeHitTestMargin, out CustomTableColumn resizeColumn, out int resizeColumnIndex, out Rect resizeRect))
            {
                return new CustomTableHitTestResult(CustomTableHitKind.HeaderResize, -1, null, resizeColumn, resizeColumnIndex, resizeRect);
            }
            bool hasHeaderColumn = CustomTableColumnLayout.TryResolveColumn(columns, tableX, out CustomTableColumn headerColumn, out int headerColumnIndex, out double headerColumnX);
            Rect headerRect = hasHeaderColumn ? CustomTableColumnLayout.CreateVisibleColumnRect(headerColumnX, headerColumn.Width, horizontalOffset, surface.ActualWidth, 0d, HeaderHeight) : new Rect(0d, 0d, surface.ActualWidth, HeaderHeight);
            return new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, headerColumn, headerColumnIndex, headerRect);
        }
        bool hasColumn = CustomTableColumnLayout.TryResolveColumn(columns, tableX, out CustomTableColumn column, out int columnIndex, out double columnX);
        if (!hasColumn)
        {
            return CreateEmptyHit();
        }
        double rowHeight = Math.Max(1d, RowHeight);
        int rowIndex = FirstVisibleRowIndex + (int)Math.Floor((surfacePoint.Y - HeaderHeight) / rowHeight);
        IList rows = ItemsSource;
        if (rows == null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return CreateEmptyHit();
        }
        double rowY = HeaderHeight + (rowIndex - FirstVisibleRowIndex) * rowHeight;
        Rect cellRect = CustomTableColumnLayout.CreateVisibleColumnRect(columnX, column.Width, horizontalOffset, surface.ActualWidth, rowY, rowHeight);
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, columnIndex, cellRect);
    }

    private void CustomTableViewPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseCellToolTip();
        if (!IsDescendantOfScrollBar(e.OriginalSource as DependencyObject))
        {
            Focus();
        }
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.HeaderResize)
        {
            BeginColumnResize(hit, e.GetPosition(surface).X);
            e.Handled = true;
            return;
        }
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
        CloseCellToolTip();
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.HeaderResize)
        {
            hit = new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, hit.Column, hit.ColumnIndex, hit.CellRect);
        }
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

    private void CustomTableViewPreviewMouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(surface);
        if (isResizingColumn)
        {
            UpdateColumnResize(position.X);
            e.Handled = true;
            return;
        }
        CustomTableHitTestResult hit = HitTestTable(position);
        Cursor = hit.Kind == CustomTableHitKind.HeaderResize ? Cursors.SizeWE : null;
        UpdateCellToolTip(hit);
    }

    private void CustomTableViewPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (isResizingColumn)
        {
            EndColumnResize();
            e.Handled = true;
        }
    }

    private void CustomTableViewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (verticalScrollBar.Visibility != Visibility.Visible || e.Delta == 0)
        {
            return;
        }
        CloseCellToolTip();
        double direction = e.Delta > 0 ? -1d : 1d;
        double nextValue = Math.Max(verticalScrollBar.Minimum, Math.Min(verticalScrollBar.Maximum, verticalScrollBar.Value + direction * verticalScrollBar.SmallChange));
        if (!verticalScrollBar.Value.Equals(nextValue))
        {
            verticalScrollBar.Value = nextValue;
        }
        e.Handled = true;
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
        Rect cellRect = column == null ? Rect.Empty : CustomTableColumnLayout.CreateVisibleColumnRect(0d, column.Width, HorizontalOffset, surface.ActualWidth, HeaderHeight + (rowIndex - FirstVisibleRowIndex) * Math.Max(1d, RowHeight), Math.Max(1d, RowHeight));
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, column == null ? -1 : 0, cellRect);
    }

    private void BeginColumnResize(CustomTableHitTestResult hit, double surfaceX)
    {
        if (hit?.Column == null || hit.Column.Layout == null || !hit.Column.CanResize)
        {
            return;
        }
        CloseCellToolTip();
        isResizingColumn = true;
        resizingColumnLayout = hit.Column.Layout;
        resizingColumnMinWidth = hit.Column.MinWidth;
        resizingColumnMaxWidth = hit.Column.MaxWidth;
        resizingStartWidth = hit.Column.Width;
        resizingStartMouseX = surfaceX;
        Cursor = Cursors.SizeWE;
        CaptureMouse();
    }

    private void UpdateColumnResize(double surfaceX)
    {
        if (!isResizingColumn || resizingColumnLayout == null)
        {
            return;
        }
        double delta = surfaceX - resizingStartMouseX;
        int width = Math.Max(resizingColumnMinWidth, Math.Min(resizingColumnMaxWidth, (int)Math.Round(resizingStartWidth + delta)));
        if (resizingColumnLayout.Width != width)
        {
            resizingColumnLayout.Width = width;
            UpdateScrollBars();
            surface.InvalidateVisual();
        }
    }

    private void EndColumnResize()
    {
        if (!isResizingColumn)
        {
            return;
        }
        isResizingColumn = false;
        resizingColumnLayout = null;
        resizingColumnMinWidth = 0;
        resizingColumnMaxWidth = 0;
        resizingStartWidth = 0;
        resizingStartMouseX = 0d;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Cursor = null;
        UpdateScrollBars();
        surface.InvalidateVisual();
    }

    private void UpdateCellToolTip(CustomTableHitTestResult hit)
    {
        if (isResizingColumn || hit == null || hit.Kind != CustomTableHitKind.Cell || hit.Column == null)
        {
            CloseCellToolTip();
            return;
        }
        string tooltip = hit.Column.GetTooltip(hit.Row);
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            CloseCellToolTip();
            return;
        }
        bool movedToAnotherCell = toolTipRowIndex != hit.RowIndex || !string.Equals(toolTipColumnId, hit.Column.Id, StringComparison.Ordinal);
        if (movedToAnotherCell && cellToolTip.IsOpen)
        {
            cellToolTip.IsOpen = false;
        }
        if (!Equals(cellToolTip.Content, tooltip))
        {
            cellToolTip.Content = tooltip;
        }
        toolTipRowIndex = hit.RowIndex;
        toolTipColumnId = hit.Column.Id;
        if (!cellToolTip.IsOpen)
        {
            cellToolTip.IsOpen = true;
        }
    }

    private void CloseCellToolTip()
    {
        if (cellToolTip.IsOpen)
        {
            cellToolTip.IsOpen = false;
        }
        toolTipRowIndex = -1;
        toolTipColumnId = null;
    }

    private static CustomTableHitTestResult CreateEmptyHit()
    {
        return new CustomTableHitTestResult(CustomTableHitKind.Empty, -1, null, null, -1, Rect.Empty);
    }

    private bool IsDescendantOfScrollBar(DependencyObject source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, verticalScrollBar) || ReferenceEquals(source, horizontalScrollBar))
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

internal static class CustomTableColumnLayout
{
    internal static double CalculateExtentWidth(IReadOnlyList<CustomTableColumn> columns)
    {
        if (columns == null || columns.Count == 0)
        {
            return 0d;
        }
        double width = 0d;
        foreach (CustomTableColumn column in columns)
        {
            width += column?.Width ?? 0d;
        }
        return width;
    }

    internal static bool TryResolveColumn(IReadOnlyList<CustomTableColumn> columns, double tableX, out CustomTableColumn column, out int columnIndex, out double columnX)
    {
        double currentX = 0d;
        if (columns != null)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                CustomTableColumn candidate = columns[i];
                double width = candidate?.Width ?? 0d;
                if (tableX >= currentX && tableX < currentX + width)
                {
                    column = candidate;
                    columnIndex = i;
                    columnX = currentX;
                    return candidate != null;
                }
                currentX += width;
            }
        }
        column = null;
        columnIndex = -1;
        columnX = 0d;
        return false;
    }

    internal static bool TryResolveResizeColumn(IReadOnlyList<CustomTableColumn> columns, double surfaceX, double horizontalOffset, double viewportWidth, double margin, out CustomTableColumn column, out int columnIndex, out Rect resizeRect)
    {
        double currentX = 0d;
        if (columns != null)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                CustomTableColumn candidate = columns[i];
                double width = candidate?.Width ?? 0d;
                double edgeX = currentX + width - horizontalOffset;
                if (candidate != null && candidate.CanResize && edgeX >= 0d && edgeX <= viewportWidth && Math.Abs(surfaceX - edgeX) <= margin)
                {
                    column = candidate;
                    columnIndex = i;
                    resizeRect = new Rect(Math.Max(0d, edgeX - margin), 0d, margin * 2d, 0d);
                    return true;
                }
                currentX += width;
            }
        }
        column = null;
        columnIndex = -1;
        resizeRect = Rect.Empty;
        return false;
    }

    internal static Rect CreateVisibleColumnRect(double columnX, double columnWidth, double horizontalOffset, double viewportWidth, double y, double height)
    {
        double left = Math.Max(0d, columnX - horizontalOffset);
        double right = Math.Min(viewportWidth, columnX + columnWidth - horizontalOffset);
        return new Rect(left, y, Math.Max(0d, right - left), height);
    }

    internal static int CountColumnsWithinViewport(IReadOnlyList<CustomTableColumn> columns, double horizontalOffset, double viewportWidth)
    {
        double currentX = 0d;
        int count = 0;
        if (columns != null)
        {
            foreach (CustomTableColumn column in columns)
            {
                double width = column?.Width ?? 0d;
                if (currentX + width > horizontalOffset && currentX < horizontalOffset + viewportWidth)
                {
                    count++;
                }
                if (currentX >= horizontalOffset + viewportWidth)
                {
                    break;
                }
                currentX += width;
            }
        }
        return count;
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
            CustomTableColumnLayout.CountColumnsWithinViewport(columns, owner.HorizontalOffset, width),
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
        double horizontalOffset = owner.HorizontalOffset;
        drawingContext.DrawRectangle(HeaderBackgroundBrush, null, new Rect(0d, 0d, width, headerHeight));
        double x = 0d;
        foreach (CustomTableColumn column in columns)
        {
            double columnWidth = column.Width;
            double screenX = x - horizontalOffset;
            if (screenX >= width)
            {
                break;
            }
            if (screenX + columnWidth > 0d)
            {
                Rect cellRect = CustomTableColumnLayout.CreateVisibleColumnRect(x, columnWidth, horizontalOffset, width, 0d, headerHeight);
                DrawCellText(drawingContext, column.Header, cellRect, CustomTableScoreBrushProvider.DefaultForeground, TextAlignment.Center, useBoldText: false);
                DrawSortGlyph(drawingContext, column, cellRect);
                double borderX = x + columnWidth - horizontalOffset - 0.5d;
                if (borderX >= 0d && borderX <= width)
                {
                    drawingContext.DrawLine(HeaderBorderPen, new Point(borderX, 0d), new Point(borderX, headerHeight));
                }
            }
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
        double horizontalOffset = owner.HorizontalOffset;
        double x = 0d;
        foreach (CustomTableColumn column in columns)
        {
            double columnWidth = column.Width;
            double screenX = x - horizontalOffset;
            if (screenX >= width)
            {
                break;
            }
            if (screenX + columnWidth > 0d)
            {
                Rect cellRect = CustomTableColumnLayout.CreateVisibleColumnRect(x, columnWidth, horizontalOffset, width, y, rowHeight);
                Brush foreground = selected ? CustomTableScoreBrushProvider.SelectedForeground : column.GetForeground(row);
                string text = column.GetText(row);
                if (column.UseIconText)
                {
                    DrawDownloadIcon(drawingContext, text, cellRect, foreground);
                }
                else
                {
                    DrawCellText(drawingContext, text, cellRect, foreground, column.Alignment, column.UseBoldText);
                }
                double borderX = x + columnWidth - horizontalOffset - 0.5d;
                if (borderX >= 0d && borderX <= width)
                {
                    drawingContext.DrawLine(CellBorderPen, new Point(borderX, y), new Point(borderX, y + rowHeight));
                }
            }
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

    private static void DrawDownloadIcon(DrawingContext drawingContext, string text, Rect cellRect, Brush foreground)
    {
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 8d || cellRect.Height <= 8d)
        {
            return;
        }
        Brush iconBrush = foreground ?? CustomTableScoreBrushProvider.DefaultForeground;
        double size = Math.Max(8d, Math.Min(13d, Math.Min(cellRect.Width - 6d, cellRect.Height - 4d)));
        double centerX = cellRect.Left + cellRect.Width / 2d;
        double top = cellRect.Top + Math.Max(1d, (cellRect.Height - size) / 2d);
        double bottom = top + size;
        double trayY = bottom - 2d;
        Pen pen = new Pen(iconBrush, 1.7d)
        {
            StartLineCap = PenLineCap.Square,
            EndLineCap = PenLineCap.Square
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        StreamGeometry arrow = new StreamGeometry();
        using (StreamGeometryContext context = arrow.Open())
        {
            context.BeginFigure(new Point(centerX, top), isFilled: false, isClosed: false);
            context.LineTo(new Point(centerX, trayY - 3d), isStroked: true, isSmoothJoin: false);
            context.BeginFigure(new Point(centerX - 4d, trayY - 7d), isFilled: false, isClosed: false);
            context.LineTo(new Point(centerX, trayY - 3d), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(centerX + 4d, trayY - 7d), isStroked: true, isSmoothJoin: false);
            context.BeginFigure(new Point(centerX - 6d, trayY), isFilled: false, isClosed: false);
            context.LineTo(new Point(centerX - 6d, bottom), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(centerX + 6d, bottom), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(centerX + 6d, trayY), isStroked: true, isSmoothJoin: false);
        }
        arrow.Freeze();
        drawingContext.DrawGeometry(null, pen, arrow);
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
