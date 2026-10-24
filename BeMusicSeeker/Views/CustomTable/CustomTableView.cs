using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

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
    internal const double CellTextHorizontalPadding = 2d;
    private const double ColumnResizeHitTestMargin = 4d;
    // 共通ScrollBarの固定ヒット領域と、末尾を露出するための計算を揃える。
    private const double ScrollBarThickness = 16d;
    private const double CellEditorBorderThickness = 1d;
    private const int RowSubscriptionOverscan = 5;
    private const long RowSubscriptionSlowLogThresholdMs = 100L;
    private const long RenderSlowLogThresholdMs = 100L;
    private static readonly Thickness CellEditorBorderThicknessValue = new(CellEditorBorderThickness);
    private static readonly Thickness CellEditorPadding = new(Math.Max(0d, CellTextHorizontalPadding - CellEditorBorderThickness), 0d, Math.Max(0d, CellTextHorizontalPadding - CellEditorBorderThickness), 0d);
    private static readonly Style CellEditorTextBoxStyle = CreateCellEditorTextBoxStyle();

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
        new FrameworkPropertyMetadata(BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutMetricChanged, CoerceRowHeight));

    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight),
        typeof(double),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(BeMusicSeeker.Properties.Settings.DefaultCustomTableHeaderHeight, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutMetricChanged, CoerceHeaderHeight));

    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize),
        typeof(double),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize, FrameworkPropertyMetadataOptions.AffectsRender, OnTextMetricChanged, CoerceTextFontSize));

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

    public static readonly DependencyProperty RowDragKindProperty = DependencyProperty.Register(
        nameof(RowDragKind),
        typeof(CustomTableRowDragKind),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(CustomTableRowDragKind.GenericSelectedRows));

    public static readonly DependencyProperty ScoreFontFamilyProperty = DependencyProperty.Register(
        nameof(ScoreFontFamily),
        typeof(FontFamily),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnScoreFontFamilyChanged));

    private readonly CustomTableSurface surface;
    private readonly Canvas editorLayer;
    private readonly ScrollBar verticalScrollBar;
    private readonly ScrollBar horizontalScrollBar;
    private readonly ToolTip cellToolTip;
    private readonly Popup editSuggestionPopup;
    private readonly ListBox editSuggestionListBox;
    private readonly CustomTableSelectionModel selectionModel = new();
    private readonly CustomTableRowCache rowCache = new();
    private readonly CustomTableRowChangeTracker rowChangeTracker;
    private readonly CustomTableRowInvalidationQueue pendingRowInvalidations = new();
    private readonly CustomTableRedrawScheduler rowPropertyChangedRedrawScheduler;
    private readonly List<INotifyPropertyChanged> subscribedColumnLayouts = [];
    private readonly HashSet<string> loggedRenderReasons = [];
    private CustomTableColumnLayoutSnapshot columnLayoutSnapshot;
    private INotifyCollectionChanged itemsCollectionChanged;
    private string pendingRedrawReason = "initial";
    private long itemsAppliedTimestamp;
    private bool firstRenderLogged = true;
    private bool updatingSelectedIndexFromSelection;
    private (object[] Selected, object Current, object Anchor)? selectionIdentities;
    private bool isResizingColumn;
    private ICustomTableColumnLayout resizingColumnLayout;
    private int resizingColumnMinWidth;
    private int resizingColumnMaxWidth;
    private double resizingStartMouseX;
    private int resizingStartWidth;
    private int toolTipRowIndex = -1;
    private string toolTipColumnId;
    private string toolTipText;
    private double? toolTipTextWidth;
    private CustomTableHitTestResult currentCellHit;
    private Point? rowDragStartPoint;
    private CustomTableHitTestResult rowDragStartHit;
    private int pendingSingleSelectionOnMouseUpRowIndex = -1;
    private DragAdorner rowDragAdorner;
    private int rowDropPreviewInsertIndex = -1;
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
    private MainChartListViewModel subscribedMainChartList;

    /// <summary>全面の描画・編集面へ固定幅のスクロールバーを重ね、既存の表操作と購読を接続します。</summary>
    public CustomTableView()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(Panel.BackgroundProperty, "ScrollBar.TrackBackgroundBrush");
        surface = new CustomTableSurface(this);
        verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = ScrollBarThickness,
            HorizontalAlignment = HorizontalAlignment.Right,
            // Windowなしの配置でもOS既定の最小17 DIPへ広げない。
            MinWidth = 0d,
            MinHeight = 0d,
            Minimum = 0d,
            SmallChange = 3d
        };
        horizontalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Height = ScrollBarThickness,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Windowなしの配置でもOS既定の最小17 DIPへ広げない。
            MinWidth = 0d,
            MinHeight = 0d,
            Minimum = 0d,
            SmallChange = 16d
        };
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
        rowChangeTracker = new CustomTableRowChangeTracker(delegate (INotifyPropertyChanged row, PropertyChangedEventArgs e)
        {
            EnqueueRowInvalidation(row);
        }, rowCache.InvalidateRow);
        verticalScrollBar.ValueChanged += VerticalScrollBarValueChanged;
        horizontalScrollBar.ValueChanged += HorizontalScrollBarValueChanged;
        Children.Add(surface);
        Children.Add(editorLayer);
        Children.Add(verticalScrollBar);
        Children.Add(horizontalScrollBar);
        // Surfaceと編集面は全面を共有し、バーだけを前面へ重ねる。
        SetZIndex(editorLayer, 1);
        SetZIndex(verticalScrollBar, 2);
        SetZIndex(horizontalScrollBar, 2);
        surface.SizeChanged += delegate (object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                InvalidateColumnLayoutSnapshot();
            }
            UpdateScrollBars();
            UpdateVisibleRowSubscriptions("surface_size_changed");
            RequestRedraw("surface_size_changed");
        };
        SizeChanged += delegate (object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                InvalidateColumnLayoutSnapshot();
            }
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
        DataContextChanged += CustomTableViewDataContextChanged;
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
            ApplyVisibilityChanged(IsVisible);
        };
    }

    private void CustomTableViewLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeService.ThemeChanged += AppThemeServiceThemeChanged;
        SubscribeMainChartList(DataContext as MainChartListViewModel);
        RefreshTheme("loaded");
    }

    private void CustomTableViewUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeService.ThemeChanged -= AppThemeServiceThemeChanged;
        SubscribeMainChartList(null);
        ApplyVisibilityChanged(false);
    }

    private void CustomTableViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SubscribeMainChartList(e.NewValue as MainChartListViewModel);
        }
    }

    private void SubscribeMainChartList(MainChartListViewModel mainChartList)
    {
        if (ReferenceEquals(subscribedMainChartList, mainChartList))
        {
            return;
        }
        if (subscribedMainChartList != null)
        {
            subscribedMainChartList.RowsReplacing -= MainChartListRowsReplacing;
            subscribedMainChartList.RowsSelectionPreserving -= MainChartListRowsSelectionPreserving;
            subscribedMainChartList.RowsReplacementCanceled -= MainChartListRowsReplacementCanceled;
            subscribedMainChartList.RowsReplacementPublishFailed -= MainChartListRowsReplacementCanceled;
            subscribedMainChartList.DisplayRefreshRequested -= MainChartListDisplayRefreshRequested;
        }
        subscribedMainChartList = mainChartList;
        if (subscribedMainChartList != null)
        {
            subscribedMainChartList.RowsReplacing += MainChartListRowsReplacing;
            subscribedMainChartList.RowsSelectionPreserving += MainChartListRowsSelectionPreserving;
            subscribedMainChartList.RowsReplacementCanceled += MainChartListRowsReplacementCanceled;
            subscribedMainChartList.RowsReplacementPublishFailed += MainChartListRowsReplacementCanceled;
            subscribedMainChartList.DisplayRefreshRequested += MainChartListDisplayRefreshRequested;
        }
    }

    private void MainChartListRowsReplacing(object sender, EventArgs e)
    {
        InvokePresentationAction(PrepareForItemsSourceSwap);
    }

    /// <summary>譜面一覧のBinding公開後に、同じ元集合なら選択識別を復元し、別の元集合なら旧添字による選択を解除します。</summary>
    private void MainChartListRowsSelectionPreserving(object sender, MainChartListSelectionRestoration e)
    {
        InvokePresentationAction(() =>
        {
            if (ItemsSource is not ICustomTableSelectionIdentitySource before
                || e.Rows is not ICustomTableSelectionIdentitySource after) { return; }
            if (!before.SharesSelectionIdentityScope(after))
            {
                e.Register(() =>
                {
                    if (ReferenceEquals(ItemsSource, after)) { ClearSelection(); }
                });
                return;
            }
            (object[] Selected, object Current, object Anchor) captured = CaptureSelectionIdentities(before);
            e.Register(() =>
            {
                if (ReferenceEquals(ItemsSource, after)) { RestoreSelectionIdentities(after, captured); }
            });
        });
    }

    private void MainChartListRowsReplacementCanceled(object sender, EventArgs e)
    {
        InvokePresentationAction(CancelPendingItemsSourceSwapPreparation);
    }

    private void MainChartListDisplayRefreshRequested(object sender, EventArgs e)
    {
        InvokePresentationAction(RefreshDisplay);
    }

    private void InvokePresentationAction(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try
            {
                Dispatcher.Invoke(DispatcherPriority.Normal, action);
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
            }
        }
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
        rowCache.Clear();
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
        if (activeEditor != null)
        {
            ApplyCellEditorTheme(activeEditor);
        }
    }

    private static Style CreateCellEditorTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateCellEditorTemplate()));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, CellEditorBorderThicknessValue));
        style.Setters.Add(new Setter(Control.PaddingProperty, CellEditorPadding));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden));
        return style;
    }

    private static ControlTemplate CreateCellEditorTemplate()
    {
        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        contentHost.SetValue(FrameworkElement.MarginProperty, new Thickness(0d));
        contentHost.SetValue(FrameworkElement.FocusVisualStyleProperty, null);
        contentHost.SetValue(UIElement.FocusableProperty, false);
        contentHost.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        contentHost.SetValue(Control.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(0d));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        border.AppendChild(contentHost);

        return new ControlTemplate(typeof(TextBox))
        {
            VisualTree = border
        };
    }

    public event EventHandler<CustomTableFirstRenderCompletedEventArgs> FirstRenderCompleted;

    public event EventHandler<CustomTableSortRequestedEventArgs> SortRequested;

    public event EventHandler<CustomTableSelectionChangedEventArgs> SelectionChanged;

    public event EventHandler<CustomTableRowRequestedEventArgs> RowActivated;

    public event EventHandler<CustomTableRowRequestedEventArgs> RowContextMenuRequested;

    public event EventHandler<CustomTableHeaderRequestedEventArgs> HeaderContextMenuRequested;

    public event EventHandler<CustomTableCellEditBeginningEventArgs> CellEditBeginning;

    public event EventHandler<CustomTableCellEditStartedEventArgs> CellEditStarted;

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

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
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

    public CustomTableRowDragKind RowDragKind
    {
        get => (CustomTableRowDragKind)GetValue(RowDragKindProperty);
        set => SetValue(RowDragKindProperty, value);
    }

    public FontFamily ScoreFontFamily
    {
        get => (FontFamily)GetValue(ScoreFontFamilyProperty);
        set => SetValue(ScoreFontFamilyProperty, value);
    }

    internal int FirstVisibleRowIndex => (int)Math.Max(0d, Math.Floor(verticalScrollBar.Value));

    internal double HorizontalOffset => Math.Max(0d, horizontalScrollBar.Value);

    internal IReadOnlyList<CustomTableColumn> VisibleColumns => Columns as IReadOnlyList<CustomTableColumn> ?? Columns?.ToArray() ?? [];

    internal int RowCount => ItemsSource?.Count ?? 0;

    /// <summary>バー背後を含む全面の実描画幅です。スクロール用の有効表示幅とは区別します。</summary>
    internal double SurfaceWidth => surface.ActualWidth;

    /// <summary>バー背後を含む全面の実描画高です。完全表示行容量とは区別します。</summary>
    internal double SurfaceHeight => surface.ActualHeight;

    internal bool IsRowSelected(int rowIndex)
    {
        return selectionModel.IsSelected(rowIndex);
    }

    /// <summary>指定位置の行に属する現在セルの列を返します。</summary>
    internal CustomTableColumn GetCurrentCellColumn(int rowIndex)
    {
        return currentCellHit?.Kind == CustomTableHitKind.Cell && currentCellHit.RowIndex == rowIndex
            ? currentCellHit.Column
            : null;
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

    internal bool IsRowDropInsertPreviewActive => rowDropPreviewInsertIndex >= 0;

    internal int RowDropPreviewInsertIndex => rowDropPreviewInsertIndex;

    internal bool IsItemsSourceSwapPending => suppressColumnRedrawUntilItemsSourceChanged;

    internal CustomTableTextStyle ResolveTextStyle(CustomTableTextStyle textStyle)
    {
        return (textStyle ?? CustomTableTextStyle.Normal).WithFontSize(TextFontSize);
    }

    internal bool IsReorderSourceColumn(CustomTableColumn column)
    {
        return isReorderingColumn
            && column != null
            && pendingHeaderHit?.Column != null
            && ReferenceEquals(pendingHeaderHit.Column, column);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.ClearPendingItemsSourceSwapColumnRedrawSuppression();
        view.DetachCollectionChanged(e.OldValue as INotifyCollectionChanged);
        view.AttachCollectionChanged(e.NewValue as INotifyCollectionChanged);
        view.MarkItemsApplied();
        view.InvalidateAllCellValues();
        view.CoerceSelectionToCurrentRows();
        view.UpdateScrollBars();
        view.UpdateVisibleRowSubscriptions(
            "items_source_changed",
            logAlways: true);
        view.RequestRedraw("items_source_changed");
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
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
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.DetachColumnLayoutHandlers();
        view.AttachColumnLayoutHandlers();
        view.RebuildColumns();
    }

    private static void OnPlaylistSummaryColumnsSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.DetachColumnLayoutHandlers();
        view.AttachColumnLayoutHandlers();
        view.RebuildColumns();
    }

    private static void OnLayoutMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.UpdateScrollBars();
        view.UpdateVisibleRowSubscriptions("layout_metric_changed");
        view.RequestRedraw("layout_metric_changed");
    }

    private static void OnTextMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.surface.ClearTextLayoutCache();
        view.RequestRedraw("text_metric_changed");
    }

    private static object CoerceRowHeight(DependencyObject d, object baseValue)
    {
        return baseValue is double value
            ? BeMusicSeeker.Properties.Settings.NormalizeRange(value, BeMusicSeeker.Properties.Settings.MinCustomTableRowHeight, BeMusicSeeker.Properties.Settings.MaxCustomTableRowHeight, BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight)
            : BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight;
    }

    private static object CoerceHeaderHeight(DependencyObject d, object baseValue)
    {
        return baseValue is double value
            ? BeMusicSeeker.Properties.Settings.NormalizeRange(value, BeMusicSeeker.Properties.Settings.MinCustomTableHeaderHeight, BeMusicSeeker.Properties.Settings.MaxCustomTableHeaderHeight, BeMusicSeeker.Properties.Settings.DefaultCustomTableHeaderHeight)
            : BeMusicSeeker.Properties.Settings.DefaultCustomTableHeaderHeight;
    }

    private static object CoerceTextFontSize(DependencyObject d, object baseValue)
    {
        return baseValue is double value
            ? BeMusicSeeker.Properties.Settings.NormalizeRange(value, BeMusicSeeker.Properties.Settings.MinCustomTableFontSize, BeMusicSeeker.Properties.Settings.MaxCustomTableFontSize, BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize)
            : BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize;
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        if (!view.updatingSelectedIndexFromSelection && !view.selectionIdentities.HasValue)
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
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.RequestRedraw("sort_state");
    }

    private static void OnScoreFontFamilyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
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
        if (collection is ICustomTableSelectionIdentitySource source)
        {
            source.SelectionUpdateStarting += CaptureSelectionIdentities;
            source.SelectionUpdateCompleted += RestoreSelectionIdentities;
        }
    }

    private void DetachCollectionChanged(INotifyCollectionChanged collection)
    {
        if (collection != null)
        {
            collection.CollectionChanged -= ItemsSourceCollectionChanged;
        }
        if (collection is ICustomTableSelectionIdentitySource source)
        {
            source.SelectionUpdateStarting -= CaptureSelectionIdentities;
            source.SelectionUpdateCompleted -= RestoreSelectionIdentities;
            selectionIdentities = null;
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
        if (!selectionIdentities.HasValue) { CoerceSelectionToCurrentRows(); }
        UpdateScrollBars();
        UpdateVisibleRowSubscriptions(
            GetCollectionChangedSubscriptionReason(e),
            logAlways: e?.Action == NotifyCollectionChangedAction.Reset);
        RequestRedraw("items_source_changed");
    }

    /// <summary>選択・主行・Shift基点だけを、更新前の元集合識別で短命に捕捉します。</summary>
    private void CaptureSelectionIdentities(object sender, EventArgs e)
    {
        if (sender is not ICustomTableSelectionIdentitySource source || !ReferenceEquals(sender, ItemsSource)) { return; }
        selectionIdentities = CaptureSelectionIdentities(source);
    }

    /// <summary>実体化なしの索引を使って、選択数に比例する識別だけを取得します。</summary>
    private (object[] Selected, object Current, object Anchor) CaptureSelectionIdentities(ICustomTableSelectionIdentitySource source)
        => (selectionModel.SelectedIndices.Select(source.GetSelectionIdentity).ToArray(),
            source.GetSelectionIdentity(selectionModel.CurrentIndex), source.GetSelectionIdentity(selectionModel.AnchorIndex));

    /// <summary>最終順序へ選択を戻します。主行の添字が同じでも副選択と基点を必ず照合します。</summary>
    private void RestoreSelectionIdentities(object sender, EventArgs e)
    {
        if (!selectionIdentities.HasValue || sender is not ICustomTableSelectionIdentitySource source
            || !ReferenceEquals(sender, ItemsSource)) { return; }
        (object[] Selected, object Current, object Anchor) captured = selectionIdentities.Value;
        selectionIdentities = null;
        RestoreSelectionIdentities(source, captured);
    }

    /// <summary>残る選択・主行・基点だけを採用し、同じ主添字でも副選択の旧位置を使いません。</summary>
    private void RestoreSelectionIdentities(ICustomTableSelectionIdentitySource source, (object[] Selected, object Current, object Anchor) captured)
    {
        selectionModel.SetItemCount(RowCount);
        bool changed = selectionModel.RestoreIndices(captured.Selected.Select(source.IndexOfSelectionIdentity),
            source.IndexOfSelectionIdentity(captured.Current), source.IndexOfSelectionIdentity(captured.Anchor));
        // 行数減少によるcoerceが先に空にした場合も、選択消失を通知する。
        changed |= captured.Selected.Length != selectionModel.SelectedIndices.Count;
        UpdateSelectedIndexFromSelectionModel();
        if (changed) { RaiseSelectionChanged(); }
        RequestRedraw("selection");
    }

    internal void ApplyVisibilityChanged(bool isVisible)
    {
        if (isVisible)
        {
            MarkItemsApplied();
            UpdateScrollBars();
            UpdateVisibleRowSubscriptions("visible_changed", logAlways: true, isVisibleOverride: true);
            RequestRedraw("visible_changed");
            return;
        }

        CommitActiveEdit();
        CloseCellToolTip();
        rowChangeTracker.DetachAllRows();
        EndColumnResize();
        ClearDragState();
        ClearHeaderDragState();
        pendingRowInvalidations.Clear();
        rowCache.Clear();
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
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        Columns = PlaylistSummaryColumnsSettings != null
            ? CustomTableColumnFactory.CreatePlaylistSummaryColumns(PlaylistSummaryColumnsSettings).ToArray()
            : [.. CustomTableColumnFactory.CreateMainColumns(ColumnsSettings)];
        InvalidateColumnLayoutSnapshot();
        InvalidateColumnCellValues();
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
        double extentWidth = GetColumnLayoutSnapshot().ExtentWidth;
        double width = Math.Max(0d, surface.ActualWidth);
        (bool hasVertical, bool hasHorizontal, int fullVisibleRows, double viewportWidth) = CalculateScrollBarLayout(width, CalculateBodyHeight(), extentWidth, rowCount, RowHeight);
        verticalScrollBar.ViewportSize = fullVisibleRows;
        verticalScrollBar.LargeChange = Math.Max(1, fullVisibleRows);
        verticalScrollBar.Maximum = CalculateVerticalScrollMaximum(rowCount, fullVisibleRows);
        verticalScrollBar.Visibility = hasVertical ? Visibility.Visible : Visibility.Collapsed;
        if (verticalScrollBar.Value > verticalScrollBar.Maximum)
        {
            verticalScrollBar.Value = verticalScrollBar.Maximum;
        }
        horizontalScrollBar.ViewportSize = viewportWidth;
        horizontalScrollBar.LargeChange = Math.Max(1d, viewportWidth);
        horizontalScrollBar.Maximum = Math.Max(0d, extentWidth - viewportWidth);
        horizontalScrollBar.Visibility = hasHorizontal ? Visibility.Visible : Visibility.Collapsed;
        horizontalScrollBar.Margin = new Thickness(0d, 0d, hasVertical ? ScrollBarThickness : 0d, 0d);
        if (horizontalScrollBar.Value > horizontalScrollBar.Maximum)
        {
            horizontalScrollBar.Value = horizontalScrollBar.Maximum;
        }
    }

    /// <summary>全面の実寸法から必要な軸だけを追加し、前回状態に依存しない有効表示幅と完全表示行容量を求めます。</summary>
    /// <remarks>描画寸法は減らさず、戻り値をスクロール範囲とセル露出の計算だけへ適用します。</remarks>
    internal static (bool HasVertical, bool HasHorizontal, int FullVisibleRows, double ViewportWidth) CalculateScrollBarLayout(
        double width, double bodyHeight, double extentWidth, int rowCount, double rowHeight)
    {
        bool hasVertical = false;
        bool hasHorizontal = false;
        // バーなしから必要な軸だけを追加する。前回のVisibilityへ依存させない。
        while (true)
        {
            int capacity = CalculateFullVisibleRowCapacity(bodyHeight - (hasHorizontal ? ScrollBarThickness : 0d), rowHeight);
            bool nextVertical = hasVertical || rowCount > capacity;
            bool nextHorizontal = hasHorizontal || extentWidth > Math.Max(0d, width - (nextVertical ? ScrollBarThickness : 0d)) + 0.5d;
            if (nextVertical == hasVertical && nextHorizontal == hasHorizontal)
            {
                break;
            }
            hasVertical = nextVertical;
            hasHorizontal = nextHorizontal;
        }
        int fullVisibleRows = CalculateFullVisibleRowCapacity(bodyHeight - (hasHorizontal ? ScrollBarThickness : 0d), rowHeight);
        double viewportWidth = Math.Max(0d, width - (hasVertical ? ScrollBarThickness : 0d));
        return (hasVertical, hasHorizontal, fullVisibleRows, viewportWidth);
    }

    /// <summary>横バーの手前まで完全表示できる行数を返します。極小領域でも既存の最小一行を保ちます。</summary>
    internal int CalculateFullVisibleRowCapacity()
    {
        return CalculateFullVisibleRowCapacity(CalculateBodyHeight() - (horizontalScrollBar.Visibility == Visibility.Visible ? ScrollBarThickness : 0d), RowHeight);
    }

    /// <summary>バーの背後と部分表示を含む全面の描画行数を返し、可視行購読にも使います。</summary>
    internal int CalculateDrawableRowCapacity()
    {
        return CalculateDrawableRowCapacity(CalculateBodyHeight(), RowHeight);
    }

    /// <summary>有効な本体高さを行高で切り捨て、完全表示容量を求めます。</summary>
    internal static int CalculateFullVisibleRowCapacity(double bodyHeight, double rowHeight)
    {
        return Math.Max(1, (int)Math.Floor(Math.Max(0d, bodyHeight) / Math.Max(1d, rowHeight)));
    }

    /// <summary>全面の本体高さを行高で切り上げ、部分行を含む描画容量を求めます。</summary>
    internal static int CalculateDrawableRowCapacity(double bodyHeight, double rowHeight)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0d, bodyHeight) / Math.Max(1d, rowHeight)));
    }

    /// <summary>最終行を完全表示容量の末尾へ置く、行単位の最大開始位置を返します。</summary>
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

    /// <summary>行と共通の寿命で、要求された列値だけを取得します。</summary>
    internal CustomTableCellValue GetCellValue(object row, CustomTableColumn column, out bool cacheHit)
    {
        if (row == null || column == null)
        {
            cacheHit = false;
            return CustomTableCellValue.Empty;
        }
        return rowCache.GetOrCreate(row).GetValue(column, out cacheHit);
    }

    /// <summary>値・警告・位置別描画が共有する行エントリを取得します。</summary>
    internal CustomTableRowEntry GetRowEntry(object row) => rowCache.GetOrCreate(row);

    /// <summary>警告を他の表示値と同じ寿命で取得します。</summary>
    internal bool HasHighlightedWarning(object row) => row != null && GetRowEntry(row).HasHighlightedWarning;

    /// <summary>描画環境の変更時に値を残してセル群だけを破棄します。</summary>
    internal void ClearRowDrawings() => rowCache.ClearDrawings();

    /// <summary>表示文化やパレットの変更時に、整形済みの値・ブラシと描画を更新します。</summary>
    internal void InvalidateDisplayValues() => InvalidateAllCellValues();

    private void InvalidateAllCellValues()
    {
        pendingRowInvalidations.Clear();
        rowCache.Clear();
    }

    private void InvalidateColumnCellValues() => rowCache.Clear();

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
            rowCache.InvalidateRow(row);
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

    private void UpdateVisibleRowSubscriptions(
        string reason,
        bool logAlways = false,
        bool? isVisibleOverride = null)
    {
        var stopwatch = Stopwatch.StartNew();
        int firstIndex = 0;
        int requestedCount = 0;
        if (isVisibleOverride ?? IsVisible)
        {
            int rowCount = RowCount;
            if (rowCount > 0)
            {
                int firstVisibleIndex = FirstVisibleRowIndex;
                int drawableRows = CalculateDrawableRowCapacity();
                firstIndex = Math.Max(0, firstVisibleIndex - RowSubscriptionOverscan);
                int lastExclusive = Math.Min(rowCount, firstVisibleIndex + drawableRows + RowSubscriptionOverscan);
                requestedCount = Math.Max(0, lastExclusive - firstIndex);
                rowChangeTracker.ReplaceVisibleRows(ItemsSource, firstIndex, requestedCount);
                rowCache.RetainDrawingPositions(firstIndex, requestedCount);
            }
            else
            {
                rowChangeTracker.DetachAllRows();
            }
        }
        else
        {
            rowChangeTracker.DetachAllRows();
        }
        stopwatch.Stop();
        if (Net10PerformanceLog.IsEnabled
            && (logAlways || stopwatch.ElapsedMilliseconds >= RowSubscriptionSlowLogThresholdMs))
        {
            QueuePerformanceLog(
                "custom_table_row_subscription",
                "reason=" + reason
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
            return [];
        }
        return [.. selectionModel.SelectedIndices
            .Where(index => index >= 0 && index < rows.Count)
            .Select(index => rows[index])
            .Where(row => row != null)];
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

    public void SelectRowsByPredicate(Func<object, bool> rowPredicate, Func<object, bool> currentRowPredicate = null)
    {
        IList rows = ItemsSource;
        if (rows == null || rows.Count == 0 || rowPredicate == null)
        {
            ClearSelection();
            return;
        }
        List<int> selectedIndices = [];
        int currentIndex = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            object row = rows[i];
            if (row == null || !rowPredicate(row))
            {
                continue;
            }
            selectedIndices.Add(i);
            if (currentIndex < 0 && currentRowPredicate?.Invoke(row) == true)
            {
                currentIndex = i;
            }
        }
        selectionModel.SetItemCount(RowCount);
        bool changed = selectionModel.SelectIndices(selectedIndices, currentIndex);
        UpdateSelectedIndexFromSelectionModel();
        EnsureRowVisible(selectionModel.CurrentIndex);
        EnsureCurrentCellForCurrentRow();
        if (changed)
        {
            RaiseSelectionChanged();
        }
        RequestRedraw("selection");
    }

    public void SetRowDropInsertPreview(int insertIndex)
    {
        int safeInsertIndex = Math.Max(0, Math.Min(RowCount, insertIndex));
        if (rowDropPreviewInsertIndex == safeInsertIndex)
        {
            return;
        }
        rowDropPreviewInsertIndex = safeInsertIndex;
        RequestRedraw("row_drop_insert");
    }

    public void ClearRowDropInsertPreview()
    {
        if (rowDropPreviewInsertIndex < 0)
        {
            return;
        }
        rowDropPreviewInsertIndex = -1;
        RequestRedraw("row_drop_insert");
    }

    public void RefreshDisplay()
    {
        ClearPendingItemsSourceSwapColumnRedrawSuppression();
        InvalidateAllCellValues();
        RequestRedraw("refresh_display");
    }

    public void PrepareForItemsSourceSwap()
    {
        var stopwatch = Stopwatch.StartNew();
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
        QueuePerformanceLog(
            "custom_table_prepare_items_source_swap",
            "totalMs=" + stopwatch.ElapsedMilliseconds
            + " commitActiveEditMs=" + commitActiveEditMs
            + " clearHitMs=" + clearHitMs
            + " suppressColumnRedrawMs=" + suppressMs);
    }

    internal void CancelPendingItemsSourceSwapPreparation()
    {
        ClearPendingItemsSourceSwapColumnRedrawSuppression();
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
            Rect headerRect = hasHeaderColumn ? headerEntry.CreateVisibleSlotRect(horizontalOffset, surface.ActualWidth, 0d, HeaderHeight) : new Rect(0d, 0d, surface.ActualWidth, HeaderHeight);
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
        Rect cellRect = cellEntry.CreateVisibleSlotRect(horizontalOffset, surface.ActualWidth, rowY, rowHeight);
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
        return hit.Column.CellKind switch
        {
            CustomTableCellKind.DownloadIcon or CustomTableCellKind.ActionText => !string.IsNullOrWhiteSpace(hit.Column.GetText(hit.Row)),
            CustomTableCellKind.CheckBox => hit.Column.GetChecked(hit.Row).HasValue,
            _ => false,
        };
    }

    private void CustomTableViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
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
        CustomTableHitTestResult hit = HitTestTable(e.GetPosition(surface));
        if (hit.Kind == CustomTableHitKind.HeaderResize)
        {
            hit = new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, hit.Column, hit.ColumnIndex, hit.CellRect);
        }
        if (hit.Kind == CustomTableHitKind.Header)
        {
            if (IsStatusIconColumn(hit.Column))
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
        // 開始済みのドラッグは上で継続し、バー上の通常hoverだけを背後のセルから除外する。
        if (IsDescendantOfScrollChrome(e.OriginalSource as DependencyObject))
        {
            Cursor = null;
            CloseCellToolTip();
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
        if (e.Key is Key.Left or Key.Right && FindVisualParent<ScrollBar>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
        if (activeEditor == null
            && (e.Key is Key.Apps or Key.Return
                || (e.Key == Key.F10 && (modifiers & ModifierKeys.Shift) != 0)))
        {
            // 行操作の通知先が別の画面操作を始める前に、この入力の受付を確定します。
            e.Handled = true;
        }
        e.Handled |= HandleKeyDown(e.Key, modifiers);
    }

    /// <summary>
    /// 表が受け付けるキー操作を、入力キーと修飾キーの組み合わせから実行します。
    /// OSの修飾キー状態は入口で取得し、現在セル移動・選択・編集・行操作へ同じ入力値を渡します。
    /// </summary>
    /// <param name="key">処理するキー。</param>
    /// <param name="modifiers">キー入力時の修飾キー。</param>
    /// <returns>表の操作を受け付けた場合は <see langword="true"/>。</returns>
    internal bool HandleKeyDown(Key key, ModifierKeys modifiers)
    {
        if (activeEditor != null)
        {
            switch (key)
            {
                case Key.Escape:
                    if (editSuggestionListBox.Items.Count > 0)
                    {
                        CloseEditSuggestions();
                        return true;
                    }
                    CancelActiveEdit();
                    return true;
                case Key.Return:
                    if (CommitSelectedEditSuggestion())
                    {
                        return true;
                    }
                    CommitActiveEdit();
                    return true;
                case Key.Tab:
                    CommitActiveEdit();
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    return true;
                case Key.Down:
                    if (MoveEditSuggestionSelection(1))
                    {
                        return true;
                    }
                    break;
                case Key.Up:
                    if (MoveEditSuggestionSelection(-1))
                    {
                        return true;
                    }
                    break;
            }
            return false;
        }
        switch (ResolveKeyboardCommand(key, modifiers))
        {
            case CustomTableKeyboardCommand.SelectAllRows:
                return SelectAllRows();
            case CustomTableKeyboardCommand.CopyCurrentCell:
                return CopyCurrentCellToClipboard();
            case CustomTableKeyboardCommand.CopySelectedRowsTsv:
                return CopySelectedRowsToClipboard();
        }
        switch (key)
        {
            case Key.Left when modifiers == ModifierKeys.None:
                return MoveCurrentCellHorizontally(-1);
            case Key.Right when modifiers == ModifierKeys.None:
                return MoveCurrentCellHorizontally(1);
            case Key.F2:
                return TryBeginEditCurrentCell(null);
            case Key.Up:
                return MoveKeyboardSelection(-1, (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            case Key.Down:
                return MoveKeyboardSelection(1, (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            case Key.Apps:
            case Key.F10 when (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift:
                CustomTableHitTestResult contextHit = CreateSelectedRowHit();
                if (contextHit.Kind == CustomTableHitKind.Cell)
                {
                    RowContextMenuRequested?.Invoke(this, new CustomTableRowRequestedEventArgs(contextHit, openAtMousePosition: false));
                }
                return true;
            case Key.Return:
                CustomTableHitTestResult selectedHit = CreateSelectedRowHit();
                if (selectedHit.Kind == CustomTableHitKind.Cell)
                {
                    RowActivated?.Invoke(this, new CustomTableRowRequestedEventArgs(selectedHit, openAtMousePosition: false));
                }
                return true;
        }
        return false;
    }

    /// <summary>
    /// 表のキー入力を組み込み操作へ対応付けます。
    /// </summary>
    /// <param name="key">判定するキー。</param>
    /// <param name="modifiers">判定する修飾キー。</param>
    /// <returns>対応する組み込み操作。</returns>
    internal static CustomTableKeyboardCommand ResolveKeyboardCommand(Key key, ModifierKeys modifiers)
    {
        return key switch
        {
            Key.A when modifiers == ModifierKeys.Control => CustomTableKeyboardCommand.SelectAllRows,
            Key.C when modifiers == ModifierKeys.Control => CustomTableKeyboardCommand.CopyCurrentCell,
            Key.C when modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => CustomTableKeyboardCommand.CopySelectedRowsTsv,
            _ => CustomTableKeyboardCommand.None,
        };
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
        SetClipboardText(text);
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
        SetClipboardText(text);
        return true;
    }

    private void SetClipboardText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception exception)
        {
            // キー操作は受け付け済みです。コピーの成否とキーの処理済み状態を混同しません。
            _ = NotifyClipboardFailureAsync(exception);
        }
    }

    private async Task NotifyClipboardFailureAsync(Exception exception)
    {
        TryLogClipboardFailure(exception);
        try
        {
            UiDialogResult notification = await new UiDialogCoordinator().ShowMessageAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK,
                owner: Window.GetWindow(this)));
            UiDialogRoute.ThrowIfNotShown(notification, "Custom table clipboard failure notification");
        }
        catch (Exception notificationFailure) { TryLogClipboardFailure(notificationFailure); }
    }

    private static void TryLogClipboardFailure(Exception exception)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, "custom table clipboard"); }
        catch { /* 通知と診断の失敗をコピーの元原因へ合成しません。 */ }
    }

    private bool MoveCurrentCellHorizontally(int direction)
    {
        int rowIndex = selectionModel.CurrentIndex;
        if (rowIndex < 0 || rowIndex >= RowCount || !selectionModel.IsSelected(rowIndex) || SurfaceWidth <= 0d)
        {
            return false;
        }
        IReadOnlyList<CustomTableColumnLayoutEntry> entries = GetColumnLayoutSnapshot().Entries;
        int currentIndex = -1;
        if (currentCellHit?.Kind == CustomTableHitKind.Cell
            && currentCellHit.RowIndex == rowIndex
            && ReferenceEquals(currentCellHit.Row, ItemsSource[rowIndex]))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Column?.IsVisible == true
                    && string.Equals(entries[i].Column.Id, currentCellHit.Column?.Id, StringComparison.Ordinal))
                {
                    currentIndex = i;
                    break;
                }
            }
        }
        int nextIndex = currentIndex < 0 ? (direction > 0 ? 0 : entries.Count - 1) : currentIndex + direction;
        while (nextIndex >= 0 && nextIndex < entries.Count && entries[nextIndex].Column?.IsVisible != true)
        {
            nextIndex += direction;
        }
        if (nextIndex < 0 || nextIndex >= entries.Count)
        {
            // 端での入力は受け付けますが、手動スクロールも現在セルも維持します。
            return currentIndex >= 0;
        }
        CustomTableColumnLayoutEntry target = entries[nextIndex];
        EnsureRowVisible(rowIndex);
        EnsureColumnVisible(target, direction);
        if (!TryCreateCellHit(rowIndex, target.Column.Id, out CustomTableHitTestResult hit))
        {
            return false;
        }
        currentCellHit = hit;
        CloseCellToolTip();
        RequestRedraw("current_cell");
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
        IReadOnlyList<CustomTableColumnLayoutEntry> entries = GetColumnLayoutSnapshot().Entries;
        CustomTableColumnLayoutEntry? first = null;
        CustomTableColumnLayoutEntry? target = null;
        foreach (CustomTableColumnLayoutEntry entry in entries)
        {
            if (entry.Column?.IsVisible != true)
            {
                continue;
            }
            first ??= entry;
            if (string.Equals(entry.Column.Id, currentCellHit?.Column?.Id, StringComparison.Ordinal))
            {
                target = entry;
                break;
            }
        }
        target ??= first;
        currentCellHit = null;
        if (target is CustomTableColumnLayoutEntry column)
        {
            EnsureColumnVisible(column, direction: 0);
            TryCreateCellHit(rowIndex, column.Column.Id, out CustomTableHitTestResult hit);
            currentCellHit = hit;
        }
    }

    private void EnsureColumnVisible(CustomTableColumnLayoutEntry column, int direction)
    {
        double viewportWidth = horizontalScrollBar.ViewportSize;
        if (viewportWidth <= 0d)
        {
            return;
        }
        double left = column.TableX;
        double right = left + column.Width;
        double offset = HorizontalOffset;
        double nextOffset = offset;
        if (column.Width > viewportWidth)
        {
            if (direction > 0 || (direction == 0 && left >= offset + viewportWidth))
            {
                nextOffset = left;
            }
            else if (direction < 0 || right <= offset)
            {
                nextOffset = right - viewportWidth;
            }
        }
        else if (left < offset)
        {
            nextOffset = left;
        }
        else if (right > offset + viewportWidth)
        {
            nextOffset = right - viewportWidth;
        }
        horizontalScrollBar.Value = Math.Clamp(nextOffset, horizontalScrollBar.Minimum, horizontalScrollBar.Maximum);
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
        DataObject dataObject = CustomTableDataTransfer.CreateSelectedRowsDataObject(rows, RowDragKind, rowDragStartHit.Row);
        CloseCellToolTip();
        rowDragAdorner = new DragAdorner(this, CreateRowDragGhost(rows.Count), new Vector(12d, 12d))
        {
            Position = WPFUtil.GetMousePosition(this)
        };
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
            RebuildColumns();
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
        var beginningArgs = new CustomTableCellEditBeginningEventArgs(hit, hit.Column.EditPropertyName);
        CellEditBeginning?.Invoke(this, beginningArgs);
        if (beginningArgs.Cancel)
        {
            return false;
        }
        if (!TryCreateVisibleCellInteriorRect(hit.RowIndex, hit.Column?.Id, out Rect rect))
        {
            return false;
        }
        if (rect.Width <= 2d || rect.Height <= 2d)
        {
            return false;
        }
        Rect editorRect = CreateEditorRect(hit.Column, rect);
        TextBox textBox = CreateCellEditor(hit, replacementText);
        textBox.LostKeyboardFocus += ActiveEditorLostKeyboardFocus;
        Canvas.SetLeft(textBox, editorRect.Left);
        Canvas.SetTop(textBox, editorRect.Top);
        textBox.Width = editorRect.Width;
        textBox.Height = editorRect.Height;
        activeEditHit = hit;
        activeEditor = textBox;
        textBox.TextChanged += ActiveEditorTextChanged;
        editorLayer.Children.Add(textBox);
        CellEditStarted?.Invoke(this, new CustomTableCellEditStartedEventArgs(hit, hit.Column.EditPropertyName));
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

    private TextBox CreateCellEditor(CustomTableHitTestResult hit, string replacementText)
    {
        CustomTableTextStyle textStyle = ResolveTextStyle(hit.Column?.TextStyle);
        Typeface typeface = textStyle.CreateTypeface(ScoreFontFamily);
        var textBox = new TextBox
        {
            Style = CellEditorTextBoxStyle,
            Text = replacementText ?? hit.Column.GetEditText(hit.Row),
            TextAlignment = hit.Column.CellKind == CustomTableCellKind.DownloadIcon ? TextAlignment.Left : hit.Column.Alignment,
            TextWrapping = hit.Column.EditTextWrapping ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = false,
            FontFamily = typeface.FontFamily,
            FontSize = textStyle.FontSize,
            FontStyle = typeface.Style,
            FontStretch = typeface.Stretch,
            FontWeight = typeface.Weight,
            Margin = new Thickness(0d),
            MinWidth = 0d,
            MinHeight = 0d,
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            DataContext = hit.Row
        };
        ApplyCellEditorTheme(textBox);
        return textBox;
    }

    private static void ApplyCellEditorTheme(TextBox textBox)
    {
        CustomTablePalette palette = CustomTablePalette.Current;
        textBox.Background = palette.RowBackground;
        textBox.BorderBrush = palette.CurrentCellBackground;
        textBox.Foreground = palette.DefaultForeground;
        textBox.CaretBrush = palette.DefaultForeground;
    }

    private Rect CreateEditorRect(CustomTableColumn column, Rect cellRect)
    {
        double width = column?.EditOverlayWidth.HasValue == true ? Math.Max(cellRect.Width, column.EditOverlayWidth.Value) : cellRect.Width;
        width = Math.Min(width, Math.Max(cellRect.Width, surface.ActualWidth - cellRect.Left));
        return new Rect(cellRect.Left, cellRect.Top, width, cellRect.Height);
    }

    private void ActiveEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HandleEditFocusDeparture(activeEditor?.IsKeyboardFocusWithin == true || editSuggestionListBox.IsKeyboardFocusWithin);
    }

    /// <summary>編集欄と候補一覧の外へフォーカスが離れた場合、入力文字列を確定します。</summary>
    /// <param name="focusWithinEditControls">移動先が編集欄または候補一覧に含まれる場合は <see langword="true"/>。</param>
    internal void HandleEditFocusDeparture(bool focusWithinEditControls)
    {
        if (!completingEdit && !completingSuggestionSelection && activeEditor != null && !focusWithinEditControls)
        {
            CommitActiveEdit();
        }
    }

    private void ActiveEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        editSuggestionListBox.SelectedIndex = -1;
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
            editor.TextChanged -= ActiveEditorTextChanged;
            editorLayer.Children.Remove(editor);
            activeEditor = null;
            activeEditHit = null;
            if (hit != null)
            {
                rowCache.InvalidateRow(hit.Row);
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
        IReadOnlyList<string> suggestions = activeEditHit?.Column?.GetEditSuggestions(activeEditHit.Row) ?? [];
        editSuggestionListBox.ItemsSource = suggestions;
        editSuggestionListBox.SelectedIndex = -1;
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
        if (activeEditor == null || editSuggestionListBox.SelectedItem is not string selectedSuggestion || string.IsNullOrWhiteSpace(selectedSuggestion))
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
        ListBoxItem item = e.OriginalSource is not DependencyObject source ? null : FindVisualParent<ListBoxItem>(source);
        if (item != null)
        {
            if (HandleEditSuggestionClick(item.DataContext as string))
            {
                e.Handled = true;
            }
        }
    }

    /// <summary>クリックした候補の生パスを選択し、その候補を編集値として確定します。</summary>
    /// <param name="suggestion">クリック対象の候補パス。</param>
    /// <returns>表示中の候補を確定した場合は <see langword="true"/>。</returns>
    internal bool HandleEditSuggestionClick(string suggestion)
    {
        if (activeEditor == null || !editSuggestionListBox.Items.Contains(suggestion))
        {
            return false;
        }
        editSuggestionListBox.SelectedItem = suggestion;
        return CommitSelectedEditSuggestion();
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
                Rect rect = CreateVisibleCellSlotRect(x, width, rowIndex);
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

    private bool TryCreateVisibleCellInteriorRect(int rowIndex, string columnId, out Rect rect)
    {
        rect = Rect.Empty;
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
                rect = CreateVisibleCellInteriorRect(x, width, rowIndex);
                return rect.Width > 0d && rect.Height > 0d;
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
        Rect cellRect = column == null ? Rect.Empty : CreateVisibleCellSlotRect(0d, column.Width, rowIndex);
        return new CustomTableHitTestResult(CustomTableHitKind.Cell, rowIndex, rows[rowIndex], column, column == null ? -1 : 0, cellRect);
    }

    private Rect CreateVisibleCellSlotRect(double columnX, double columnWidth, int rowIndex)
    {
        double rowHeight = Math.Max(1d, RowHeight);
        double rowY = HeaderHeight + (rowIndex - FirstVisibleRowIndex) * rowHeight;
        double visibleHeight = Math.Min(rowHeight, Math.Max(0d, surface.ActualHeight - rowY));
        return CustomTableColumnLayout.CreateVisibleColumnSlotRect(
            columnX,
            columnWidth,
            HorizontalOffset,
            surface.ActualWidth,
            rowY,
            visibleHeight);
    }

    private Rect CreateVisibleCellInteriorRect(double columnX, double columnWidth, int rowIndex)
    {
        double rowHeight = Math.Max(1d, RowHeight);
        double rowY = HeaderHeight + (rowIndex - FirstVisibleRowIndex) * rowHeight;
        double visibleHeight = Math.Min(rowHeight, Math.Max(0d, surface.ActualHeight - rowY));
        return CustomTableColumnLayout.CreateVisibleColumnInteriorRect(
            columnX,
            columnWidth,
            HorizontalOffset,
            surface.ActualWidth,
            rowY,
            visibleHeight);
    }

    private static bool IsStatusIconColumn(CustomTableColumn column)
    {
        return column?.CellKind == CustomTableCellKind.StatusIcon;
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
        double? tooltipTextWidth = hit.Column.TooltipTextWidth;
        if (!string.Equals(toolTipText, tooltip, StringComparison.Ordinal) || toolTipTextWidth != tooltipTextWidth)
        {
            cellToolTip.Content = CreateCellToolTipContent(tooltip, tooltipTextWidth);
            toolTipText = tooltip;
            toolTipTextWidth = tooltipTextWidth;
        }
        toolTipRowIndex = hit.RowIndex;
        toolTipColumnId = hit.Column.Id;
        if (!cellToolTip.IsOpen)
        {
            cellToolTip.IsOpen = true;
        }
    }

    internal static object CreateCellToolTipContent(string tooltip, double? tooltipTextWidth)
    {
        if (!tooltipTextWidth.HasValue)
        {
            return tooltip;
        }
        return new TextBlock
        {
            Text = tooltip,
            Width = tooltipTextWidth.Value,
            TextWrapping = TextWrapping.Wrap
        };
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
            ResolveTextStyle(cellValue.TextStyle),
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
        toolTipText = null;
        toolTipTextWidth = null;
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
                || ReferenceEquals(source, horizontalScrollBar))
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    internal void NotifySurfaceRendered(
        int visibleRowCount,
        int visibleColumnCount,
        long renderWorkMs,
        double textCacheHitRate,
        string redrawReason,
        long rowHighlightMs = 0L,
        long cellValueMs = 0L,
        long textLayoutMs = 0L,
        long drawTextMs = 0L,
        int textCacheHits = 0,
        int textCacheMisses = 0)
    {
        int visibleCellCount = TableFirstVisibleMetrics.CalculateVisibleCellCount(visibleRowCount, visibleColumnCount);
        TryLogRenderMetrics(
            redrawReason,
            visibleRowCount,
            visibleColumnCount,
            visibleCellCount,
            renderWorkMs,
            textCacheHitRate,
            rowHighlightMs,
            cellValueMs,
            textLayoutMs,
            drawTextMs,
            textCacheHits,
            textCacheMisses);
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

    private void TryLogRenderMetrics(
        string redrawReason,
        int visibleRowCount,
        int visibleColumnCount,
        int visibleCellCount,
        long renderWorkMs,
        double textCacheHitRate,
        long rowHighlightMs,
        long cellValueMs,
        long textLayoutMs,
        long drawTextMs,
        int textCacheHits,
        int textCacheMisses)
    {
        if (!Net10PerformanceLog.IsEnabled)
        {
            return;
        }
        string reason = NormalizeRedrawReason(redrawReason);
        bool firstForReason = loggedRenderReasons.Add(reason);
        if (renderWorkMs < RenderSlowLogThresholdMs && !firstForReason && !IsAlwaysLoggedRenderReason(reason))
        {
            return;
        }
        QueuePerformanceLog(
            "custom_table_render",
            "reason=" + reason
            + " rowCount=" + RowCount
            + " visibleRowCount=" + visibleRowCount
            + " visibleColumnCount=" + visibleColumnCount
            + " visibleCellCount=" + visibleCellCount
            + " renderWorkMs=" + renderWorkMs
            + " rowHighlightMs=" + rowHighlightMs
            + " cellValueMs=" + cellValueMs
            + " textLayoutMs=" + textLayoutMs
            + " drawTextMs=" + drawTextMs
            + " textCacheHitRate=" + textCacheHitRate
            + " textCacheHits=" + textCacheHits
            + " textCacheMisses=" + textCacheMisses
            + " rowCacheCount=" + rowCache.Count);
    }

    private static void QueuePerformanceLog(string stage, string fields)
    {
        if (!Net10PerformanceLog.IsEnabled)
        {
            return;
        }
        var interaction = PerformanceInteraction.Start("custom_table");
        Net10PerformanceLog.Write(interaction, stage, fields);
    }

    private static bool IsAlwaysLoggedRenderReason(string reason)
    {
        return string.Equals(reason, "items_source_changed", StringComparison.Ordinal)
            || string.Equals(reason, "pending_items_source_swap_skipped", StringComparison.Ordinal);
    }
}

internal static class CustomTableColumnLayout
{
    internal const double GridLineThickness = 1d;

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

    internal static Rect CreateVisibleColumnSlotRect(double columnX, double columnWidth, double horizontalOffset, double viewportWidth, double y, double height)
    {
        double left = Math.Max(0d, columnX - horizontalOffset);
        double right = Math.Min(viewportWidth, columnX + columnWidth - horizontalOffset);
        return new Rect(left, y, Math.Max(0d, right - left), height);
    }

    internal static Rect CreateVisibleColumnInteriorRect(double columnX, double columnWidth, double horizontalOffset, double viewportWidth, double y, double height)
    {
        double slotLeft = columnX - horizontalOffset;
        double slotRight = slotLeft + Math.Max(0d, columnWidth);
        double visibleLeft = Math.Max(0d, slotLeft);
        double interiorRight = Math.Max(slotLeft, slotRight - GridLineThickness);
        double visibleRight = Math.Min(viewportWidth, interiorRight);
        double interiorHeight = Math.Max(0d, height - GridLineThickness);
        return new Rect(visibleLeft, y, Math.Max(0d, visibleRight - visibleLeft), interiorHeight);
    }

    internal static Rect CreateColumnSlotRect(double columnX, double columnWidth, double horizontalOffset, double y, double height)
    {
        return new Rect(columnX - horizontalOffset, y, Math.Max(0d, columnWidth), height);
    }

    internal static Rect CreateInteriorRect(Rect slotRect)
    {
        if (slotRect.IsEmpty)
        {
            return Rect.Empty;
        }
        double width = Math.Max(0d, slotRect.Width - GridLineThickness);
        double height = Math.Max(0d, slotRect.Height - GridLineThickness);
        return new Rect(slotRect.X, slotRect.Y, width, height);
    }

    internal static double CreateRightGridLineX(double columnX, double columnWidth, double horizontalOffset)
    {
        return columnX + columnWidth - horizontalOffset - GridLineThickness / 2d;
    }

    internal static double CreateBottomGridLineY(double y, double height)
    {
        return y + height - GridLineThickness / 2d;
    }

    internal static bool IsGridLineFullyVisible(double center, double viewportStart, double viewportEnd)
    {
        double halfThickness = GridLineThickness / 2d;
        return center - halfThickness >= viewportStart
            && center + halfThickness <= viewportEnd;
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
    private readonly CustomTableTextLayoutCache textLayoutCache = new();
    private DrawingEnvironment drawingEnvironment;
    private CustomTableTextStyle normalTextStyle;
    private CustomTableTextStyle boldTextStyle;
    private CustomTableTextStyle scoreTextStyle;
    private bool measureRenderDetails;

    // 高さと見出し高はセル群の内容を変えない。行の配置・保持範囲だけを再評価する。
    private sealed record DrawingEnvironment(
        CustomTableColumnLayoutSnapshot Layout, double Width, double HorizontalOffset, double RowHeight,
        double FontSize, FontFamily ScoreFontFamily, double PixelsPerDip,
        CultureInfo Culture, CultureInfo UICulture, CustomTablePalette Palette);

    private int renderTextCacheHits;
    private int renderTextCacheMisses;
    private long renderRowHighlightTicks;
    private long renderCellValueTicks;
    private long renderTextLayoutTicks;
    private long renderDrawTextTicks;

    /// <summary>行エントリのセル描画を配置し、表全体の背景・見出し・罫線を描画します。</summary>
    internal CustomTableSurface(CustomTableView owner)
    {
        this.owner = owner;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>文字条件の更新を保持済みセル描画へも反映します。</summary>
    internal void ClearTextLayoutCache()
    {
        textLayoutCache.Clear();
        owner.ClearRowDrawings();
    }

    /// <summary>DPI変更後の文字とセル描画を新しい表示倍率で作り直します。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearTextLayoutCache();
        InvalidateVisual();
    }

    private void UpdateDrawingEnvironment(CustomTableColumnLayoutSnapshot layout, double width)
    {
        var next = new DrawingEnvironment(layout, width, owner.HorizontalOffset, Math.Max(1d, owner.RowHeight),
            owner.TextFontSize, owner.ScoreFontFamily, VisualTreeHelper.GetDpi(this).PixelsPerDip,
            CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CustomTablePalette.Current);
        // 同名でも数値書式を変更したCultureInfoへ交換できるため、参照の変更も確認する。
        bool valuesChanged = drawingEnvironment != null
            && (!ReferenceEquals(drawingEnvironment.Culture, next.Culture)
                || !ReferenceEquals(drawingEnvironment.UICulture, next.UICulture)
                || !ReferenceEquals(drawingEnvironment.Palette, next.Palette));
        if (drawingEnvironment != next || valuesChanged)
        {
            if (valuesChanged)
            {
                owner.InvalidateDisplayValues();
            }
            bool textEnvironmentChanged = drawingEnvironment == null || valuesChanged
                || !drawingEnvironment.FontSize.Equals(next.FontSize)
                || !Equals(drawingEnvironment.ScoreFontFamily, next.ScoreFontFamily)
                || !drawingEnvironment.PixelsPerDip.Equals(next.PixelsPerDip);
            if (textEnvironmentChanged)
            {
                ClearTextLayoutCache();
            }
            else
            {
                // 横移動・列配置・行高は文字キー自身で区別できるため、既存FIFOを残す。
                owner.ClearRowDrawings();
            }
            drawingEnvironment = next;
        }
        normalTextStyle = CustomTableTextStyle.Normal.WithFontSize(next.FontSize);
        boldTextStyle = CustomTableTextStyle.NormalBold.WithFontSize(next.FontSize);
        scoreTextStyle = CustomTableTextStyle.Score.WithFontSize(next.FontSize);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var renderStopwatch = Stopwatch.StartNew();
        measureRenderDetails = Net10PerformanceLog.IsEnabled;
        renderTextCacheHits = 0;
        renderTextCacheMisses = 0;
        renderRowHighlightTicks = 0L;
        renderCellValueTicks = 0L;
        renderTextLayoutTicks = 0L;
        renderDrawTextTicks = 0L;
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0d || height <= 0d)
        {
            owner.NotifySurfaceRendered(0, 0, 0L, -1d, owner.ConsumeRedrawReason());
            return;
        }
        string redrawReason = owner.ConsumeRedrawReason();
        CustomTableColumnLayoutSnapshot layout = owner.GetColumnLayoutSnapshot();
        UpdateDrawingEnvironment(layout, width);
        DrawBackground(drawingContext, width, height);
        DrawHeader(drawingContext, layout, width, height);
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
        DrawRowDropInsertPreview(drawingContext, width, height);
        renderStopwatch.Stop();
        owner.NotifySurfaceRendered(
            visibleRowCount,
            layout.VisibleColumnCount,
            renderStopwatch.ElapsedMilliseconds,
            CustomTableTextLayoutCache.CalculateHitRate(renderTextCacheHits, renderTextCacheMisses),
            redrawReason,
            StopwatchTicksToMilliseconds(renderRowHighlightTicks),
            StopwatchTicksToMilliseconds(renderCellValueTicks),
            StopwatchTicksToMilliseconds(renderTextLayoutTicks),
            StopwatchTicksToMilliseconds(renderDrawTextTicks),
            renderTextCacheHits,
            renderTextCacheMisses);
    }

    private static long StopwatchTicksToMilliseconds(long ticks)
    {
        return ticks <= 0L ? 0L : (ticks * 1000L) / Stopwatch.Frequency;
    }

    private void DrawBackground(DrawingContext drawingContext, double width, double height)
    {
        drawingContext.DrawRectangle(drawingEnvironment.Palette.RowBackground, null, new Rect(0d, 0d, width, height));
    }

    private void DrawHeader(DrawingContext drawingContext, CustomTableColumnLayoutSnapshot layout, double width, double height)
    {
        double headerHeight = owner.HeaderHeight;
        double horizontalOffset = drawingEnvironment.HorizontalOffset;
        CustomTablePalette palette = drawingEnvironment.Palette;
        drawingContext.DrawRectangle(palette.HeaderBackground, null, new Rect(0d, 0d, width, headerHeight));
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column != null)
            {
                Rect cellRect = entry.CreateInteriorRect(horizontalOffset, 0d, headerHeight);
                if (owner.IsReorderSourceColumn(column))
                {
                    drawingContext.DrawRectangle(
                        palette.ReorderSourceHeader,
                        null,
                        entry.CreateVisibleInteriorRect(horizontalOffset, width, 0d, headerHeight));
                }
                DrawCellText(drawingContext, column.Header, cellRect, palette.DefaultForeground, [], TextAlignment.Center, normalTextStyle);
                DrawSortGlyph(drawingContext, column, cellRect);
                double borderX = entry.RightGridLineX(horizontalOffset);
                if (CustomTableColumnLayout.IsGridLineFullyVisible(borderX, 0d, width))
                {
                    drawingContext.DrawLine(palette.HeaderBorder, new Point(borderX, 0d), new Point(borderX, headerHeight));
                }
            }
        }
        // ツールバーの段数や列の有無によらず、ヘッダー自身が全幅の上枠を所有する。
        double topGridLineY = CustomTableColumnLayout.GridLineThickness / 2d;
        if (CustomTableColumnLayout.IsGridLineFullyVisible(topGridLineY, 0d, height))
        {
            drawingContext.DrawLine(palette.HeaderBorder, new Point(0d, topGridLineY), new Point(width, topGridLineY));
        }
        double bottomGridLineY = CustomTableColumnLayout.CreateBottomGridLineY(0d, headerHeight);
        if (CustomTableColumnLayout.IsGridLineFullyVisible(bottomGridLineY, 0d, height))
        {
            drawingContext.DrawLine(palette.HeaderBorder, new Point(0d, bottomGridLineY), new Point(width, bottomGridLineY));
        }
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
            CustomTableRowEntry rowEntry = owner.GetRowEntry(row);
            bool selected = owner.IsRowSelected(rowIndex);
            CustomTableColumn currentColumn = owner.GetCurrentCellColumn(rowIndex);
            CustomTablePalette palette = drawingEnvironment.Palette;
            long highlightStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
            bool highlightedWarning = rowEntry.HasHighlightedWarning;
            if (measureRenderDetails)
            {
                renderRowHighlightTicks += Stopwatch.GetTimestamp() - highlightStart;
            }
            Brush rowBackground = selected
                ? palette.SelectedRowBackground
                : highlightedWarning
                    ? palette.WarningRowBackground
                    : (rowIndex & 1) == 1
                        ? palette.AlternatingRowBackground
                        : palette.RowBackground;
            var rowRect = new Rect(0d, y, width, Math.Min(rowHeight, height - y));
            drawingContext.DrawRectangle(rowBackground, null, rowRect);
            if (!rowEntry.Drawings.TryGetValue(rowIndex, out CustomTableRowDrawing retained)
                || retained.Selected != selected || !ReferenceEquals(retained.CurrentColumn, currentColumn))
            {
                var drawing = new DrawingGroup();
                using (DrawingContext cells = drawing.Open())
                {
                    DrawRowCells(cells, layout, rowEntry, selected, currentColumn, width, rowHeight);
                }
                retained = new CustomTableRowDrawing(drawing, selected, currentColumn);
                rowEntry.Drawings[rowIndex] = retained;
            }
            // セル群は通常行高で記録する。部分行の可視範囲はSurfaceのクリップが決める。
            drawingContext.PushTransform(new TranslateTransform(0d, y));
            drawingContext.DrawDrawing(retained.Drawing);
            drawingContext.Pop();
            double bottomGridLineY = CustomTableColumnLayout.CreateBottomGridLineY(y, rowHeight);
            if (CustomTableColumnLayout.IsGridLineFullyVisible(bottomGridLineY, 0d, height))
            {
                drawingContext.DrawLine(palette.CellBorder, new Point(0d, bottomGridLineY), new Point(width, bottomGridLineY));
            }
            y += rowHeight;
            drawnRows++;
        }
        return drawnRows;
    }

    private void DrawRowDropInsertPreview(DrawingContext drawingContext, double width, double height)
    {
        if (!owner.IsRowDropInsertPreviewActive || width <= 0d || height <= owner.HeaderHeight)
        {
            return;
        }
        IList rows = owner.ItemsSource;
        int rowCount = rows?.Count ?? 0;
        int insertIndex = Math.Max(0, Math.Min(rowCount, owner.RowDropPreviewInsertIndex));
        int firstRowIndex = owner.FirstVisibleRowIndex;
        int visibleCapacity = owner.CalculateDrawableRowCapacity();
        int lastVisibleExclusive = Math.Min(rowCount, firstRowIndex + visibleCapacity);
        double rowHeight = Math.Max(1d, owner.RowHeight);
        double y;
        if (insertIndex <= firstRowIndex)
        {
            y = owner.HeaderHeight;
        }
        else if (insertIndex >= lastVisibleExclusive)
        {
            y = owner.HeaderHeight + Math.Max(0, lastVisibleExclusive - firstRowIndex) * rowHeight;
        }
        else
        {
            y = owner.HeaderHeight + (insertIndex - firstRowIndex) * rowHeight;
        }
        y = Math.Max(owner.HeaderHeight, Math.Min(height, y));
        drawingContext.DrawLine(drawingEnvironment.Palette.ColumnReorderInsert, new Point(0d, y), new Point(width, y));
    }

    private void DrawRowCells(DrawingContext drawingContext, CustomTableColumnLayoutSnapshot layout, CustomTableRowEntry row, bool selected, CustomTableColumn currentColumn, double width, double rowHeight)
    {
        const double y = 0d;
        double horizontalOffset = drawingEnvironment.HorizontalOffset;
        CustomTablePalette palette = drawingEnvironment.Palette;
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column == null)
            {
                continue;
            }
            Rect cellRect = entry.CreateInteriorRect(horizontalOffset, y, rowHeight);
            bool currentCell = currentColumn != null && string.Equals(currentColumn.Id, column.Id, StringComparison.Ordinal);
            long cellValueStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
            CustomTableCellValue cellValue = row.GetValue(column, out _);
            if (measureRenderDetails)
            {
                renderCellValueTicks += Stopwatch.GetTimestamp() - cellValueStart;
            }
            if (!selected && cellValue.Background != null)
            {
                drawingContext.DrawRectangle(cellValue.Background, null, entry.CreateVisibleInteriorRect(horizontalOffset, width, y, rowHeight));
            }
            if (currentCell)
            {
                drawingContext.DrawRectangle(palette.CurrentCellBackground, null, entry.CreateVisibleInteriorRect(horizontalOffset, width, y, rowHeight));
            }
            bool hasTextRuns = cellValue.TextRuns.Count > 0;
            Brush foreground = hasTextRuns
                ? cellValue.Foreground
                : currentCell
                    ? palette.CurrentCellForeground
                    : selected
                        ? palette.SelectedForeground
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
                DrawCellText(drawingContext, cellValue.Text, cellRect, foreground, cellValue.TextRuns, cellValue.Alignment, ResolveCellTextStyle(cellValue.TextStyle));
            }
            double borderX = entry.RightGridLineX(horizontalOffset);
            if (CustomTableColumnLayout.IsGridLineFullyVisible(borderX, 0d, width))
            {
                drawingContext.DrawLine(palette.CellBorder, new Point(borderX, y), new Point(borderX, y + rowHeight));
            }
        }
    }

    private CustomTableTextStyle ResolveCellTextStyle(CustomTableTextStyle style)
    {
        if (ReferenceEquals(style, CustomTableTextStyle.Normal))
        {
            return normalTextStyle;
        }
        if (ReferenceEquals(style, CustomTableTextStyle.NormalBold))
        {
            return boldTextStyle;
        }
        if (ReferenceEquals(style, CustomTableTextStyle.Score))
        {
            return scoreTextStyle;
        }
        // サイズ適用済みのスタイルを渡された場合も、元の書体・縦補正を保持する。
        return style.WithFontSize(drawingEnvironment.FontSize);
    }

    private void DrawCellText(DrawingContext drawingContext, string text, Rect cellRect, Brush foreground, IReadOnlyList<CustomTableTextRunStyle> textRuns, TextAlignment alignment, CustomTableTextStyle textStyle)
    {
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 2d || cellRect.Height <= 1d)
        {
            return;
        }
        CustomTableTextStyle effectiveTextStyle = textStyle ?? normalTextStyle;
        double pixelsPerDip = drawingEnvironment.PixelsPerDip;
        double maxTextWidth = Math.Max(1d, cellRect.Width - CustomTableView.CellTextHorizontalPadding * 2d);
        double maxTextHeight = Math.Max(Math.Max(1d, cellRect.Height), effectiveTextStyle.FontSize * 2d);
        long layoutStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
        FormattedText formattedText = textLayoutCache.GetOrCreate(
            text,
            maxTextWidth,
            maxTextHeight,
            alignment,
            effectiveTextStyle,
            drawingEnvironment.ScoreFontFamily,
            foreground,
            textRuns,
            pixelsPerDip,
            drawingEnvironment.UICulture,
            out bool cacheHit);
        if (measureRenderDetails)
        {
            renderTextLayoutTicks += Stopwatch.GetTimestamp() - layoutStart;
        }
        if (cacheHit)
        {
            renderTextCacheHits++;
        }
        else
        {
            renderTextCacheMisses++;
        }
        double x = cellRect.X + CustomTableView.CellTextHorizontalPadding;
        double y = cellRect.Y + Math.Max(0d, (cellRect.Height - formattedText.Height) / 2d) + effectiveTextStyle.VerticalOffset;
        long drawTextStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
        drawingContext.PushClip(new RectangleGeometry(cellRect));
        drawingContext.DrawText(formattedText, new Point(x, y));
        drawingContext.Pop();
        if (measureRenderDetails)
        {
            renderDrawTextTicks += Stopwatch.GetTimestamp() - drawTextStart;
        }
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
        var pen = new Pen(iconBrush, 1.7d)
        {
            StartLineCap = PenLineCap.Square,
            EndLineCap = PenLineCap.Square
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        var arrow = new StreamGeometry();
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
        var iconRect = new Rect(left, top, size, size);
        var pen = new Pen(iconBrush, 1.5d)
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

    private void DrawCheckBox(DrawingContext drawingContext, bool? isChecked, Rect cellRect, Brush foreground)
    {
        if (!isChecked.HasValue || cellRect.Width <= 8d || cellRect.Height <= 8d)
        {
            return;
        }
        double size = Math.Max(8d, Math.Min(12d, Math.Min(cellRect.Width - 4d, cellRect.Height - 4d)));
        double left = cellRect.Left + (cellRect.Width - size) / 2d;
        double top = cellRect.Top + (cellRect.Height - size) / 2d;
        var rect = new Rect(left, top, size, size);
        CustomTablePalette palette = drawingEnvironment.Palette;
        drawingContext.DrawRectangle(palette.CheckBoxBackground, palette.CheckBoxBorder, rect);
        if (isChecked.Value)
        {
            Pen checkPen = foreground == null || ReferenceEquals(foreground, palette.DefaultForeground)
                ? palette.CheckBoxCheck
                : new Pen(foreground, 1.8d);
            drawingContext.DrawLine(checkPen, new Point(rect.Left + 2.5d, rect.Top + size * 0.55d), new Point(rect.Left + size * 0.43d, rect.Bottom - 2.5d));
            drawingContext.DrawLine(checkPen, new Point(rect.Left + size * 0.43d, rect.Bottom - 2.5d), new Point(rect.Right - 2d, rect.Top + 2.5d));
        }
    }

    private static void DrawRefreshIcon(DrawingContext drawingContext, Brush brush, Pen pen, Rect rect)
    {
        var start = new Point(rect.Right - 2d, rect.Top + rect.Height * 0.45d);
        var end = new Point(rect.Left + 2d, rect.Top + rect.Height * 0.58d);
        var arc = new StreamGeometry();
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
        var geometry = new StreamGeometry();
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
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(p1, isFilled: true, isClosed: true);
            context.LineTo(p2, isStroked: true, isSmoothJoin: false);
            context.LineTo(p3, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(drawingEnvironment.Palette.SortGlyph, null, geometry);
    }
}
