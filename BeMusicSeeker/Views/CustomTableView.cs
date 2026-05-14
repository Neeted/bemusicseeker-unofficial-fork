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
using System.Windows.Media.Media3D;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using NLog;

namespace BeMusicSeeker.Views;

public sealed class CustomTableFirstRenderCompletedEventArgs : EventArgs
{
    internal CustomTableFirstRenderCompletedEventArgs(int rowCount, int visibleRowCount, int visibleColumnCount, int visibleCellCount, long firstRenderMs, long renderWorkMs, double textCacheHitRate, bool isPreparationRender)
    {
        RowCount = rowCount;
        VisibleRowCount = visibleRowCount;
        VisibleColumnCount = visibleColumnCount;
        VisibleCellCount = visibleCellCount;
        FirstRenderMs = firstRenderMs;
        RenderWorkMs = renderWorkMs;
        TextCacheHitRate = textCacheHitRate;
        IsPreparationRender = isPreparationRender;
    }

    public int RowCount { get; }

    public int VisibleRowCount { get; }

    public int VisibleColumnCount { get; }

    public int VisibleCellCount { get; }

    public long FirstRenderMs { get; }

    public long RenderWorkMs { get; }

    public double TextCacheHitRate { get; }

    public bool IsPreparationRender { get; }
}

internal enum CustomTableKeyboardCommand
{
    None,
    SelectAllRows,
    CopyCurrentCell,
    CopySelectedRowsTsv
}

public sealed class CustomTableView : Grid
{
    private const double ColumnResizeHitTestMargin = 4d;
    private const double ScrollBarThickness = 15d;
    private const int RowSubscriptionOverscan = 5;
    private const long RowSubscriptionSlowLogThresholdMs = 100L;
    private const long RenderSlowLogThresholdMs = 100L;
    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.CustomTableView");

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IList),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(IList<CustomTableColumn>),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, OnColumnsChanged));

    public static readonly DependencyProperty ColumnsSettingsProperty = DependencyProperty.Register(
        nameof(ColumnsSettings),
        typeof(CustomTableColumnSettings),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, OnColumnsSettingsChanged));

    public static readonly DependencyProperty PlaylistSummaryColumnsSettingsProperty = DependencyProperty.Register(
        nameof(PlaylistSummaryColumnsSettings),
        typeof(PlaylistSummaryColumnSettings),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, OnPlaylistSummaryColumnsSettingsChanged));

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

    public static readonly DependencyProperty ScoreFontFamilyProperty = DependencyProperty.Register(
        nameof(ScoreFontFamily),
        typeof(FontFamily),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnScoreFontFamilyChanged));

    private readonly CustomTableSurface surface;
    private readonly Canvas editorLayer;
    private readonly ScrollBar verticalScrollBar;
    private readonly ScrollBar horizontalScrollBar;
    private readonly Border scrollBarCorner;
    private readonly ToolTip cellToolTip;
    private readonly Popup editSuggestionPopup;
    private readonly ListBox editSuggestionListBox;
    private readonly CustomTableSelectionModel selectionModel = new CustomTableSelectionModel();
    private readonly CustomTableCellValueCache cellValueCache = new CustomTableCellValueCache();
    private readonly CustomTableRowChangeTracker rowChangeTracker;
    private readonly CustomTableRowInvalidationQueue pendingRowInvalidations = new CustomTableRowInvalidationQueue();
    private readonly CustomTableRedrawScheduler rowPropertyChangedRedrawScheduler;
    private readonly List<INotifyPropertyChanged> subscribedColumnLayouts = new List<INotifyPropertyChanged>();
    private readonly HashSet<string> loggedRenderReasons = new HashSet<string>();
    private CustomTableColumnLayoutSnapshot columnLayoutSnapshot;
    private INotifyCollectionChanged itemsCollectionChanged;
    private string pendingRedrawReason = "initial";
    private int lastVisibleSubscriptionFirstIndex = -1;
    private int lastVisibleSubscriptionCount = -1;
    private int rowValueGeneration;
    private int columnValueGeneration;
    private long itemsAppliedTimestamp;
    private bool firstRenderLogged = true;
    private bool updatingSelectedIndexFromSelection;
    private bool isResizingColumn;
    private ICustomTableColumnLayout resizingColumnLayout;
    private int resizingColumnMinWidth;
    private int resizingColumnMaxWidth;
    private double resizingStartMouseX;
    private int resizingStartWidth;
    private int toolTipRowIndex = -1;
    private string toolTipColumnId;
    private CustomTableHitTestResult currentCellHit;
    private Point? rowDragStartPoint;
    private CustomTableHitTestResult rowDragStartHit;
    private int pendingSingleSelectionOnMouseUpRowIndex = -1;
    private DragAdorner rowDragAdorner;
    private Point? headerDragStartPoint;
    private CustomTableHitTestResult pendingHeaderHit;
    private bool isReorderingColumn;
    private double reorderPreviewInsertX = double.NaN;
    private CustomTableHitTestResult activeEditHit;
    private TextBox activeEditor;
    private bool completingEdit;
    private bool completingSuggestionSelection;
    private bool preparationRenderLogged;
    private bool suppressColumnRedrawUntilItemsSourceChanged;

    public CustomTableView()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(Panel.BackgroundProperty, "ScrollBar.TrackBackgroundBrush");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        surface = new CustomTableSurface(this);
        verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = ScrollBarThickness,
            Minimum = 0d,
            SmallChange = 3d
        };
        horizontalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Height = ScrollBarThickness,
            Minimum = 0d,
            SmallChange = 16d
        };
        scrollBarCorner = new Border();
        scrollBarCorner.SetResourceReference(Border.BackgroundProperty, "ScrollBar.TrackBackgroundBrush");
        cellToolTip = new ToolTip
        {
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint
        };
        editSuggestionListBox = new ListBox
        {
            BorderThickness = new Thickness(0d),
            Padding = new Thickness(0d)
        };
        editSuggestionPopup = new Popup
        {
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = CustomTablePalette.Current.RowBackground,
                BorderBrush = CustomTablePalette.Current.HeaderBorder.Brush,
                BorderThickness = new Thickness(1d),
                MaxWidth = 600d,
                Child = editSuggestionListBox
            }
        };
        editorLayer = new Canvas
        {
            ClipToBounds = true
        };
        rowPropertyChangedRedrawScheduler = new CustomTableRedrawScheduler(Dispatcher, FlushPendingRowInvalidations);
        rowChangeTracker = new CustomTableRowChangeTracker(delegate(INotifyPropertyChanged row, PropertyChangedEventArgs e)
        {
            EnqueueRowInvalidation(row);
        });
        verticalScrollBar.ValueChanged += VerticalScrollBarValueChanged;
        horizontalScrollBar.ValueChanged += HorizontalScrollBarValueChanged;
        Children.Add(surface);
        Children.Add(editorLayer);
        Children.Add(verticalScrollBar);
        Children.Add(horizontalScrollBar);
        Children.Add(scrollBarCorner);
        SetColumn(surface, 0);
        SetRow(surface, 0);
        SetColumn(editorLayer, 0);
        SetRow(editorLayer, 0);
        SetColumn(verticalScrollBar, 1);
        SetRow(verticalScrollBar, 0);
        SetColumn(horizontalScrollBar, 0);
        SetRow(horizontalScrollBar, 1);
        SetColumn(scrollBarCorner, 1);
        SetRow(scrollBarCorner, 1);
        surface.SizeChanged += delegate
        {
            InvalidateColumnLayoutSnapshot();
            UpdateScrollBars();
            UpdateVisibleRowSubscriptions("surface_size_changed");
            RequestRedraw("surface_size_changed");
        };
        SizeChanged += delegate
        {
            InvalidateColumnLayoutSnapshot();
            UpdateScrollBars();
            UpdateVisibleRowSubscriptions("size_changed");
            RequestRedraw("size_changed");
        };
        PreviewMouseLeftButtonDown += CustomTableViewPreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += CustomTableViewPreviewMouseLeftButtonUp;
        PreviewMouseRightButtonDown += CustomTableViewPreviewMouseRightButtonDown;
        PreviewMouseMove += CustomTableViewPreviewMouseMove;
        PreviewMouseWheel += CustomTableViewPreviewMouseWheel;
        PreviewKeyDown += CustomTableViewPreviewKeyDown;
        PreviewTextInput += CustomTableViewPreviewTextInput;
        QueryContinueDrag += CustomTableViewQueryContinueDrag;
        editSuggestionListBox.PreviewMouseLeftButtonDown += EditSuggestionListBoxPreviewMouseLeftButtonDown;
        editSuggestionListBox.PreviewKeyDown += EditSuggestionListBoxPreviewKeyDown;
        Loaded += CustomTableViewLoaded;
        Unloaded += CustomTableViewUnloaded;
        MouseLeave += delegate
        {
            CloseCellToolTip();
        };
        LostMouseCapture += delegate
        {
            EndColumnResize();
            ClearHeaderDragState();
        };
        IsVisibleChanged += delegate
        {
            if (IsVisible)
            {
                MarkItemsApplied();
                InvalidateColumnLayoutSnapshot();
                UpdateScrollBars();
                UpdateVisibleRowSubscriptions("visible_changed", logAlways: true);
                RequestRedraw("visible_changed");
            }
            else
            {
                CommitActiveEdit();
                CloseCellToolTip();
                rowChangeTracker.DetachAllRows();
                EndColumnResize();
                ClearDragState();
                ClearHeaderDragState();
            }
        };
    }

    private void CustomTableViewLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeService.ThemeChanged += AppThemeServiceThemeChanged;
        RefreshTheme("loaded");
    }

    private void CustomTableViewUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeService.ThemeChanged -= AppThemeServiceThemeChanged;
    }

    private void AppThemeServiceThemeChanged(object sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshTheme("theme_changed");
            return;
        }
        Dispatcher.BeginInvoke(new Action(() => RefreshTheme("theme_changed")));
    }

    private void RefreshTheme(string reason)
    {
        CustomTablePalette.Invalidate();
        cellValueCache.Clear();
        surface.ClearTextLayoutCache();
        ApplyOverlayTheme();
        RequestRedraw(reason);
    }

    private void ApplyOverlayTheme()
    {
        CustomTablePalette palette = CustomTablePalette.Current;
        if (editSuggestionPopup.Child is Border border)
        {
            border.Background = palette.RowBackground;
            border.BorderBrush = palette.HeaderBorder.Brush;
        }
        editSuggestionListBox.Background = palette.RowBackground;
        editSuggestionListBox.Foreground = palette.DefaultForeground;
    }

    public event EventHandler<CustomTableFirstRenderCompletedEventArgs> FirstRenderCompleted;

    public event EventHandler<CustomTableSortRequestedEventArgs> SortRequested;

    public event EventHandler<CustomTableSelectionChangedEventArgs> SelectionChanged;

    public event EventHandler<CustomTableRowRequestedEventArgs> RowActivated;

    public event EventHandler<CustomTableRowRequestedEventArgs> RowContextMenuRequested;

    public event EventHandler<CustomTableHeaderRequestedEventArgs> HeaderContextMenuRequested;

    public event EventHandler<CustomTableCellEditBeginningEventArgs> CellEditBeginning;

    public event EventHandler<CustomTableCellActionRequestedEventArgs> CellActionRequested;

    public event EventHandler<CustomTableCellEditEndedEventArgs> CellEditEnded;

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

    public CustomTableColumnSettings ColumnsSettings
    {
        get => (CustomTableColumnSettings)GetValue(ColumnsSettingsProperty);
        set => SetValue(ColumnsSettingsProperty, value);
    }

    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get => (PlaylistSummaryColumnSettings)GetValue(PlaylistSummaryColumnsSettingsProperty);
        set => SetValue(PlaylistSummaryColumnsSettingsProperty, value);
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

    public FontFamily ScoreFontFamily
    {
        get => (FontFamily)GetValue(ScoreFontFamilyProperty);
        set => SetValue(ScoreFontFamilyProperty, value);
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

    internal bool IsCurrentCell(int rowIndex, CustomTableColumn column)
    {
        return currentCellHit?.Kind == CustomTableHitKind.Cell
            && currentCellHit.RowIndex == rowIndex
            && column != null
            && string.Equals(currentCellHit.Column?.Id, column.Id, StringComparison.Ordinal);
    }

    internal bool IsColumnReorderPreviewActive => isReorderingColumn && !double.IsNaN(reorderPreviewInsertX);

    internal double ColumnReorderPreviewInsertX => reorderPreviewInsertX;

    internal bool IsItemsSourceSwapPending => suppressColumnRedrawUntilItemsSourceChanged;

    internal bool IsReorderSourceColumn(CustomTableColumn column)
    {
        return isReorderingColumn
            && column != null
            && pendingHeaderHit?.Column != null
            && ReferenceEquals(pendingHeaderHit.Column, column);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.ClearPendingItemsSourceSwapColumnRedrawSuppression();
        view.DetachCollectionChanged(e.OldValue as INotifyCollectionChanged);
        view.AttachCollectionChanged(e.NewValue as INotifyCollectionChanged);
        view.MarkItemsApplied();
        view.InvalidateAllCellValues();
        view.CoerceSelectionToCurrentRows();
        view.InvalidateColumnLayoutSnapshot();
        view.UpdateScrollBars();
        view.UpdateVisibleRowSubscriptions("items_source_changed", logAlways: true);
        view.RequestRedraw("items_source_changed");
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.InvalidateColumnLayoutSnapshot();
        view.InvalidateColumnCellValues();
        view.UpdateScrollBars();
        if (view.suppressColumnRedrawUntilItemsSourceChanged)
        {
            return;
        }
        view.RequestRedraw("columns_changed");
    }

    private static void OnColumnsSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.DetachColumnLayoutHandlers();
        view.AttachColumnLayoutHandlers();
        view.RebuildColumns();
    }

    private static void OnPlaylistSummaryColumnsSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.DetachColumnLayoutHandlers();
        view.AttachColumnLayoutHandlers();
        view.RebuildColumns();
    }

    private static void OnLayoutMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.InvalidateColumnLayoutSnapshot();
        view.UpdateScrollBars();
        view.UpdateVisibleRowSubscriptions("layout_metric_changed");
        view.RequestRedraw("layout_metric_changed");
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
        view.RequestRedraw("selection");
    }

    private static void OnSortStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.RequestRedraw("sort_state");
    }

    private static void OnScoreFontFamilyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        CustomTableView view = (CustomTableView)d;
        view.surface?.ClearTextLayoutCache();
        view.RequestRedraw("layout_metric_changed");
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
        CommitActiveEdit();
        currentCellHit = null;
        ClearPendingItemsSourceSwapColumnRedrawSuppression();
        MarkItemsApplied();
        InvalidateAllCellValues();
        CoerceSelectionToCurrentRows();
        InvalidateColumnLayoutSnapshot();
        UpdateScrollBars();
        UpdateVisibleRowSubscriptions(GetCollectionChangedSubscriptionReason(e), logAlways: e?.Action == NotifyCollectionChangedAction.Reset);
        RequestRedraw("items_source_changed");
    }

    private void AttachColumnLayoutHandlers()
    {
        IEnumerable<ICustomTableColumnLayout> layouts = PlaylistSummaryColumnsSettings != null
            ? CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(PlaylistSummaryColumnsSettings).Cast<ICustomTableColumnLayout>()
            : CustomTableColumnFactory.EnumerateMainColumnLayouts(ColumnsSettings).Cast<ICustomTableColumnLayout>();
        foreach (ICustomTableColumnLayout layoutCandidate in layouts)
        {
            if (layoutCandidate is INotifyPropertyChanged layout && !subscribedColumnLayouts.Contains(layout))
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
        CommitActiveEdit();
        currentCellHit = null;
        RebuildColumns(!string.Equals(e.PropertyName, nameof(ICustomTableColumnLayout.Width), StringComparison.Ordinal));
    }

    private void RebuildColumns(bool markItemsApplied = true)
    {
        Columns = PlaylistSummaryColumnsSettings != null
            ? CustomTableColumnFactory.CreatePlaylistSummaryColumns(PlaylistSummaryColumnsSettings).ToArray()
            : CustomTableColumnFactory.CreateMainColumns(ColumnsSettings).ToArray();
        InvalidateColumnLayoutSnapshot();
        InvalidateColumnCellValues();
        if (markItemsApplied && !suppressColumnRedrawUntilItemsSourceChanged)
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
        preparationRenderLogged = false;
    }

    private void VerticalScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CommitActiveEdit();
        CloseCellToolTip();
        UpdateVisibleRowSubscriptions("vertical_scroll");
        RequestRedraw("scroll_vertical");
    }

    private void HorizontalScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CommitActiveEdit();
        CloseCellToolTip();
        RequestRedraw("scroll_horizontal");
    }

    private void UpdateScrollBars()
    {
        int rowCount = RowCount;
        int fullVisibleRows = CalculateFullVisibleRowCapacity();
        verticalScrollBar.ViewportSize = fullVisibleRows;
        verticalScrollBar.LargeChange = Math.Max(1, fullVisibleRows);
        verticalScrollBar.Maximum = CalculateVerticalScrollMaximum(rowCount, fullVisibleRows);
        verticalScrollBar.Visibility = rowCount > fullVisibleRows ? Visibility.Visible : Visibility.Collapsed;
        if (verticalScrollBar.Value > verticalScrollBar.Maximum)
        {
            verticalScrollBar.Value = verticalScrollBar.Maximum;
        }
        double extentWidth = GetColumnLayoutSnapshot().ExtentWidth;
        double viewportWidth = Math.Max(0d, surface.ActualWidth);
        horizontalScrollBar.ViewportSize = viewportWidth;
        horizontalScrollBar.LargeChange = Math.Max(1d, viewportWidth);
        horizontalScrollBar.Maximum = Math.Max(0d, extentWidth - viewportWidth);
        horizontalScrollBar.Visibility = extentWidth > viewportWidth + 0.5d ? Visibility.Visible : Visibility.Collapsed;
        if (horizontalScrollBar.Value > horizontalScrollBar.Maximum)
        {
            horizontalScrollBar.Value = horizontalScrollBar.Maximum;
        }
        scrollBarCorner.Visibility = verticalScrollBar.Visibility == Visibility.Visible && horizontalScrollBar.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    internal int CalculateFullVisibleRowCapacity()
    {
        return CalculateFullVisibleRowCapacity(CalculateBodyHeight(), RowHeight);
    }

    internal int CalculateDrawableRowCapacity()
    {
        return CalculateDrawableRowCapacity(CalculateBodyHeight(), RowHeight);
    }

    internal static int CalculateFullVisibleRowCapacity(double bodyHeight, double rowHeight)
    {
        return Math.Max(1, (int)Math.Floor(Math.Max(0d, bodyHeight) / Math.Max(1d, rowHeight)));
    }

    internal static int CalculateDrawableRowCapacity(double bodyHeight, double rowHeight)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0d, bodyHeight) / Math.Max(1d, rowHeight)));
    }

    internal static int CalculateVerticalScrollMaximum(int rowCount, int fullVisibleRows)
    {
        return Math.Max(0, Math.Max(0, rowCount) - Math.Max(1, fullVisibleRows));
    }

    private double CalculateBodyHeight()
    {
        return Math.Max(0d, surface.ActualHeight - HeaderHeight);
    }

    internal CustomTableColumnLayoutSnapshot GetColumnLayoutSnapshot()
    {
        IReadOnlyList<CustomTableColumn> columns = VisibleColumns;
        double horizontalOffset = HorizontalOffset;
        double viewportWidth = Math.Max(0d, surface.ActualWidth);
        if (columnLayoutSnapshot == null || !columnLayoutSnapshot.Matches(columns, horizontalOffset, viewportWidth))
        {
            columnLayoutSnapshot = CustomTableColumnLayoutSnapshot.Create(columns, horizontalOffset, viewportWidth);
        }
        return columnLayoutSnapshot;
    }

    private void InvalidateColumnLayoutSnapshot()
    {
        columnLayoutSnapshot = null;
    }

    internal CustomTableCellValue GetCellValue(object row, CustomTableColumn column, out bool cacheHit)
    {
        return cellValueCache.GetOrCreate(row, column, rowValueGeneration, columnValueGeneration, out cacheHit);
    }

    internal bool HasHighlightedWarning(object row)
    {
        return cellValueCache.HasHighlightedWarning(row);
    }

    private void InvalidateAllCellValues()
    {
        unchecked
        {
            rowValueGeneration++;
        }
        pendingRowInvalidations.Clear();
        cellValueCache.Clear();
    }

    private void InvalidateColumnCellValues()
    {
        unchecked
        {
            columnValueGeneration++;
        }
        cellValueCache.Clear();
    }

    private void EnqueueRowInvalidation(INotifyPropertyChanged row)
    {
        if (row == null)
        {
            return;
        }
        pendingRowInvalidations.Enqueue(row);
        rowPropertyChangedRedrawScheduler.Request();
    }

    private void FlushPendingRowInvalidations()
    {
        object[] rows = pendingRowInvalidations.Drain();
        if (rows.Length == 0)
        {
            return;
        }
        foreach (object row in rows)
        {
            cellValueCache.InvalidateRow(row);
        }
        RequestRedraw("row_property_changed");
    }

    private void RequestRedraw(string reason)
    {
        pendingRedrawReason = NormalizeRedrawReason(reason);
        surface.InvalidateVisual();
    }

    internal string ConsumeRedrawReason()
    {
        string reason = pendingRedrawReason;
        pendingRedrawReason = "implicit";
        return NormalizeRedrawReason(reason);
    }

    private static string NormalizeRedrawReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
    }

    private void UpdateVisibleRowSubscriptions(string reason, bool logAlways = false)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int firstIndex = 0;
        int requestedCount = 0;
        if (IsVisible)
        {
            int rowCount = RowCount;
            if (rowCount > 0)
            {
                int firstVisibleIndex = FirstVisibleRowIndex;
                int drawableRows = CalculateDrawableRowCapacity();
                firstIndex = Math.Max(0, firstVisibleIndex - RowSubscriptionOverscan);
                int lastExclusive = Math.Min(rowCount, firstVisibleIndex + drawableRows + RowSubscriptionOverscan);
                requestedCount = Math.Max(0, lastExclusive - firstIndex);
                if (firstIndex != lastVisibleSubscriptionFirstIndex || requestedCount != lastVisibleSubscriptionCount)
                {
                    InvalidateAllCellValues();
                    lastVisibleSubscriptionFirstIndex = firstIndex;
                    lastVisibleSubscriptionCount = requestedCount;
                }
                rowChangeTracker.ReplaceVisibleRows(ItemsSource, firstIndex, requestedCount);
            }
            else
            {
                lastVisibleSubscriptionFirstIndex = -1;
                lastVisibleSubscriptionCount = -1;
                rowChangeTracker.DetachAllRows();
            }
        }
        else
        {
            lastVisibleSubscriptionFirstIndex = -1;
            lastVisibleSubscriptionCount = -1;
            rowChangeTracker.DetachAllRows();
        }
        stopwatch.Stop();
        if (logAlways || stopwatch.ElapsedMilliseconds >= RowSubscriptionSlowLogThresholdMs)
        {
            installPerformanceLogger?.Info(
                "custom_table_row_subscription reason=" + reason
                + " firstIndex=" + firstIndex
                + " requestedCount=" + requestedCount
                + " subscribedRowCount=" + rowChangeTracker.SubscribedRowCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    private static string GetCollectionChangedSubscriptionReason(NotifyCollectionChangedEventArgs e)
    {
        return e == null ? "collection_changed" : "collection_" + e.Action.ToString().ToLowerInvariant();
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
            RequestRedraw("selection");
        }
    }

    public void RefreshDisplay()
    {
        ClearPendingItemsSourceSwapColumnRedrawSuppression();
        InvalidateAllCellValues();
        RequestRedraw("refresh_display");
    }

    public void PrepareForItemsSourceSwap()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long commitStartMs = stopwatch.ElapsedMilliseconds;
        CommitActiveEdit();
        long commitActiveEditMs = stopwatch.ElapsedMilliseconds - commitStartMs;
        long clearHitStartMs = stopwatch.ElapsedMilliseconds;
        currentCellHit = null;
        long clearHitMs = stopwatch.ElapsedMilliseconds - clearHitStartMs;
        long suppressStartMs = stopwatch.ElapsedMilliseconds;
        BeginPendingItemsSourceSwapColumnRedrawSuppression();
        long suppressMs = stopwatch.ElapsedMilliseconds - suppressStartMs;
        stopwatch.Stop();
        installPerformanceLogger?.Info(
            "custom_table_prepare_items_source_swap totalMs=" + stopwatch.ElapsedMilliseconds
            + " commitActiveEditMs=" + commitActiveEditMs
            + " clearHitMs=" + clearHitMs
            + " suppressColumnRedrawMs=" + suppressMs);
    }

    private void BeginPendingItemsSourceSwapColumnRedrawSuppression()
    {
        suppressColumnRedrawUntilItemsSourceChanged = true;
    }

    private void ClearPendingItemsSourceSwapColumnRedrawSuppression()
    {
        suppressColumnRedrawUntilItemsSourceChanged = false;
    }

    public void ScrollSelectedRowIntoView()
    {
        int rowIndex = selectionModel.CurrentIndex >= 0 ? selectionModel.CurrentIndex : SelectedIndex;
        EnsureRowVisible(rowIndex);
        RequestRedraw("scroll_into_view");
    }

    internal CustomTableHitTestResult HitTestTable(Point surfacePoint)
    {
        if (surfacePoint.X < 0d || surfacePoint.Y < 0d || surfacePoint.X >= surface.ActualWidth || surfacePoint.Y >= surface.ActualHeight)
        {
            return CreateEmptyHit();
        }
        double horizontalOffset = HorizontalOffset;
        double tableX = surfacePoint.X + horizontalOffset;
        CustomTableColumnLayoutSnapshot layout = GetColumnLayoutSnapshot();
        if (surfacePoint.Y < HeaderHeight)
        {
            if (layout.TryResolveResizeColumn(surfacePoint.X, ColumnResizeHitTestMargin, out CustomTableColumnLayoutEntry resizeEntry, out Rect resizeRect))
            {
                return new CustomTableHitTestResult(CustomTableHitKind.HeaderResize, -1, null, resizeEntry.Column, resizeEntry.ColumnIndex, resizeRect);
            }
            bool hasHeaderColumn = layout.TryResolveColumn(tableX, out CustomTableColumnLayoutEntry headerEntry);
            Rect headerRect = hasHeaderColumn ? headerEntry.CreateVisibleRect(horizontalOffset, surface.ActualWidth, 0d, HeaderHeight) : new Rect(0d, 0d, surface.ActualWidth, HeaderHeight);
            return new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, headerEntry.Column, hasHeaderColumn ? headerEntry.ColumnIndex : -1, headerRect);
        }
        bool hasColumn = layout.TryResolveColumn(tableX, out CustomTableColumnLayoutEntry cellEntry);
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
        Rect cellRect = cellEntry.CreateVisibleRect(horizontalOffset, surface.ActualWidth, rowY, rowHeight);
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], cellEntry.Column, cellEntry.ColumnIndex, cellRect);
    }

    private void CustomTableViewPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendantOfActiveEditControl(e.OriginalSource as DependencyObject))
        {
            return;
        }
        CommitActiveEdit();
        CloseCellToolTip();
        if (IsDescendantOfScrollChrome(e.OriginalSource as DependencyObject))
        {
            return;
        }
        Focus();
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.HeaderResize)
        {
            ClearPendingSingleSelectionOnMouseUp();
            CommitActiveEdit();
            BeginColumnResize(hit, e.GetPosition(surface).X);
            e.Handled = true;
            return;
        }
        if (hit.Kind == CustomTableHitKind.Header)
        {
            ClearPendingSingleSelectionOnMouseUp();
            CommitActiveEdit();
            pendingHeaderHit = hit;
            headerDragStartPoint = e.GetPosition(surface);
            isReorderingColumn = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (hit.Kind == CustomTableHitKind.Cell)
        {
            bool plainSingleClick = e.ClickCount == 1
                && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.None;
            bool delaySingleSelectionUntilMouseUp = plainSingleClick
                && selectionModel.SelectedIndices.Count > 1
                && selectionModel.IsSelected(hit.RowIndex);
            bool shouldBeginEditOnRepeatClick = e.ClickCount == 1
                && IsSameEditableCell(currentCellHit, hit)
                && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.None;
            currentCellHit = hit;
            bool changed = ApplyMouseSelection(hit.RowIndex);
            if (changed)
            {
                RaiseSelectionChanged();
            }
            rowDragStartPoint = e.ClickCount == 1 ? e.GetPosition(this) : null;
            rowDragStartHit = e.ClickCount == 1 ? hit : null;
            pendingSingleSelectionOnMouseUpRowIndex = delaySingleSelectionUntilMouseUp ? hit.RowIndex : -1;
            if (e.ClickCount == 1
                && IsActionCell(hit)
                && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.None)
            {
                ClearDragState();
                CellActionRequested?.Invoke(this, new CustomTableCellActionRequestedEventArgs(hit));
                e.Handled = true;
                return;
            }
            if (shouldBeginEditOnRepeatClick && BeginCellEdit(hit, null))
            {
                ClearDragState();
                e.Handled = true;
                return;
            }
            if (e.ClickCount >= 2)
            {
                ClearDragState();
                RowActivated?.Invoke(this, new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true));
            }
            e.Handled = true;
            return;
        }
        ClearDragState();
    }

    private static bool IsActionCell(CustomTableHitTestResult hit)
    {
        if (hit?.Column == null)
        {
            return false;
        }
        switch (hit.Column.CellKind)
        {
            case CustomTableCellKind.DownloadIcon:
            case CustomTableCellKind.ActionText:
                return !string.IsNullOrWhiteSpace(hit.Column.GetText(hit.Row));
            case CustomTableCellKind.CheckBox:
                return hit.Column.GetChecked(hit.Row).HasValue;
            default:
                return false;
        }
    }

    private void CustomTableViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendantOfActiveEditControl(e.OriginalSource as DependencyObject))
        {
            return;
        }
        CommitActiveEdit();
        CloseCellToolTip();
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.HeaderResize)
        {
            hit = new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, hit.Column, hit.ColumnIndex, hit.CellRect);
        }
        if (hit.Kind == CustomTableHitKind.Header)
        {
            if (IsStatusColumn(hit.Column))
            {
                e.Handled = true;
                return;
            }
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
                RequestRedraw("selection");
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
        if (pendingHeaderHit?.Kind == CustomTableHitKind.Header && headerDragStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!isReorderingColumn && IsDragging(headerDragStartPoint.Value, position) && pendingHeaderHit.Column?.CanReorder == true)
            {
                isReorderingColumn = true;
                Cursor = Cursors.SizeWE;
            }
            if (isReorderingColumn)
            {
                UpdateColumnReorderPreview(position);
                e.Handled = true;
                return;
            }
        }
        if (rowDragStartHit?.Kind == CustomTableHitKind.Cell && rowDragStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            if (IsDragging(rowDragStartPoint.Value, e.GetPosition(this)))
            {
                StartRowDrag();
                e.Handled = true;
                return;
            }
        }
        CustomTableHitTestResult hit = HitTestTable(position);
        Cursor = hit.Kind == CustomTableHitKind.HeaderResize ? Cursors.SizeWE : null;
        UpdateCellToolTip(hit);
    }

    private void CustomTableViewPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (isResizingColumn)
        {
            ClearPendingSingleSelectionOnMouseUp();
            EndColumnResize();
            e.Handled = true;
            return;
        }
        if (pendingHeaderHit?.Kind == CustomTableHitKind.Header)
        {
            if (isReorderingColumn)
            {
                CompleteColumnReorder(e.GetPosition(surface));
            }
            else
            {
                RequestSort(pendingHeaderHit.Column);
            }
            ClearHeaderDragState();
            e.Handled = true;
            return;
        }
        bool committedPendingSingleSelection = CommitPendingSingleSelectionOnMouseUp();
        ClearDragState();
        if (committedPendingSingleSelection)
        {
            e.Handled = true;
        }
    }

    private void CustomTableViewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CommitActiveEdit();
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
        if (activeEditor != null)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (editSuggestionPopup.IsOpen)
                    {
                        CloseEditSuggestions();
                        e.Handled = true;
                        return;
                    }
                    CancelActiveEdit();
                    e.Handled = true;
                    return;
                case Key.Return:
                    if (CommitSelectedEditSuggestion())
                    {
                        e.Handled = true;
                        return;
                    }
                    CommitActiveEdit();
                    e.Handled = true;
                    return;
                case Key.Tab:
                    CommitActiveEdit();
                    e.Handled = true;
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    return;
                case Key.Down:
                    if (MoveEditSuggestionSelection(1))
                    {
                        e.Handled = true;
                        return;
                    }
                    break;
                case Key.Up:
                    if (MoveEditSuggestionSelection(-1))
                    {
                        e.Handled = true;
                        return;
                    }
                    break;
            }
            return;
        }
        switch (ResolveKeyboardCommand(e.Key, Keyboard.Modifiers))
        {
            case CustomTableKeyboardCommand.SelectAllRows:
                e.Handled = SelectAllRows();
                return;
            case CustomTableKeyboardCommand.CopyCurrentCell:
                e.Handled = CopyCurrentCellToClipboard();
                return;
            case CustomTableKeyboardCommand.CopySelectedRowsTsv:
                e.Handled = CopySelectedRowsToClipboard();
                return;
        }
        switch (e.Key)
        {
            case Key.F2:
                e.Handled = TryBeginEditCurrentCell(null);
                break;
            case Key.Up:
                e.Handled = MoveKeyboardSelection(-1, (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
                break;
            case Key.Down:
                e.Handled = MoveKeyboardSelection(1, (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
                break;
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

    internal static CustomTableKeyboardCommand ResolveKeyboardCommand(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.A when modifiers == ModifierKeys.Control:
                return CustomTableKeyboardCommand.SelectAllRows;
            case Key.C when modifiers == ModifierKeys.Control:
                return CustomTableKeyboardCommand.CopyCurrentCell;
            case Key.C when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                return CustomTableKeyboardCommand.CopySelectedRowsTsv;
            default:
                return CustomTableKeyboardCommand.None;
        }
    }

    private void CustomTableViewPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (activeEditor != null || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }
        if (TryBeginEditCurrentCell(e.Text))
        {
            e.Handled = true;
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
                : selectionModel.SelectForLeftMouseDownDragCandidate(rowIndex);
        UpdateSelectedIndexFromSelectionModel();
        RequestRedraw("selection");
        return changed;
    }

    private bool CommitPendingSingleSelectionOnMouseUp()
    {
        int rowIndex = pendingSingleSelectionOnMouseUpRowIndex;
        ClearPendingSingleSelectionOnMouseUp();
        if (rowIndex < 0)
        {
            return false;
        }
        selectionModel.SetItemCount(RowCount);
        bool changed = selectionModel.SelectSingle(rowIndex);
        UpdateSelectedIndexFromSelectionModel();
        EnsureCurrentCellForCurrentRow();
        if (changed)
        {
            RaiseSelectionChanged();
        }
        RequestRedraw("selection");
        return changed;
    }

    private void ClearPendingSingleSelectionOnMouseUp()
    {
        pendingSingleSelectionOnMouseUpRowIndex = -1;
    }

    private bool SelectAllRows()
    {
        selectionModel.SetItemCount(RowCount);
        bool changed = selectionModel.SelectAll();
        UpdateSelectedIndexFromSelectionModel();
        EnsureRowVisible(selectionModel.CurrentIndex);
        EnsureCurrentCellForCurrentRow();
        if (changed)
        {
            RaiseSelectionChanged();
        }
        RequestRedraw("selection");
        return RowCount > 0;
    }

    private bool MoveKeyboardSelection(int delta, bool extendRange)
    {
        selectionModel.SetItemCount(RowCount);
        bool changed = extendRange ? selectionModel.ExtendRangeBy(delta) : selectionModel.MoveCurrent(delta);
        UpdateSelectedIndexFromSelectionModel();
        EnsureRowVisible(selectionModel.CurrentIndex);
        EnsureCurrentCellForCurrentRow();
        if (changed)
        {
            RaiseSelectionChanged();
        }
        RequestRedraw("selection");
        return RowCount > 0;
    }

    private bool CopySelectedRowsToClipboard()
    {
        string text = CustomTableDataTransfer.BuildTsv(GetSelectedRowsSnapshot(), VisibleColumns);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        Clipboard.SetText(text);
        return true;
    }

    private bool CopyCurrentCellToClipboard()
    {
        if (currentCellHit?.Kind != CustomTableHitKind.Cell || currentCellHit.Column == null)
        {
            return false;
        }
        IList rows = ItemsSource;
        int rowIndex = currentCellHit.RowIndex;
        if (rows == null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return false;
        }
        CustomTableColumn column = VisibleColumns.FirstOrDefault(candidate =>
            candidate != null
            && candidate.IsVisible
            && string.Equals(candidate.Id, currentCellHit.Column.Id, StringComparison.Ordinal));
        if (column == null)
        {
            return false;
        }
        string text = CustomTableDataTransfer.BuildCellText(rows[rowIndex], column);
        Clipboard.SetText(text);
        return true;
    }

    private void EnsureCurrentCellForCurrentRow()
    {
        int rowIndex = selectionModel.CurrentIndex;
        if (rowIndex < 0)
        {
            currentCellHit = null;
            return;
        }
        string columnId = currentCellHit?.Column?.Id;
        if (!TryCreateCellHit(rowIndex, columnId, out CustomTableHitTestResult hit))
        {
            CustomTableColumn firstColumn = VisibleColumns.FirstOrDefault();
            if (firstColumn != null)
            {
                TryCreateCellHit(rowIndex, firstColumn.Id, out hit);
            }
        }
        currentCellHit = hit;
    }

    private void EnsureRowVisible(int rowIndex)
    {
        if (rowIndex < 0 || verticalScrollBar.Visibility != Visibility.Visible)
        {
            return;
        }
        int first = FirstVisibleRowIndex;
        int capacity = Math.Max(1, CalculateFullVisibleRowCapacity());
        if (rowIndex < first)
        {
            verticalScrollBar.Value = Math.Max(verticalScrollBar.Minimum, rowIndex);
        }
        else if (rowIndex >= first + capacity)
        {
            verticalScrollBar.Value = Math.Min(verticalScrollBar.Maximum, rowIndex - capacity + 1);
        }
    }

    private void StartRowDrag()
    {
        if (rowDragStartHit?.Kind != CustomTableHitKind.Cell)
        {
            return;
        }
        IReadOnlyList<object> rows = GetSelectedRowsSnapshot();
        if (rows.Count == 0 || !rows.Contains(rowDragStartHit.Row))
        {
            return;
        }
        DataObject dataObject = CustomTableDataTransfer.CreateSelectedRowsDataObject(rows);
        CloseCellToolTip();
        rowDragAdorner = new DragAdorner(this, CreateRowDragGhost(rows.Count), new Vector(12d, 12d));
        rowDragAdorner.Position = WPFUtil.GetMousePosition(this);
        ClearDragState();
        try
        {
            DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            rowDragAdorner?.Remove();
            rowDragAdorner = null;
        }
    }

    private static UIElement CreateRowDragGhost(int rowCount)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x88, 0xAA)),
            BorderThickness = new Thickness(1d),
            Padding = new Thickness(8d, 3d, 8d, 3d),
            Child = new TextBlock
            {
                Text = rowCount <= 1 ? "1 row" : rowCount.ToString(CultureInfo.InvariantCulture) + " rows",
                Foreground = Brushes.Black,
                FontSize = 11d
            }
        };
    }

    private void CustomTableViewQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (rowDragAdorner != null)
        {
            rowDragAdorner.Position = WPFUtil.GetMousePosition(this);
        }
    }

    private void CompleteColumnReorder(Point surfacePoint)
    {
        CustomTableColumn sourceColumn = pendingHeaderHit?.Column;
        if (sourceColumn?.CanReorder != true)
        {
            return;
        }
        double tableX = surfacePoint.X + HorizontalOffset;
        GetColumnLayoutSnapshot().TryResolveColumn(tableX, out CustomTableColumnLayoutEntry targetEntry);
        CustomTableColumn targetColumn = targetEntry.Column;
        double targetColumnX = targetEntry.TableX;
        bool insertAfter = targetColumn != null && tableX >= targetColumnX + targetColumn.Width / 2d;
        if (CustomTableDataTransfer.TryReorderVisibleColumns(VisibleColumns, sourceColumn, targetColumn, insertAfter))
        {
            RebuildColumns(markItemsApplied: false);
        }
    }

    private void UpdateColumnReorderPreview(Point surfacePoint)
    {
        double insertTableX = CalculateColumnReorderInsertTableX(surfacePoint);
        double nextInsertX = Math.Max(0d, Math.Min(surface.ActualWidth, insertTableX - HorizontalOffset));
        if (!AreClose(reorderPreviewInsertX, nextInsertX))
        {
            reorderPreviewInsertX = nextInsertX;
            RequestRedraw("column_reorder");
        }
    }

    private double CalculateColumnReorderInsertTableX(Point surfacePoint)
    {
        IReadOnlyList<CustomTableColumn> columns = VisibleColumns;
        double tableX = surfacePoint.X + HorizontalOffset;
        CustomTableColumnLayoutSnapshot layout = GetColumnLayoutSnapshot();
        if (!layout.TryResolveColumn(tableX, out CustomTableColumnLayoutEntry targetEntry))
        {
            return Math.Max(GetLeadingFixedColumnWidth(columns), layout.ExtentWidth);
        }
        CustomTableColumn targetColumn = targetEntry.Column;
        double targetColumnX = targetEntry.TableX;
        bool insertAfter = targetColumn != null && tableX >= targetColumnX + targetColumn.Width / 2d;
        double insertTableX = insertAfter ? targetColumnX + targetColumn.Width : targetColumnX;
        return Math.Max(GetLeadingFixedColumnWidth(columns), insertTableX);
    }

    private static double GetLeadingFixedColumnWidth(IReadOnlyList<CustomTableColumn> columns)
    {
        double width = 0d;
        if (columns == null)
        {
            return width;
        }
        foreach (CustomTableColumn column in columns)
        {
            if (column?.CanReorder == true)
            {
                break;
            }
            width += column?.Width ?? 0d;
        }
        return width;
    }

    private void ClearDragState()
    {
        rowDragStartPoint = null;
        rowDragStartHit = null;
        ClearPendingSingleSelectionOnMouseUp();
    }

    private void ClearHeaderDragState()
    {
        pendingHeaderHit = null;
        headerDragStartPoint = null;
        isReorderingColumn = false;
        reorderPreviewInsertX = double.NaN;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Cursor = null;
        RequestRedraw("column_reorder");
    }

    private static bool AreClose(double left, double right)
    {
        if (double.IsNaN(left) || double.IsNaN(right))
        {
            return double.IsNaN(left) && double.IsNaN(right);
        }
        return Math.Abs(left - right) < 0.5d;
    }

    private static bool IsDragging(Point start, Point current)
    {
        return Math.Abs(start.X - current.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(start.Y - current.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private static bool IsSameEditableCell(CustomTableHitTestResult current, CustomTableHitTestResult next)
    {
        return current?.Kind == CustomTableHitKind.Cell
            && next?.Kind == CustomTableHitKind.Cell
            && current.RowIndex == next.RowIndex
            && string.Equals(current.Column?.Id, next.Column?.Id, StringComparison.Ordinal)
            && next.Column.EditOnRepeatClick
            && !string.IsNullOrWhiteSpace(next.Column?.EditPropertyName);
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
        RequestRedraw("sort_state");
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

    private bool TryBeginEditCurrentCell(string replacementText)
    {
        if (currentCellHit == null || currentCellHit.Kind != CustomTableHitKind.Cell)
        {
            return false;
        }
        if (!TryCreateCellHit(currentCellHit.RowIndex, currentCellHit.Column?.Id, out CustomTableHitTestResult hit))
        {
            return false;
        }
        return BeginCellEdit(hit, replacementText);
    }

    private bool BeginCellEdit(CustomTableHitTestResult hit, string replacementText)
    {
        if (hit?.Kind != CustomTableHitKind.Cell || string.IsNullOrWhiteSpace(hit.Column?.EditPropertyName))
        {
            return false;
        }
        CommitActiveEdit();
        CustomTableCellEditBeginningEventArgs beginningArgs = new CustomTableCellEditBeginningEventArgs(hit, hit.Column.EditPropertyName);
        CellEditBeginning?.Invoke(this, beginningArgs);
        if (beginningArgs.Cancel)
        {
            return false;
        }
        Rect rect = hit.CellRect;
        if (rect.Width <= 2d || rect.Height <= 2d)
        {
            return false;
        }
        Rect editorRect = CreateEditorRect(hit.Column, rect);
        TextBox textBox = new TextBox
        {
            Text = replacementText ?? hit.Column.GetEditText(hit.Row),
            TextAlignment = hit.Column.CellKind == CustomTableCellKind.DownloadIcon ? TextAlignment.Left : hit.Column.Alignment,
            TextWrapping = hit.Column.EditTextWrapping ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = false,
            BorderThickness = new Thickness(1d),
            BorderBrush = CustomTablePalette.Current.HeaderBorder.Brush,
            Background = CustomTablePalette.Current.RowBackground,
            Foreground = CustomTablePalette.Current.DefaultForeground,
            Padding = new Thickness(0d),
            Margin = new Thickness(0d),
            VerticalContentAlignment = VerticalAlignment.Center,
            DataContext = hit.Row
        };
        textBox.LostKeyboardFocus += ActiveEditorLostKeyboardFocus;
        Canvas.SetLeft(textBox, editorRect.Left);
        Canvas.SetTop(textBox, editorRect.Top);
        textBox.Width = editorRect.Width;
        textBox.Height = editorRect.Height;
        activeEditHit = hit;
        activeEditor = textBox;
        editorLayer.Children.Add(textBox);
        UpdateEditSuggestions();
        textBox.Focus();
        if (replacementText == null)
        {
            textBox.SelectAll();
        }
        else
        {
            textBox.CaretIndex = textBox.Text.Length;
        }
        return true;
    }

    private Rect CreateEditorRect(CustomTableColumn column, Rect cellRect)
    {
        double width = column?.EditOverlayWidth.HasValue == true ? Math.Max(cellRect.Width, column.EditOverlayWidth.Value) : cellRect.Width;
        width = Math.Min(width, Math.Max(cellRect.Width, surface.ActualWidth - cellRect.Left));
        return new Rect(cellRect.Left, cellRect.Top, width, cellRect.Height);
    }

    private void ActiveEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!completingEdit && !completingSuggestionSelection && activeEditor != null && !activeEditor.IsKeyboardFocusWithin && !editSuggestionListBox.IsKeyboardFocusWithin)
        {
            CommitActiveEdit();
        }
    }

    private bool CommitActiveEdit()
    {
        return CompleteActiveEdit(commit: true);
    }

    private bool CancelActiveEdit()
    {
        return CompleteActiveEdit(commit: false);
    }

    private bool CompleteActiveEdit(bool commit)
    {
        if (activeEditor == null || completingEdit)
        {
            return false;
        }
        completingEdit = true;
        try
        {
            TextBox editor = activeEditor;
            CustomTableHitTestResult hit = activeEditHit;
            string text = editor.Text ?? string.Empty;
            CloseEditSuggestions();
            editor.LostKeyboardFocus -= ActiveEditorLostKeyboardFocus;
            editorLayer.Children.Remove(editor);
            activeEditor = null;
            activeEditHit = null;
            if (hit != null)
            {
                cellValueCache.InvalidateRow(hit.Row);
                CellEditEnded?.Invoke(this, new CustomTableCellEditEndedEventArgs(hit, hit.Column?.EditPropertyName, text, commit));
            }
            RequestRedraw("edit");
            return true;
        }
        finally
        {
            completingEdit = false;
        }
    }

    private void UpdateEditSuggestions()
    {
        IReadOnlyList<string> suggestions = activeEditHit?.Column?.GetEditSuggestions(activeEditHit.Row) ?? Array.Empty<string>();
        editSuggestionListBox.ItemsSource = suggestions;
        editSuggestionListBox.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
        if (activeEditor == null || suggestions.Count == 0)
        {
            CloseEditSuggestions();
            return;
        }
        editSuggestionPopup.PlacementTarget = activeEditor;
        if (editSuggestionPopup.Child is Border border)
        {
            border.MinWidth = activeEditor.Width;
        }
        editSuggestionPopup.IsOpen = true;
    }

    private void CloseEditSuggestions()
    {
        if (editSuggestionPopup.IsOpen)
        {
            editSuggestionPopup.IsOpen = false;
        }
        editSuggestionListBox.ItemsSource = null;
        editSuggestionListBox.SelectedIndex = -1;
    }

    private bool MoveEditSuggestionSelection(int delta)
    {
        if (activeEditor == null || editSuggestionListBox.Items.Count == 0)
        {
            return false;
        }
        if (!editSuggestionPopup.IsOpen)
        {
            editSuggestionPopup.IsOpen = true;
        }
        int nextIndex = editSuggestionListBox.SelectedIndex < 0 ? 0 : editSuggestionListBox.SelectedIndex + delta;
        nextIndex = Math.Max(0, Math.Min(editSuggestionListBox.Items.Count - 1, nextIndex));
        editSuggestionListBox.SelectedIndex = nextIndex;
        editSuggestionListBox.ScrollIntoView(editSuggestionListBox.SelectedItem);
        return true;
    }

    private bool CommitSelectedEditSuggestion()
    {
        if (!editSuggestionPopup.IsOpen || activeEditor == null || editSuggestionListBox.SelectedItem is not string selectedSuggestion || string.IsNullOrWhiteSpace(selectedSuggestion))
        {
            return false;
        }
        completingSuggestionSelection = true;
        try
        {
            activeEditor.Text = selectedSuggestion;
            activeEditor.CaretIndex = activeEditor.Text.Length;
            CommitActiveEdit();
            return true;
        }
        finally
        {
            completingSuggestionSelection = false;
        }
    }

    private void EditSuggestionListBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject source = e.OriginalSource as DependencyObject;
        ListBoxItem item = source == null ? null : FindVisualParent<ListBoxItem>(source);
        if (item != null)
        {
            editSuggestionListBox.SelectedItem = item.DataContext;
            if (CommitSelectedEditSuggestion())
            {
                e.Handled = true;
            }
        }
    }

    private void EditSuggestionListBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = CommitSelectedEditSuggestion();
        }
        else if (e.Key == Key.Escape)
        {
            CloseEditSuggestions();
            activeEditor?.Focus();
            e.Handled = true;
        }
    }

    private static T FindVisualParent<T>(DependencyObject source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T typed)
            {
                return typed;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private bool TryCreateCellHit(int rowIndex, string columnId, out CustomTableHitTestResult hit)
    {
        hit = null;
        if (rowIndex < FirstVisibleRowIndex || rowIndex >= FirstVisibleRowIndex + CalculateDrawableRowCapacity() || string.IsNullOrWhiteSpace(columnId))
        {
            return false;
        }
        IList rows = ItemsSource;
        if (rows == null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return false;
        }
        IReadOnlyList<CustomTableColumn> columns = VisibleColumns;
        double x = 0d;
        for (int i = 0; i < columns.Count; i++)
        {
            CustomTableColumn column = columns[i];
            double width = column?.Width ?? 0d;
            if (column != null && string.Equals(column.Id, columnId, StringComparison.Ordinal))
            {
                Rect rect = CreateVisibleCellRect(x, width, rowIndex);
                if (rect.Width <= 0d || rect.Height <= 0d)
                {
                    return false;
                }
                hit = new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, i, rect);
                return true;
            }
            x += width;
        }
        return false;
    }

    private bool IsDescendantOfActiveEditControl(DependencyObject source)
    {
        if (activeEditor == null && editSuggestionPopup.IsOpen == false)
        {
            return false;
        }
        while (source != null)
        {
            if (ReferenceEquals(source, activeEditor) || ReferenceEquals(source, editSuggestionListBox))
            {
                return true;
            }
            DependencyObject visualParent = source is Visual || source is Visual3D
                ? VisualTreeHelper.GetParent(source)
                : null;
            source = visualParent ?? LogicalTreeHelper.GetParent(source);
        }
        return false;
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
        Rect cellRect = column == null ? Rect.Empty : CreateVisibleCellRect(0d, column.Width, rowIndex);
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, column == null ? -1 : 0, cellRect);
    }

    private Rect CreateVisibleCellRect(double columnX, double columnWidth, int rowIndex)
    {
        double rowHeight = Math.Max(1d, RowHeight);
        double rowY = HeaderHeight + (rowIndex - FirstVisibleRowIndex) * rowHeight;
        double visibleHeight = Math.Min(rowHeight, Math.Max(0d, surface.ActualHeight - rowY));
        return CustomTableColumnLayout.CreateVisibleColumnRect(
            columnX,
            columnWidth,
            HorizontalOffset,
            surface.ActualWidth,
            rowY,
            visibleHeight);
    }

    private static bool IsStatusColumn(CustomTableColumn column)
    {
        return string.Equals(column?.Id, "Status", StringComparison.Ordinal);
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
            InvalidateColumnLayoutSnapshot();
            InvalidateColumnCellValues();
            UpdateScrollBars();
            RequestRedraw("column_resize");
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
        InvalidateColumnLayoutSnapshot();
        UpdateScrollBars();
        RequestRedraw("column_resize");
    }

    private void UpdateCellToolTip(CustomTableHitTestResult hit)
    {
        if (isResizingColumn || hit == null || hit.Kind != CustomTableHitKind.Cell || hit.Column == null)
        {
            CloseCellToolTip();
            return;
        }
        string tooltip = ResolveCellToolTip(hit);
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

    private string ResolveCellToolTip(CustomTableHitTestResult hit)
    {
        string explicitTooltip = hit.Column.GetTooltip(hit.Row);
        if (!string.IsNullOrWhiteSpace(explicitTooltip))
        {
            return explicitTooltip;
        }
        if (!hit.Column.AutoTrimTooltip)
        {
            return null;
        }
        CustomTableCellValue cellValue = GetCellValue(hit.Row, hit.Column, out _);
        if (cellValue.CellKind != CustomTableCellKind.Text || string.IsNullOrWhiteSpace(cellValue.Text))
        {
            return null;
        }
        double maxTextWidth = Math.Max(1d, hit.Column.Width - 4d);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool wouldTrim = CustomTableTextLayoutCache.WouldTrim(
            cellValue.Text,
            maxTextWidth,
            cellValue.TextStyle,
            ScoreFontFamily,
            pixelsPerDip,
            CultureInfo.CurrentUICulture);
        return wouldTrim ? cellValue.Text : null;
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

    private bool IsDescendantOfScrollChrome(DependencyObject source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, verticalScrollBar)
                || ReferenceEquals(source, horizontalScrollBar)
                || ReferenceEquals(source, scrollBarCorner))
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    internal void NotifySurfaceRendered(int visibleRowCount, int visibleColumnCount, long renderWorkMs, double textCacheHitRate, string redrawReason)
    {
        int visibleCellCount = TableFirstVisibleMetrics.CalculateVisibleCellCount(visibleRowCount, visibleColumnCount);
        TryLogRenderMetrics(redrawReason, visibleRowCount, visibleColumnCount, visibleCellCount, renderWorkMs, textCacheHitRate);
        if (firstRenderLogged || !IsVisible || itemsAppliedTimestamp <= 0L || suppressColumnRedrawUntilItemsSourceChanged)
        {
            return;
        }
        long firstRenderMs = (Stopwatch.GetTimestamp() - itemsAppliedTimestamp) * 1000L / Stopwatch.Frequency;
        bool isPreparationRender = RowCount == 0 && visibleCellCount == 0;
        if (isPreparationRender)
        {
            if (preparationRenderLogged)
            {
                return;
            }
            preparationRenderLogged = true;
        }
        else
        {
            firstRenderLogged = true;
        }
        FirstRenderCompleted?.Invoke(this, new CustomTableFirstRenderCompletedEventArgs(RowCount, visibleRowCount, visibleColumnCount, visibleCellCount, firstRenderMs, renderWorkMs, textCacheHitRate, isPreparationRender));
    }

    private void TryLogRenderMetrics(string redrawReason, int visibleRowCount, int visibleColumnCount, int visibleCellCount, long renderWorkMs, double textCacheHitRate)
    {
        string reason = NormalizeRedrawReason(redrawReason);
        bool firstForReason = loggedRenderReasons.Add(reason);
        if (renderWorkMs < RenderSlowLogThresholdMs && !firstForReason && !IsAlwaysLoggedRenderReason(reason))
        {
            return;
        }
        installPerformanceLogger?.Info(
            "custom_table_render reason=" + reason
            + " rowCount=" + RowCount
            + " visibleRowCount=" + visibleRowCount
            + " visibleColumnCount=" + visibleColumnCount
            + " visibleCellCount=" + visibleCellCount
            + " renderWorkMs=" + renderWorkMs
            + " textCacheHitRate=" + textCacheHitRate
            + " cellValueCacheCount=" + cellValueCache.Count);
    }

    private static bool IsAlwaysLoggedRenderReason(string reason)
    {
        return string.Equals(reason, "items_source_changed", StringComparison.Ordinal)
            || string.Equals(reason, "pending_items_source_swap_skipped", StringComparison.Ordinal);
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

    internal static Rect CreateContentColumnRect(double columnX, double columnWidth, double horizontalOffset, double y, double height)
    {
        return new Rect(columnX - horizontalOffset, y, Math.Max(0d, columnWidth), height);
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

    internal void ClearTextLayoutCache()
    {
        textLayoutCache.Clear();
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
            owner.NotifySurfaceRendered(0, 0, 0L, -1d, owner.ConsumeRedrawReason());
            return;
        }
        string redrawReason = owner.ConsumeRedrawReason();
        CustomTableColumnLayoutSnapshot layout = owner.GetColumnLayoutSnapshot();
        DrawBackground(drawingContext, width, height);
        DrawHeader(drawingContext, layout, width);
        if (owner.IsItemsSourceSwapPending)
        {
            renderStopwatch.Stop();
            owner.NotifySurfaceRendered(
                visibleRowCount: 0,
                visibleColumnCount: layout.VisibleColumnCount,
                renderWorkMs: renderStopwatch.ElapsedMilliseconds,
                textCacheHitRate: -1d,
                redrawReason: "pending_items_source_swap_skipped");
            return;
        }
        int visibleRowCount = DrawRows(drawingContext, layout, width, height);
        renderStopwatch.Stop();
        owner.NotifySurfaceRendered(
            visibleRowCount,
            layout.VisibleColumnCount,
            renderStopwatch.ElapsedMilliseconds,
            CustomTableTextLayoutCache.CalculateHitRate(renderTextCacheHits, renderTextCacheMisses),
            redrawReason);
    }

    private void DrawBackground(DrawingContext drawingContext, double width, double height)
    {
        drawingContext.DrawRectangle(CustomTablePalette.Current.RowBackground, null, new Rect(0d, 0d, width, height));
    }

    private void DrawHeader(DrawingContext drawingContext, CustomTableColumnLayoutSnapshot layout, double width)
    {
        double headerHeight = owner.HeaderHeight;
        double horizontalOffset = owner.HorizontalOffset;
        CustomTablePalette palette = CustomTablePalette.Current;
        drawingContext.DrawRectangle(palette.HeaderBackground, null, new Rect(0d, 0d, width, headerHeight));
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column != null)
            {
                Rect cellRect = entry.CreateContentRect(horizontalOffset, 0d, headerHeight);
                if (owner.IsReorderSourceColumn(column))
                {
                    drawingContext.DrawRectangle(
                        palette.ReorderSourceHeader,
                        null,
                        entry.CreateVisibleRect(horizontalOffset, width, 0d, headerHeight));
                }
                DrawCellText(drawingContext, column.Header, cellRect, CustomTableScoreBrushProvider.DefaultForeground, TextAlignment.Center, CustomTableTextStyle.Normal);
                DrawSortGlyph(drawingContext, column, cellRect);
                double borderX = entry.TableX + entry.Width - horizontalOffset - 0.5d;
                if (borderX >= 0d && borderX <= width)
                {
                    drawingContext.DrawLine(palette.HeaderBorder, new Point(borderX, 0d), new Point(borderX, headerHeight));
                }
            }
        }
        drawingContext.DrawLine(palette.HeaderBorder, new Point(0d, headerHeight - 0.5d), new Point(width, headerHeight - 0.5d));
        if (owner.IsColumnReorderPreviewActive)
        {
            double insertX = Math.Max(0d, Math.Min(width, owner.ColumnReorderPreviewInsertX));
            drawingContext.DrawLine(palette.ColumnReorderInsert, new Point(insertX, 0d), new Point(insertX, headerHeight));
        }
    }

    private int DrawRows(DrawingContext drawingContext, CustomTableColumnLayoutSnapshot layout, double width, double height)
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
        int drawableRowCapacity = owner.CalculateDrawableRowCapacity();
        int lastRowExclusive = Math.Min(rowCount, firstRowIndex + drawableRowCapacity);
        int drawnRows = 0;
        for (int rowIndex = firstRowIndex; rowIndex < lastRowExclusive && y < height; rowIndex++)
        {
            object row = rows[rowIndex];
            bool selected = owner.IsRowSelected(rowIndex);
            CustomTablePalette palette = CustomTablePalette.Current;
            Brush rowBackground = selected
                ? palette.SelectedRowBackground
                : owner.HasHighlightedWarning(row)
                    ? palette.WarningRowBackground
                    : (rowIndex & 1) == 1
                        ? palette.AlternatingRowBackground
                        : palette.RowBackground;
            Rect rowRect = new Rect(0d, y, width, Math.Min(rowHeight, height - y));
            drawingContext.DrawRectangle(rowBackground, null, rowRect);
            DrawRowCells(drawingContext, layout, rowIndex, row, width, y, rowHeight);
            drawingContext.DrawLine(palette.CellBorder, new Point(0d, y + rowHeight - 0.5d), new Point(width, y + rowHeight - 0.5d));
            y += rowHeight;
            drawnRows++;
        }
        return drawnRows;
    }

    private void DrawRowCells(DrawingContext drawingContext, CustomTableColumnLayoutSnapshot layout, int rowIndex, object row, double width, double y, double rowHeight)
    {
        double horizontalOffset = owner.HorizontalOffset;
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column == null)
            {
                continue;
            }
            Rect cellRect = entry.CreateContentRect(horizontalOffset, y, rowHeight);
            bool currentCell = owner.IsCurrentCell(rowIndex, column);
            CustomTableCellValue cellValue = owner.GetCellValue(row, column, out _);
            if (!owner.IsRowSelected(rowIndex) && cellValue.Background != null)
            {
                drawingContext.DrawRectangle(cellValue.Background, null, entry.CreateVisibleRect(horizontalOffset, width, y, rowHeight));
            }
            if (currentCell)
            {
                drawingContext.DrawRectangle(CustomTablePalette.Current.CurrentCellBackground, null, entry.CreateVisibleRect(horizontalOffset, width, y, rowHeight));
            }
            Brush foreground = currentCell
                ? CustomTablePalette.Current.CurrentCellForeground
                : owner.IsRowSelected(rowIndex)
                    ? CustomTableScoreBrushProvider.SelectedForeground
                    : cellValue.Foreground;
            if (cellValue.CellKind == CustomTableCellKind.DownloadIcon)
            {
                DrawDownloadIcon(drawingContext, cellValue.Text, cellRect, foreground);
            }
            else if (cellValue.CellKind == CustomTableCellKind.StatusIcon)
            {
                DrawStatusIcon(drawingContext, cellValue.Text, cellRect, foreground);
            }
            else if (cellValue.CellKind == CustomTableCellKind.CheckBox)
            {
                DrawCheckBox(drawingContext, cellValue.IsChecked, cellRect, foreground);
            }
            else
            {
                DrawCellText(drawingContext, cellValue.Text, cellRect, foreground, cellValue.Alignment, cellValue.TextStyle);
            }
            double borderX = entry.TableX + entry.Width - horizontalOffset - 0.5d;
            if (borderX >= 0d && borderX <= width)
            {
                drawingContext.DrawLine(CustomTablePalette.Current.CellBorder, new Point(borderX, y), new Point(borderX, y + rowHeight));
            }
        }
    }

    private void DrawCellText(DrawingContext drawingContext, string text, Rect cellRect, Brush foreground, TextAlignment alignment, CustomTableTextStyle textStyle)
    {
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 2d || cellRect.Height <= 1d)
        {
            return;
        }
        CustomTableTextStyle effectiveTextStyle = textStyle ?? CustomTableTextStyle.Normal;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maxTextWidth = Math.Max(1d, cellRect.Width - 4d);
        double maxTextHeight = Math.Max(Math.Max(1d, cellRect.Height), effectiveTextStyle.FontSize * 2d);
        FormattedText formattedText = textLayoutCache.GetOrCreate(
            text,
            maxTextWidth,
            maxTextHeight,
            alignment,
            effectiveTextStyle,
            owner.ScoreFontFamily,
            foreground,
            pixelsPerDip,
            CultureInfo.CurrentUICulture,
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
        double y = cellRect.Y + Math.Max(0d, (cellRect.Height - formattedText.Height) / 2d) + effectiveTextStyle.VerticalOffset;
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

    private static void DrawStatusIcon(DrawingContext drawingContext, string text, Rect cellRect, Brush foreground)
    {
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 6d || cellRect.Height <= 6d)
        {
            return;
        }
        if (!Enum.TryParse(text, out CustomTableStatusIconKind iconKind) || iconKind == CustomTableStatusIconKind.None)
        {
            return;
        }
        Brush iconBrush = foreground ?? CustomTableScoreBrushProvider.DefaultForeground;
        double size = Math.Max(7d, Math.Min(12d, Math.Min(cellRect.Width - 4d, cellRect.Height - 4d)));
        double left = cellRect.Left + (cellRect.Width - size) / 2d;
        double top = cellRect.Top + (cellRect.Height - size) / 2d;
        Rect iconRect = new Rect(left, top, size, size);
        Pen pen = new Pen(iconBrush, 1.5d)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        switch (iconKind)
        {
            case CustomTableStatusIconKind.Forward:
                DrawTrianglePair(drawingContext, iconBrush, iconRect, forward: true);
                break;
            case CustomTableStatusIconKind.Backward:
                DrawTrianglePair(drawingContext, iconBrush, iconRect, forward: false);
                break;
            case CustomTableStatusIconKind.Play:
                DrawTriangle(drawingContext, iconBrush, new Point(iconRect.Left + 2d, iconRect.Top + 1d), new Point(iconRect.Left + 2d, iconRect.Bottom - 1d), new Point(iconRect.Right - 1d, iconRect.Top + iconRect.Height / 2d));
                break;
            case CustomTableStatusIconKind.Loading:
                drawingContext.DrawEllipse(iconBrush, null, new Point(iconRect.Left + 2d, iconRect.Top + iconRect.Height / 2d), 1.2d, 1.2d);
                drawingContext.DrawEllipse(iconBrush, null, new Point(iconRect.Left + iconRect.Width / 2d, iconRect.Top + iconRect.Height / 2d), 1.2d, 1.2d);
                drawingContext.DrawEllipse(iconBrush, null, new Point(iconRect.Right - 2d, iconRect.Top + iconRect.Height / 2d), 1.2d, 1.2d);
                break;
            case CustomTableStatusIconKind.Pause:
                drawingContext.DrawRectangle(iconBrush, null, new Rect(iconRect.Left + 2d, iconRect.Top + 1d, 2d, iconRect.Height - 2d));
                drawingContext.DrawRectangle(iconBrush, null, new Rect(iconRect.Right - 4d, iconRect.Top + 1d, 2d, iconRect.Height - 2d));
                break;
            case CustomTableStatusIconKind.Searching:
                drawingContext.DrawEllipse(null, pen, new Point(iconRect.Left + iconRect.Width * 0.43d, iconRect.Top + iconRect.Height * 0.43d), iconRect.Width * 0.28d, iconRect.Height * 0.28d);
                drawingContext.DrawLine(pen, new Point(iconRect.Left + iconRect.Width * 0.63d, iconRect.Top + iconRect.Height * 0.63d), new Point(iconRect.Right - 1d, iconRect.Bottom - 1d));
                break;
            case CustomTableStatusIconKind.ScoreUnsent:
                DrawRefreshIcon(drawingContext, iconBrush, pen, iconRect);
                break;
        }
    }

    private static void DrawCheckBox(DrawingContext drawingContext, bool? isChecked, Rect cellRect, Brush foreground)
    {
        if (!isChecked.HasValue || cellRect.Width <= 8d || cellRect.Height <= 8d)
        {
            return;
        }
        double size = Math.Max(8d, Math.Min(12d, Math.Min(cellRect.Width - 4d, cellRect.Height - 4d)));
        double left = cellRect.Left + (cellRect.Width - size) / 2d;
        double top = cellRect.Top + (cellRect.Height - size) / 2d;
        Rect rect = new Rect(left, top, size, size);
        CustomTablePalette palette = CustomTablePalette.Current;
        drawingContext.DrawRectangle(palette.CheckBoxBackground, palette.CheckBoxBorder, rect);
        if (isChecked.Value)
        {
            Pen checkPen = foreground == null || ReferenceEquals(foreground, CustomTableScoreBrushProvider.DefaultForeground)
                ? palette.CheckBoxCheck
                : new Pen(foreground, 1.8d);
            drawingContext.DrawLine(checkPen, new Point(rect.Left + 2.5d, rect.Top + size * 0.55d), new Point(rect.Left + size * 0.43d, rect.Bottom - 2.5d));
            drawingContext.DrawLine(checkPen, new Point(rect.Left + size * 0.43d, rect.Bottom - 2.5d), new Point(rect.Right - 2d, rect.Top + 2.5d));
        }
    }

    private static void DrawRefreshIcon(DrawingContext drawingContext, Brush brush, Pen pen, Rect rect)
    {
        Point start = new Point(rect.Right - 2d, rect.Top + rect.Height * 0.45d);
        Point end = new Point(rect.Left + 2d, rect.Top + rect.Height * 0.58d);
        StreamGeometry arc = new StreamGeometry();
        using (StreamGeometryContext context = arc.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(rect.Width * 0.42d, rect.Height * 0.42d), 0d, isLargeArc: true, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        drawingContext.DrawGeometry(null, pen, arc);
        DrawTriangle(drawingContext, brush, new Point(end.X, end.Y), new Point(end.X + 4d, end.Y - 1d), new Point(end.X + 2d, end.Y + 3d));
    }

    private static void DrawTrianglePair(DrawingContext drawingContext, Brush brush, Rect rect, bool forward)
    {
        double mid = rect.Left + rect.Width / 2d;
        if (forward)
        {
            DrawTriangle(drawingContext, brush, new Point(rect.Left, rect.Top + 1d), new Point(rect.Left, rect.Bottom - 1d), new Point(mid, rect.Top + rect.Height / 2d));
            DrawTriangle(drawingContext, brush, new Point(mid - 1d, rect.Top + 1d), new Point(mid - 1d, rect.Bottom - 1d), new Point(rect.Right, rect.Top + rect.Height / 2d));
        }
        else
        {
            DrawTriangle(drawingContext, brush, new Point(rect.Right, rect.Top + 1d), new Point(rect.Right, rect.Bottom - 1d), new Point(mid, rect.Top + rect.Height / 2d));
            DrawTriangle(drawingContext, brush, new Point(mid + 1d, rect.Top + 1d), new Point(mid + 1d, rect.Bottom - 1d), new Point(rect.Left, rect.Top + rect.Height / 2d));
        }
    }

    private static void DrawTriangle(DrawingContext drawingContext, Brush brush, Point p1, Point p2, Point p3)
    {
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(p1, isFilled: true, isClosed: true);
            context.LineTo(p2, isStroked: true, isSmoothJoin: false);
            context.LineTo(p3, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(brush, null, geometry);
    }

    private void DrawSortGlyph(DrawingContext drawingContext, CustomTableColumn column, Rect cellRect)
    {
        if (column == null || string.IsNullOrWhiteSpace(column.SortMemberPath) || !string.Equals(column.SortMemberPath, owner.SortColumnName, StringComparison.Ordinal) || !owner.SortDirection.HasValue || cellRect.Width < 8d || cellRect.Height < 8d)
        {
            return;
        }
        double centerX = cellRect.Left + cellRect.Width / 2d;
        double top = cellRect.Top + 1d;
        double bottom = top + 3.5d;
        bool ascending = owner.SortDirection == ListSortDirection.Ascending;
        Point p1 = ascending ? new Point(centerX, top) : new Point(centerX, bottom);
        Point p2 = ascending ? new Point(centerX - 3d, bottom) : new Point(centerX - 3d, top);
        Point p3 = ascending ? new Point(centerX + 3d, bottom) : new Point(centerX + 3d, top);
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(p1, isFilled: true, isClosed: true);
            context.LineTo(p2, isStroked: true, isSmoothJoin: false);
            context.LineTo(p3, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(CustomTablePalette.Current.SortGlyph, null, geometry);
    }
}
