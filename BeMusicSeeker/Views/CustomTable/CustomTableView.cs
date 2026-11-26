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

public sealed partial class CustomTableView : Grid
{
    internal const double CellTextHorizontalPadding = 2d;
    private const double ColumnResizeHitTestMargin = 4d;
    // 共通ScrollBarの固定ヒット領域と、末尾を露出するための計算を揃える。
    internal const double ScrollBarThickness = 16d;
    // 背景を細い状態から不透明に表示する一覧用ScrollBar Styleの資源キー。
    private const string ListScrollBarStyleKey = "App.Canonical.ListScrollBarStyle";
    private const double CellEditorBorderThickness = 1d;
    internal const int RowSubscriptionOverscan = 5;
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

    /// <summary>統合候補一覧の列設定です。起動中だけの設定で、設定へ保存しません。指定すると、この表は統合候補一覧の列を持ちます。</summary>
    public static readonly DependencyProperty DuplicateCandidateColumnsSettingsProperty = DependencyProperty.Register(
        nameof(DuplicateCandidateColumnsSettings),
        typeof(DuplicateMergeCandidateColumnSettings),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, OnDuplicateCandidateColumnsSettingsChanged));

    /// <summary>重複音源の整理候補一覧の列設定です。起動中だけの設定で、設定へ保存しません。指定すると、この表は整理候補一覧の列を持ちます。</summary>
    public static readonly DependencyProperty AudioDuplicateCandidateColumnsSettingsProperty = DependencyProperty.Register(
        nameof(AudioDuplicateCandidateColumnsSettings),
        typeof(AudioDuplicateCandidateColumnSettings),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(null, OnDuplicateCandidateColumnsSettingsChanged));

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

    /// <summary>
    /// 通常の文字、見出し、セルの編集欄に使うフォントを識別します。未指定とnullは既定の表示フォントです。
    /// </summary>
    public static readonly DependencyProperty TextFontFamilyProperty = DependencyProperty.Register(
        nameof(TextFontFamily),
        typeof(FontFamily),
        typeof(CustomTableView),
        new FrameworkPropertyMetadata(AppFontService.DefaultTableFontFamily, FrameworkPropertyMetadataOptions.AffectsRender, OnTextMetricChanged, CoerceTextFontFamily));

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
    // 保持範囲を購読してよい状態です。表示の切替（ApplyVisibilityChanged）で決まり、非表示の間は購読を持ちません。
    private bool isRowRetentionActive;
    private bool isRowRetentionReleased;
    private RetainedRowsUpdate pendingRetainedRowsUpdate;
    private string pendingRetainedRowsReason;
    private bool pendingRetainedRowsLogAlways;
    // ホイールの回転量のうち、一刻みに満たない分です。表ごとに持ち、向きが変わるか範囲の端に達すると捨てます。
    private int wheelRemainder;
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
    private IUiDialogService dialogService;

    /// <summary>
    /// 通知に使う構成の共通窓口を接続します。XAMLの要素として引数なしで生成されるため、生成の後にホストが一度設定します。
    /// 未設定のまま通知しようとした場合は、通知の失敗として診断に残します。
    /// </summary>
    /// <param name="dialogService">コピー失敗などの通知に使う窓口。</param>
    internal void ConfigureDialogs(IUiDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        this.dialogService = dialogService;
    }

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
        // 本体の2本だけ一覧用Styleを明示する。暗黙Styleにしないため、編集候補Popupなど同じ資源範囲の他のバーへは波及しない。
        verticalScrollBar.SetResourceReference(StyleProperty, ListScrollBarStyleKey);
        horizontalScrollBar.SetResourceReference(StyleProperty, ListScrollBarStyleKey);
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
            MarkRetainedRowsStale("surface_size_changed");
            RefreshAutomationChildren();
            RequestRedraw("surface_size_changed");
        };
        SizeChanged += delegate (object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                InvalidateColumnLayoutSnapshot();
            }
            UpdateScrollBars();
            MarkRetainedRowsStale("size_changed");
            RefreshAutomationChildren();
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
        ResourceService.Current.PropertyChanged -= ResourceServicePropertyChanged;
        ResourceService.Current.PropertyChanged += ResourceServicePropertyChanged;
        SubscribeMainChartList(DataContext as MainChartListViewModel);
        RefreshTheme("loaded");
    }

    private void CustomTableViewUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeService.ThemeChanged -= AppThemeServiceThemeChanged;
        ResourceService.Current.PropertyChanged -= ResourceServicePropertyChanged;
        SubscribeMainChartList(null);
        ApplyVisibilityChanged(false);
    }

    private void CustomTableViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SubscribeMainChartList(e.NewValue as MainChartListViewModel);
        }
        InvalidateColumnCellValues();
        RequestRedraw("operation_context");
        RaiseAutomationChanged(CustomTableAutomationChange.Value);
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
            subscribedMainChartList.OperationContextChanged -= MainChartListOperationContextChanged;
        }
        subscribedMainChartList = mainChartList;
        if (subscribedMainChartList != null)
        {
            subscribedMainChartList.RowsReplacing += MainChartListRowsReplacing;
            subscribedMainChartList.RowsSelectionPreserving += MainChartListRowsSelectionPreserving;
            subscribedMainChartList.RowsReplacementCanceled += MainChartListRowsReplacementCanceled;
            subscribedMainChartList.RowsReplacementPublishFailed += MainChartListRowsReplacementCanceled;
            subscribedMainChartList.DisplayRefreshRequested += MainChartListDisplayRefreshRequested;
            subscribedMainChartList.OperationContextChanged += MainChartListOperationContextChanged;
        }
    }

    private void MainChartListRowsReplacing(object sender, EventArgs e)
    {
        InvokePresentationAction(PrepareForItemsSourceSwap);
    }

    /// <summary>識別対応の譜面一覧へ移るBinding公開後に、同じ元集合なら選択識別を復元し、識別非対応の元表示や別集合の旧添字による選択を解除します。</summary>
    private void MainChartListRowsSelectionPreserving(object sender, MainChartListSelectionRestoration e)
    {
        InvokePresentationAction(() =>
        {
            if (e.Rows is not ICustomTableSelectionIdentitySource after) { return; }
            if (ItemsSource is not ICustomTableSelectionIdentitySource before
                || !before.SharesSelectionIdentityScope(after))
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

    /// <summary>生存する表示文脈の能力変更だけで列値と描画を失効させ、editor・選択・現在セルを保持します。</summary>
    private void MainChartListOperationContextChanged(object sender, EventArgs e)
    {
        InvokePresentationAction(() =>
        {
            if (!IsLoaded || !ReferenceEquals(sender, subscribedMainChartList)) { return; }
            InvalidateColumnCellValues();
            RequestRedraw("operation_context");
            RaiseAutomationChanged(CustomTableAutomationChange.Value);
        });
    }

    /// <summary>表示のたびに最新文脈を一度捕捉します。受付を伴う編集開始probeは使いません。</summary>
    private bool CanEditInstallDestination(object row)
    {
        if (DataContext is not MainChartListViewModel mainChartList) { return false; }
        MainChartListOperationContext context = mainChartList.CurrentOperationContext;
        return GridRowResolver.TryGetInstallDestinationEditTarget(row, context.SourceScope, context.OperationSection, out _);
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

    // アプリ言語はthread cultureを変えないため、表示資源の通知で既存cacheを更新します。
    // 旧語を捕捉せずUI適用時の現在語を使い、切離し後の配送では表へ触れません。
    private void ResourceServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(ResourceService.Resources)) { return; }
        InvokePresentationAction(() =>
        {
            if (!IsLoaded) { return; }
            CloseCellToolTip();
            InvalidateDisplayValues();
            surface.ClearTextLayoutCache();
            RequestRedraw("language_changed");
            RaiseAutomationChanged(CustomTableAutomationChange.Value);
        });
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

    public DuplicateMergeCandidateColumnSettings DuplicateCandidateColumnsSettings
    {
        get => (DuplicateMergeCandidateColumnSettings)GetValue(DuplicateCandidateColumnsSettingsProperty);
        set => SetValue(DuplicateCandidateColumnsSettingsProperty, value);
    }

    public AudioDuplicateCandidateColumnSettings AudioDuplicateCandidateColumnsSettings
    {
        get => (AudioDuplicateCandidateColumnSettings)GetValue(AudioDuplicateCandidateColumnsSettingsProperty);
        set => SetValue(AudioDuplicateCandidateColumnsSettingsProperty, value);
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

    /// <summary>
    /// 行と見出しの高さを表示倍率で物理画素の整数へ丸めた、縦の寸法です。描画・当たり判定・編集欄の位置・
    /// スクロール範囲と完全表示行容量・行ドロップの位置・UI Automationの矩形が共通に使います。
    /// <see cref="RowHeight"/>・<see cref="HeaderHeight"/> の値と保存値は変えません。
    /// </summary>
    internal CustomTableRowGeometry RowGeometry => new(RowHeight, HeaderHeight, surface.ActualHeight, PixelsPerDip);

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

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

    /// <summary>通常の文字、見出し、セルの編集欄に使うフォントです。変更すると文字の配置を作り直します。</summary>
    public FontFamily TextFontFamily
    {
        get => (FontFamily)GetValue(TextFontFamilyProperty);
        set => SetValue(TextFontFamilyProperty, value);
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

    /// <summary>
    /// 行グループの提供元です。<see cref="ItemsSource"/> が <see cref="ICustomTableRowGroupSource"/> を実装する表だけが持ち、
    /// 行集合の差替えと同時に切り替わります。持たない表（既存の全用途）はnullで、描画・操作・UI Automationは変わりません。
    /// </summary>
    internal ICustomTableRowGroupSource RowGroupSource => ItemsSource as ICustomTableRowGroupSource;

    /// <summary>バー背後を含む全面の実描画幅です。スクロール用の有効表示幅とは区別します。</summary>
    internal double SurfaceWidth => surface.ActualWidth;

    /// <summary>バー背後を含む全面の実描画高です。完全表示行容量とは区別します。</summary>
    internal double SurfaceHeight => surface.ActualHeight;

    internal bool IsRowSelected(int rowIndex)
    {
        return selectionModel.IsSelected(rowIndex);
    }

    /// <summary>行番号の範囲 [<paramref name="firstRowIndex"/>, <paramref name="endRowIndex"/>) の全行が選択済みかを返します。画面外の行も含め、区間として判定します。</summary>
    internal bool IsRowSpanSelected(int firstRowIndex, int endRowIndex)
    {
        return selectionModel.IsSpanSelected(firstRowIndex, endRowIndex - 1);
    }

    /// <summary>現在セルの行が行番号の範囲 [<paramref name="firstRowIndex"/>, <paramref name="endRowIndex"/>) にあれば、その列を返します。なければnullです。</summary>
    internal CustomTableColumn GetCurrentCellColumnInRange(int firstRowIndex, int endRowIndex)
    {
        return currentCellHit?.Kind == CustomTableHitKind.Cell && currentCellHit.RowIndex >= firstRowIndex && currentCellHit.RowIndex < endRowIndex
            ? currentCellHit.Column
            : null;
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
        return (textStyle ?? CustomTableTextStyle.Normal).WithFontFamily(TextFontFamily).WithFontSize(TextFontSize);
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
        if (e.NewValue == null)
        {
            // 読み込み解除の経路でも、描画先と文字のレイアウトを解放する。次の描画で必要な分だけ作り直す。
            view.surface?.ReleaseDrawingResources();
        }
        view.DetachCollectionChanged(e.OldValue as INotifyCollectionChanged);
        view.AttachCollectionChanged(e.NewValue as INotifyCollectionChanged);
        view.MarkItemsApplied();
        view.InvalidateAllCellValues();
        view.CoerceSelectionToCurrentRows();
        view.UpdateScrollBars();
        view.MarkRetainedRowsRebuild("items_source_changed", logAlways: true);
        view.RequestRedraw("items_source_changed");
        view.RaiseAutomationChanged(CustomTableAutomationChange.Structure);
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        view.CommitActiveEdit();
        view.currentCellHit = null;
        view.InvalidateColumnLayoutSnapshot();
        view.InvalidateColumnCellValues();
        view.UpdateScrollBars();
        view.RaiseAutomationChanged(CustomTableAutomationChange.Structure);
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

    private static void OnDuplicateCandidateColumnsSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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
        view.MarkRetainedRowsStale("layout_metric_changed");
        view.RefreshAutomationChildren();
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

    private static object CoerceTextFontFamily(DependencyObject d, object baseValue)
    {
        return baseValue as FontFamily ?? AppFontService.DefaultTableFontFamily;
    }

    /// <summary>外部から実際に受理した主行変更だけで旧編集を一回確定し、同期再入後の最新主行へ現在セルと可視範囲を揃えます。</summary>
    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CustomTableView)d;
        if (!view.updatingSelectedIndexFromSelection && !view.selectionIdentities.HasValue)
        {
            int selectedIndex = (int)e.NewValue;
            if (selectedIndex < 0 || view.RowCount > 0)
            {
                int previousCurrent = view.selectionModel.CurrentIndex;
                view.selectionModel.SetItemCount(view.RowCount);
                view.selectionModel.SyncCurrentIndex(selectedIndex);
                if (previousCurrent != view.selectionModel.CurrentIndex)
                {
                    string columnId = view.currentCellHit?.Column?.Id;
                    IList rows = view.ItemsSource;
                    view.CommitActiveEdit();
                    // 編集終端の同期整列で主行が移動し得るため、入力の旧添字を再適用しない。
                    view.EnsureRowVisible(view.selectionModel.CurrentIndex);
                    view.EnsureCurrentCellForCurrentRow(ReferenceEquals(rows, view.ItemsSource) ? columnId : null);
                }
            }
        }
        view.RequestRedraw("selection");
        view.RaiseAutomationChanged(CustomTableAutomationChange.Selection);
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
        if (!ReferenceEquals(sender, ItemsSource)) { return; }
        CommitActiveEdit();
        currentCellHit = null;
        ClearPendingItemsSourceSwapColumnRedrawSuppression();
        MarkItemsApplied();
        InvalidateAllCellValues();
        if (!selectionIdentities.HasValue) { CoerceSelectionToCurrentRows(); }
        UpdateScrollBars();
        MarkRetainedRowsRebuild(
            GetCollectionChangedSubscriptionReason(e),
            logAlways: e?.Action == NotifyCollectionChangedAction.Reset);
        RequestRedraw("items_source_changed");
        RaiseAutomationChanged(CustomTableAutomationChange.Structure);
    }

    /// <summary>選択・主行・Shift基点だけを、更新前の元集合識別で短命に捕捉します。</summary>
    private void CaptureSelectionIdentities(object sender, EventArgs e)
    {
        if (sender is not ICustomTableSelectionIdentitySource source || !ReferenceEquals(sender, ItemsSource)) { return; }
        if (e is CustomTableSelectionResetStartingEventArgs)
        {
            // 編集終端の同期再入が局所更新を完結してから、外側の旧行と選択を捕捉する。
            CommitActiveEdit();
            if (!ReferenceEquals(sender, ItemsSource)) { return; }
        }
        selectionIdentities = CaptureSelectionIdentities(source);
    }

    /// <summary>実体化なしの索引を使って、選択数に比例する識別だけを取得します。</summary>
    private (object[] Selected, object Current, object Anchor) CaptureSelectionIdentities(ICustomTableSelectionIdentitySource source)
        => (selectionModel.SelectedIndices.Select(source.GetSelectionIdentity).ToArray(),
            source.GetSelectionIdentity(selectionModel.CurrentIndex), source.GetSelectionIdentity(selectionModel.AnchorIndex));

    /// <summary>最終順序へ選択を戻します。主行の添字が同じでも副選択と基点を必ず照合します。</summary>
    private void RestoreSelectionIdentities(object sender, EventArgs e)
    {
        if (sender is not ICustomTableSelectionIdentitySource source || !ReferenceEquals(sender, ItemsSource)) { return; }
        (object[] Selected, object Current, object Anchor)? captured = selectionIdentities;
        selectionIdentities = null;
        if (e is CustomTableSelectionUpdateEventArgs requested)
        {
            RestoreSelectionIdentities(source, (requested.Selected, requested.Current, requested.Anchor));
            EnsureRowVisible(selectionModel.CurrentIndex);
            EnsureCurrentCellForCurrentRow();
        }
        else if (captured.HasValue) { RestoreSelectionIdentities(source, captured.Value); }
    }

    /// <summary>残る選択・主行・基点だけを採用し、同じ主添字でも副選択の旧位置を使いません。</summary>
    private void RestoreSelectionIdentities(ICustomTableSelectionIdentitySource source, (object[] Selected, object Current, object Anchor) captured)
    {
        selectionModel.SetItemCount(RowCount);
        bool changed = selectionModel.RestoreIndices(captured.Selected.Select(source.IndexOfSelectionIdentity),
            source.IndexOfSelectionIdentity(captured.Current), source.IndexOfSelectionIdentity(captured.Anchor));
        // 行数減少によるcoerceが先に空にした場合も、選択消失を通知する。
        changed |= captured.Selected.Length != selectionModel.SelectedCount;
        UpdateSelectedIndexFromSelectionModel();
        if (changed) { RaiseSelectionChanged(); }
        RequestRedraw("selection");
        RaiseAutomationChanged(CustomTableAutomationChange.Selection);
    }

    internal void ApplyVisibilityChanged(bool isVisible)
    {
        isRowRetentionActive = isVisible;
        isRowRetentionReleased = !isVisible;
        if (isVisible)
        {
            MarkItemsApplied();
            UpdateScrollBars();
            // 非表示の間は購読を持たないため、最初の描画で最新の位置の範囲を全件取得する。
            MarkRetainedRowsRebuild("visible_changed", logAlways: true);
            RefreshAutomationChildren();
            RequestRedraw("visible_changed");
            return;
        }

        CommitActiveEdit();
        CloseCellToolTip();
        rowChangeTracker.DetachAllRows();
        // 保留中の範囲の更新は破棄する。後の配置や消化で再購読や行集合の読取りをしない。
        pendingRetainedRowsUpdate = RetainedRowsUpdate.None;
        EndColumnResize();
        ClearDragState();
        ClearHeaderDragState();
        pendingRowInvalidations.Clear();
        rowCache.Clear();
        // 再表示では、最新の状態から描画先と文字のレイアウトを作り直す。
        surface.ReleaseDrawingResources();
    }

    /// <summary>倍率が変わると行と見出しの高さの実効値が変わるため、編集欄を確定し、スクロール範囲と可視行を更新して描き直します。</summary>
    internal void HandleDpiChanged()
    {
        CommitActiveEdit();
        CloseCellToolTip();
        UpdateScrollBars();
        MarkRetainedRowsStale("dpi_changed");
        RefreshAutomationChildren();
        RequestRedraw("dpi_changed");
    }

    private void AttachColumnLayoutHandlers()
    {
        IEnumerable<ICustomTableColumnLayout> layouts = AudioDuplicateCandidateColumnsSettings != null
            ? CustomTableColumnFactory.EnumerateAudioDuplicateCandidateColumnLayouts(AudioDuplicateCandidateColumnsSettings).Cast<ICustomTableColumnLayout>()
            : DuplicateCandidateColumnsSettings != null
            ? CustomTableColumnFactory.EnumerateDuplicateCandidateColumnLayouts(DuplicateCandidateColumnsSettings).Cast<ICustomTableColumnLayout>()
            : PlaylistSummaryColumnsSettings != null
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
        if (e.PropertyName == nameof(ICustomTableColumnLayout.Width) && sender is ICustomTableColumnLayout layout)
        {
            ApplyColumnWidthChange(layout);
            return;
        }
        currentCellHit = null;
        RebuildColumns();
    }

    // 列インスタンスと他列の取得済み値を保ち、幅で役割が変わるURL列だけを再評価します。
    private void ApplyColumnWidthChange(ICustomTableColumnLayout layout)
    {
        bool valuesChanged = false;
        foreach (CustomTableColumn column in VisibleColumns)
        {
            if (ReferenceEquals(column.Layout, layout) && column.HasWidthDependentContent)
            {
                rowCache.InvalidateColumnValues(column.Id);
                valuesChanged = true;
            }
        }
        InvalidateColumnLayoutSnapshot();
        CloseCellToolTip();
        UpdateScrollBars();
        RefreshAutomationChildren();
        RequestRedraw("column_resize");
        if (valuesChanged)
        {
            RaiseAutomationChanged(CustomTableAutomationChange.Value);
        }
    }

    private void RebuildColumns()
    {
        Columns = AudioDuplicateCandidateColumnsSettings != null
            ? CustomTableColumnFactory.CreateAudioDuplicateCandidateColumns(AudioDuplicateCandidateColumnsSettings).ToArray()
            : DuplicateCandidateColumnsSettings != null
            ? CustomTableColumnFactory.CreateDuplicateCandidateColumns(DuplicateCandidateColumnsSettings).ToArray()
            : PlaylistSummaryColumnsSettings != null
            ? CustomTableColumnFactory.CreatePlaylistSummaryColumns(PlaylistSummaryColumnsSettings).ToArray()
            : [.. CustomTableColumnFactory.CreateMainColumns(ColumnsSettings, CanEditInstallDestination)];
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
        // 行集合へは触れない。保持範囲は次の描画で、その時点の最新の位置に対して一回だけ更新する。
        MarkRetainedRowsStale("vertical_scroll");
        RequestRedraw("scroll_vertical");
        RaiseAutomationChanged(CustomTableAutomationChange.Scroll);
    }

    private void HorizontalScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CommitActiveEdit();
        CloseCellToolTip();
        RequestRedraw("scroll_horizontal");
        RaiseAutomationChanged(CustomTableAutomationChange.Scroll);
    }

    private void UpdateScrollBars()
    {
        int rowCount = RowCount;
        double extentWidth = GetColumnLayoutSnapshot().ExtentWidth;
        double width = Math.Max(0d, surface.ActualWidth);
        (bool hasVertical, bool hasHorizontal, int fullVisibleRows, double viewportWidth) = CalculateScrollBarLayout(width, extentWidth, rowCount, RowGeometry);
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
    /// <param name="width">全面の実幅（DIP）。</param>
    /// <param name="extentWidth">表示列の合計幅（DIP）。</param>
    /// <param name="rowCount">行数。</param>
    /// <param name="rows">物理画素の整数で持つ縦の寸法。完全表示行容量は画素の整数で求めます。</param>
    internal static (bool HasVertical, bool HasHorizontal, int FullVisibleRows, double ViewportWidth) CalculateScrollBarLayout(
        double width, double extentWidth, int rowCount, CustomTableRowGeometry rows)
    {
        bool hasVertical = false;
        bool hasHorizontal = false;
        // バーなしから必要な軸だけを追加する。前回のVisibilityへ依存させない。
        while (true)
        {
            int capacity = rows.FullVisibleRowCapacity(hasHorizontal);
            bool nextVertical = hasVertical || rowCount > capacity;
            bool nextHorizontal = hasHorizontal || extentWidth > Math.Max(0d, width - (nextVertical ? ScrollBarThickness : 0d)) + 0.5d;
            if (nextVertical == hasVertical && nextHorizontal == hasHorizontal)
            {
                break;
            }
            hasVertical = nextVertical;
            hasHorizontal = nextHorizontal;
        }
        int fullVisibleRows = rows.FullVisibleRowCapacity(hasHorizontal);
        double viewportWidth = Math.Max(0d, width - (hasVertical ? ScrollBarThickness : 0d));
        return (hasVertical, hasHorizontal, fullVisibleRows, viewportWidth);
    }

    /// <summary>横バーの手前まで完全表示できる行数を返します。極小領域でも既存の最小一行を保ちます。</summary>
    internal int CalculateFullVisibleRowCapacity()
    {
        return RowGeometry.FullVisibleRowCapacity(horizontalScrollBar.Visibility == Visibility.Visible);
    }

    /// <summary>バーの背後と部分表示を含む全面の描画行数を返し、可視行購読にも使います。</summary>
    internal int CalculateDrawableRowCapacity()
    {
        return RowGeometry.DrawableRowCapacity;
    }

    /// <summary>有効な本体高さを行高で切り捨て、完全表示容量を求めます。</summary>
    /// <remarks>
    /// 二つの長さは同じ単位で渡します。表は <see cref="CustomTableRowGeometry"/> から物理画素の整数を渡すため、商は丸め誤差を含みません。
    /// </remarks>
    internal static int CalculateFullVisibleRowCapacity(double bodyHeight, double rowHeight)
    {
        return Math.Max(1, (int)Math.Floor(Math.Max(0d, bodyHeight) / Math.Max(1d, rowHeight)));
    }

    /// <summary>全面の本体高さを行高で切り上げ、部分行を含む描画容量を求めます。</summary>
    /// <remarks>二つの長さは同じ単位で渡します。表は物理画素の整数を渡します。</remarks>
    internal static int CalculateDrawableRowCapacity(double bodyHeight, double rowHeight)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0d, bodyHeight) / Math.Max(1d, rowHeight)));
    }

    /// <summary>最終行を完全表示容量の末尾へ置く、行単位の最大開始位置を返します。</summary>
    internal static int CalculateVerticalScrollMaximum(int rowCount, int fullVisibleRows)
    {
        return Math.Max(0, Math.Max(0, rowCount) - Math.Max(1, fullVisibleRows));
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

    /// <summary>値と警告が共有する行エントリを取得します。</summary>
    internal CustomTableRowEntry GetRowEntry(object row) => rowCache.GetOrCreate(row);

    /// <summary>警告を他の表示値と同じ寿命で取得します。</summary>
    internal bool HasHighlightedWarning(object row) => row != null && GetRowEntry(row).HasHighlightedWarning;

    /// <summary>表示文化やパレットの変更時に、整形済みの値とブラシを更新します。</summary>
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
        RaiseAutomationChanged(CustomTableAutomationChange.Value);
    }

    /// <summary>描画を要求します。UI Automationの通知とは結び付けません。通知は変更の種類ごとに <see cref="RaiseAutomationChanged"/> で明示します。</summary>
    private void RequestRedraw(string reason)
    {
        pendingRedrawReason = NormalizeRedrawReason(reason);
        surface.InvalidateVisual();
    }

    /// <summary>選択の変更を描画へ反映し、UI Automationの選択の通知を予約します。</summary>
    private void RequestSelectionRedraw()
    {
        RequestRedraw("selection");
        RaiseAutomationChanged(CustomTableAutomationChange.Selection);
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

    /// <summary>
    /// 描画が使う行参照を返します。描画の直前に確定した保持範囲が持つ参照を位置で取得し、行集合の添字アクセスをしません。
    /// 一度も表示していない表と窓のない表は、行集合から読みます。非表示にして資源を解放した表は、行集合を読まず空のセルを描き、再表示で最新の状態から描きます。
    /// </summary>
    internal object GetRowForRender(int index)
    {
        IList rows = ItemsSource;
        if (rowChangeTracker.TryGetRow(rows, index, out object row))
        {
            return row;
        }
        // 非表示にして解放した表の描画（WPFの配置で描かれることがある）は、行集合を読まず空のセルにする。表示面は再表示で作り直す。
        return isRowRetentionReleased && !IsVisible ? null : rows[index];
    }

    /// <summary>保持範囲の更新の要求です。</summary>
    private enum RetainedRowsUpdate
    {
        /// <summary>更新を要求していません。</summary>
        None,
        /// <summary>範囲の位置だけが変わりました。重なる行は保ちます。</summary>
        Move,
        /// <summary>行集合の内容が変わった可能性があります。範囲の全位置を取り直します。</summary>
        Rebuild
    }

    /// <summary>
    /// 範囲の位置（スクロール・寸法・倍率）が変わったことを記録します。行集合へは触れず、保持範囲は次の描画で一回だけ更新します。
    /// </summary>
    private void MarkRetainedRowsStale(string reason)
    {
        if (pendingRetainedRowsUpdate == RetainedRowsUpdate.None)
        {
            pendingRetainedRowsUpdate = RetainedRowsUpdate.Move;
        }
        pendingRetainedRowsReason = reason;
    }

    /// <summary>
    /// 行集合の差替え・変更通知・再表示で、範囲の全位置の取り直しを次の描画へ予約します。
    /// 行がなくなった場合は読むものがないため、購読の解除と値の破棄を同期に行い、保留中の更新を破棄します。
    /// </summary>
    private void MarkRetainedRowsRebuild(string reason, bool logAlways = false)
    {
        if (RowCount == 0)
        {
            rowChangeTracker.DetachAllRows();
            pendingRetainedRowsUpdate = RetainedRowsUpdate.None;
            return;
        }
        pendingRetainedRowsUpdate = RetainedRowsUpdate.Rebuild;
        pendingRetainedRowsReason = reason;
        pendingRetainedRowsLogAlways |= logAlways;
    }

    /// <summary>
    /// 保留中の保持範囲の更新を、その時点の最新の位置に対して一回だけ行います。描画（<see cref="CustomTableSurface"/> の <c>OnRender</c>）の冒頭で呼び、
    /// 描画の時点で可視行（部分表示の行を含む）が保持範囲に入り購読済みであることを保証します。更新の要求がなければ何もしません。
    /// 非表示の表は購読を持たないため、保留を破棄して何も読みません。
    /// </summary>
    internal void CommitRetainedRows()
    {
        RetainedRowsUpdate update = pendingRetainedRowsUpdate;
        if (update == RetainedRowsUpdate.None)
        {
            return;
        }
        string reason = pendingRetainedRowsReason;
        bool logAlways = pendingRetainedRowsLogAlways;
        pendingRetainedRowsUpdate = RetainedRowsUpdate.None;
        pendingRetainedRowsLogAlways = false;
        if (!isRowRetentionActive && !IsVisible)
        {
            rowChangeTracker.DetachAllRows();
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        int firstIndex = 0;
        int requestedCount = 0;
        int rowCount = RowCount;
        if (rowCount > 0)
        {
            int firstVisibleIndex = FirstVisibleRowIndex;
            int drawableRows = CalculateDrawableRowCapacity();
            firstIndex = Math.Max(0, firstVisibleIndex - RowSubscriptionOverscan);
            int lastExclusive = Math.Min(rowCount, firstVisibleIndex + drawableRows + RowSubscriptionOverscan);
            requestedCount = Math.Max(0, lastExclusive - firstIndex);
            if (update == RetainedRowsUpdate.Rebuild)
            {
                rowChangeTracker.Rebuild(ItemsSource, firstIndex, requestedCount);
            }
            else
            {
                rowChangeTracker.Move(ItemsSource, firstIndex, requestedCount);
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
            RequestSelectionRedraw();
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
        RequestSelectionRedraw();
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
        RaiseAutomationChanged(CustomTableAutomationChange.Value);
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
        CustomTableRowGeometry rowGeometry = RowGeometry;
        double headerHeight = rowGeometry.HeaderHeight;
        if (surfacePoint.Y < headerHeight)
        {
            if (layout.TryResolveResizeColumn(surfacePoint.X, ColumnResizeHitTestMargin, out CustomTableColumnLayoutEntry resizeEntry, out Rect resizeRect))
            {
                return new CustomTableHitTestResult(CustomTableHitKind.HeaderResize, -1, null, resizeEntry.Column, resizeEntry.ColumnIndex, resizeRect);
            }
            bool hasHeaderColumn = layout.TryResolveColumn(tableX, out CustomTableColumnLayoutEntry headerEntry);
            Rect headerRect = hasHeaderColumn ? headerEntry.CreateVisibleSlotRect(horizontalOffset, surface.ActualWidth, 0d, headerHeight) : new Rect(0d, 0d, surface.ActualWidth, headerHeight);
            return new CustomTableHitTestResult(CustomTableHitKind.Header, -1, null, headerEntry.Column, hasHeaderColumn ? headerEntry.ColumnIndex : -1, headerRect);
        }
        bool hasColumn = layout.TryResolveColumn(tableX, out CustomTableColumnLayoutEntry cellEntry);
        if (!hasColumn)
        {
            return CreateEmptyHit();
        }
        int slot = rowGeometry.RowSlotAt(surfacePoint.Y);
        int rowIndex = FirstVisibleRowIndex + slot;
        IList rows = ItemsSource;
        if (rows == null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return CreateEmptyHit();
        }
        Rect cellRect = cellEntry.CreateVisibleSlotRect(horizontalOffset, surface.ActualWidth, rowGeometry.RowTop(slot), rowGeometry.RowHeight);
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
        e.Handled = HandleCellMouseDown(hit, MouseButton.Left, Keyboard.Modifiers, e.ClickCount, e.GetPosition(this));
    }

    /// <summary>実ヒットと押下情報から行選択とセル操作を処理し、選択通知・メニュー要求より前に現在行と操作列を揃えます。</summary>
    /// <remarks>見出し・スクロールバー・編集中の入力の判定は呼出元で済ませます。空白は選択と現在セルを保ちます。画面外の残行にも現在セルの行・列を保持し、編集の可視判定は編集受付で行います。</remarks>
    internal bool HandleCellMouseDown(CustomTableHitTestResult hit, MouseButton button, ModifierKeys modifiers, int clickCount, Point dragStartPoint)
    {
        if (hit.Kind != CustomTableHitKind.Cell)
        {
            if (button == MouseButton.Left) { ClearDragState(); }
            return false;
        }
        if (button == MouseButton.Right)
        {
            Focus();
            selectionModel.SetItemCount(RowCount);
            bool changed = TryGetRowGroupSpan(hit.Column, hit.RowIndex, out int groupFirst, out int groupLast)
                ? selectionModel.SelectSpanForRightClick(groupFirst, groupLast, hit.RowIndex)
                : selectionModel.SelectForRightClick(hit.RowIndex);
            currentCellHit = hit;
            UpdateSelectedIndexFromSelectionModel();
            if (changed)
            {
                RaiseSelectionChanged();
            }
            RequestSelectionRedraw();
            RowContextMenuRequested?.Invoke(this, new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true));
            return true;
        }
        if (button == MouseButton.Left)
        {
            bool plainSingleClick = clickCount == 1
                && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.None;
            // 結合列のクリックはグループの全行を選ぶため、マウスを離したときに一行へ絞らない。
            bool delaySingleSelectionUntilMouseUp = plainSingleClick
                && !IsMergedColumn(hit.Column)
                && selectionModel.SelectedCount > 1
                && selectionModel.IsSelected(hit.RowIndex);
            bool shouldBeginEditOnRepeatClick = clickCount == 1
                && IsSameEditableCell(currentCellHit, hit)
                && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.None;
            currentCellHit = hit;
            // 行グループの提供元を持つ表では、操作できるチェックボックス・ラジオボタンのダブルクリックを、
            // 二回目の押下も操作として扱い、行の起動（RowActivated）にしない。提供元のない表は、従来どおり二回目の押下で行を起動する。
            bool repeatableAction = RowGroupSource != null
                && hit.Column?.CellKind is CustomTableCellKind.CheckBox or CustomTableCellKind.RadioButton;
            bool actionClick = (clickCount == 1 || (clickCount >= 2 && repeatableAction))
                && IsActionCell(hit)
                && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.None;
            // 行グループの提供元を持つ表の操作セルは、ドラッグの起点にならないため、複数選択の中の行でも押した行だけを即座に選ぶ。
            // 結合列のクリックでグループの全行を選んだ後に、結合列でない操作セル（KEEP）を押したとき、全行の選択が残らないようにするためです。
            bool changed = ApplyMouseSelection(hit, modifiers, narrowToClickedRow: actionClick && RowGroupSource != null);
            if (changed)
            {
                RaiseSelectionChanged();
            }
            rowDragStartPoint = clickCount == 1 ? dragStartPoint : null;
            rowDragStartHit = clickCount == 1 ? hit : null;
            pendingSingleSelectionOnMouseUpRowIndex = delaySingleSelectionUntilMouseUp ? hit.RowIndex : -1;
            if (actionClick)
            {
                ClearDragState();
                CellActionRequested?.Invoke(this, new CustomTableCellActionRequestedEventArgs(hit));
                return true;
            }
            if (shouldBeginEditOnRepeatClick && BeginCellEdit(hit, null))
            {
                ClearDragState();
                return true;
            }
            if (clickCount >= 2)
            {
                ClearDragState();
                RowActivated?.Invoke(this, new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true));
            }
            return true;
        }
        return false;
    }

    /// <summary>行グループの提供元を持つ表で、グループの全行にまたがる列かを返します。</summary>
    private bool IsMergedColumn(CustomTableColumn column)
    {
        return column != null && column.MergesRowGroup && RowGroupSource != null;
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
            CustomTableCellKind.CheckBox or CustomTableCellKind.RadioButton => hit.Column.GetChecked(hit.Row).HasValue && hit.Column.GetEnabled(hit.Row),
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
        e.Handled = HandleCellMouseDown(hit, MouseButton.Right, Keyboard.Modifiers, e.ClickCount, default);
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
        // 未確定の編集は、刻み未満や縦バーなしの通知でも従来どおり確定する。
        CommitActiveEdit();
        if (verticalScrollBar.Visibility != Visibility.Visible)
        {
            wheelRemainder = 0;
            return;
        }
        if (e.Delta == 0)
        {
            return;
        }
        CloseCellToolTip();
        // OSの設定は入力のたびに読む。設定の変更を再起動なしで反映する。
        CustomTableWheelScroll.Result scrolled = CustomTableWheelScroll.Convert(
            e.Delta, wheelRemainder, SystemParameters.WheelScrollLines, CalculateFullVisibleRowCapacity(), verticalScrollBar.Value, verticalScrollBar.Maximum);
        wheelRemainder = scrolled.Remainder;
        if (!verticalScrollBar.Value.Equals(scrolled.Position))
        {
            verticalScrollBar.Value = scrolled.Position;
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

    private bool ApplyMouseSelection(CustomTableHitTestResult hit, ModifierKeys modifiers, bool narrowToClickedRow)
    {
        int rowIndex = hit.RowIndex;
        selectionModel.SetItemCount(RowCount);
        bool changed;
        if (TryGetRowGroupSpan(hit.Column, rowIndex, out int groupFirst, out int groupLast))
        {
            changed = ApplyMergedColumnMouseSelection(hit, modifiers, groupFirst, groupLast);
        }
        else
        {
            changed = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
                ? selectionModel.SelectRange(rowIndex)
                : (modifiers & ModifierKeys.Control) == ModifierKeys.Control
                    ? selectionModel.Toggle(rowIndex)
                    : narrowToClickedRow
                        ? selectionModel.SelectSingle(rowIndex)
                        : selectionModel.SelectForLeftMouseDownDragCandidate(rowIndex);
        }
        UpdateSelectedIndexFromSelectionModel();
        int currentRowIndex = selectionModel.CurrentIndex;
        if (currentRowIndex != rowIndex)
        {
            // Ctrl解除の残行は画面外でも論理的な現在セルにします。編集受付では従来どおり可視ヒットを作り直します。
            currentCellHit = currentRowIndex < 0 ? null : new CustomTableHitTestResult(
                CustomTableHitKind.Cell, currentRowIndex, ItemsSource[currentRowIndex], currentCellHit.Column, currentCellHit.ColumnIndex, Rect.Empty);
        }
        RequestSelectionRedraw();
        return changed;
    }

    /// <summary>
    /// 結合列の左クリックの選択を、行グループの全行を単位として決めます。修飾なしはグループの全行へ置き換え、Ctrlは全行が選択済みなら全行を外してそうでなければ全行を加え、
    /// Shiftは範囲の基点とクリックした行の両端を、それぞれのグループの境界まで広げます。
    /// 操作できるチェックボックス・ラジオボタンの修飾なしのクリックだけは右クリックと同じ規則で、全行が選択済みなら選択集合を保ちます。
    /// </summary>
    private bool ApplyMergedColumnMouseSelection(CustomTableHitTestResult hit, ModifierKeys modifiers, int groupFirst, int groupLast)
    {
        int rowIndex = hit.RowIndex;
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            int anchor = selectionModel.AnchorIndex >= 0 && selectionModel.AnchorIndex < RowCount ? selectionModel.AnchorIndex : rowIndex;
            return SelectGroupAlignedRange(Math.Min(anchor, rowIndex), Math.Max(anchor, rowIndex), rowIndex);
        }
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            return selectionModel.IsSpanSelected(groupFirst, groupLast)
                ? selectionModel.RemoveSpan(groupFirst, groupLast, rowIndex)
                : selectionModel.AddSpan(groupFirst, groupLast, rowIndex);
        }
        bool toggleKind = hit.Column.CellKind is CustomTableCellKind.CheckBox or CustomTableCellKind.RadioButton;
        return toggleKind && IsActionCell(hit)
            ? selectionModel.SelectSpanForRightClick(groupFirst, groupLast, rowIndex)
            : selectionModel.SelectSpan(groupFirst, groupLast, rowIndex, rowIndex);
    }

    /// <summary>
    /// 行番号 <paramref name="lowRow"/> から <paramref name="highRow"/> までの範囲を、両端がそれぞれの行のグループの境界になるよう広げて選び、
    /// 主行を <paramref name="currentRow"/> にします。範囲の基点は保ちます。
    /// </summary>
    private bool SelectGroupAlignedRange(int lowRow, int highRow, int currentRow)
    {
        TryGetRowGroupSpan(lowRow, out int start, out int lowGroupLast);
        int end = lowGroupLast;
        if (highRow > lowGroupLast)
        {
            TryGetRowGroupSpan(highRow, out _, out end);
        }
        return selectionModel.SelectSpanKeepingAnchor(start, end, currentRow);
    }

    /// <summary>
    /// 列が行グループの結合列なら、行が属するグループの両端を含む行番号の範囲を返します。提供元のない表と、結合列以外の列はfalseです。
    /// 提供元への問合せは一回だけで、グループの行を走査しません。
    /// </summary>
    private bool TryGetRowGroupSpan(CustomTableColumn column, int rowIndex, out int first, out int last)
    {
        if (!IsMergedColumn(column))
        {
            first = last = -1;
            return false;
        }
        return TryGetRowGroupSpan(rowIndex, out first, out last);
    }

    /// <summary>行が属するグループの、両端を含む行番号の範囲を返します。提供元のない表と範囲外の行はfalseです。表の行数の範囲へ収めます。</summary>
    private bool TryGetRowGroupSpan(int rowIndex, out int first, out int last)
    {
        first = last = -1;
        ICustomTableRowGroupSource groups = RowGroupSource;
        int rowCount = RowCount;
        if (groups == null || rowIndex < 0 || rowIndex >= rowCount)
        {
            return false;
        }
        CustomTableRowGroup group = groups.GetRowGroup(rowIndex);
        first = Math.Max(0, Math.Min(group.FirstRowIndex, rowIndex));
        last = Math.Min(rowCount - 1, Math.Max(group.EndRowIndex - 1, rowIndex));
        return true;
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
        RequestSelectionRedraw();
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
        RequestSelectionRedraw();
        return RowCount > 0;
    }

    private bool MoveKeyboardSelection(int delta, bool extendRange)
    {
        selectionModel.SetItemCount(RowCount);
        bool changed = IsCurrentCellInMergedColumn()
            ? MoveGroupSelection(delta, extendRange)
            : extendRange ? selectionModel.ExtendRangeBy(delta) : selectionModel.MoveCurrent(delta);
        UpdateSelectedIndexFromSelectionModel();
        EnsureRowVisible(selectionModel.CurrentIndex);
        EnsureCurrentCellForCurrentRow();
        if (changed)
        {
            RaiseSelectionChanged();
        }
        RequestSelectionRedraw();
        return RowCount > 0;
    }

    /// <summary>現在セルが、行グループの結合列にあるかを返します。</summary>
    private bool IsCurrentCellInMergedColumn()
    {
        return currentCellHit?.Kind == CustomTableHitKind.Cell
            && IsMergedColumn(currentCellHit.Column)
            && selectionModel.CurrentIndex >= 0;
    }

    /// <summary>
    /// 現在セルが結合列にあるときの上下の移動を、行グループを単位として行います。主行は移動先のグループの先頭行で、
    /// Shift付きは範囲の基点と移動先を、それぞれのグループの境界まで広げます。先頭のグループの上と末尾のグループの下では動きません。
    /// </summary>
    private bool MoveGroupSelection(int delta, bool extendRange)
    {
        if (!TryGetRowGroupSpan(selectionModel.CurrentIndex, out int currentFirst, out int currentLast))
        {
            return false;
        }
        int targetRow = delta > 0 ? currentLast + 1 : currentFirst - 1;
        if (targetRow < 0 || targetRow >= RowCount || !TryGetRowGroupSpan(targetRow, out int targetFirst, out int targetLast))
        {
            return false;
        }
        if (!extendRange)
        {
            return selectionModel.SelectSpan(targetFirst, targetLast, targetFirst, targetFirst);
        }
        int anchor = selectionModel.AnchorIndex >= 0 && selectionModel.AnchorIndex < RowCount ? selectionModel.AnchorIndex : currentFirst;
        return SelectGroupAlignedRange(Math.Min(anchor, targetFirst), Math.Max(anchor, targetFirst), targetFirst);
    }

    private bool CopySelectedRowsToClipboard()
    {
        if (!CustomTableDataTransfer.TryBuildTsv(GetSelectedRowsSnapshot(), VisibleColumns, out string text))
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
            IUiDialogService dialogs = dialogService
                ?? throw new InvalidOperationException("Custom table dialog service is not configured.");
            UiDialogResult notification = await dialogs.ShowMessageAsync(new UiMessageRequest(
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
        if (TryGetRowGroupSpan(target.Column, rowIndex, out int groupFirst, out int groupLast))
        {
            // 結合列へ入ったときは、右クリックと同じ規則でそのグループの全行を選ぶ。結合列から出るときは選択を保つ。
            selectionModel.SetItemCount(RowCount);
            if (selectionModel.SelectSpanForRightClick(groupFirst, groupLast, rowIndex))
            {
                UpdateSelectedIndexFromSelectionModel();
                RaiseSelectionChanged();
            }
            RequestSelectionRedraw();
            return true;
        }
        RequestRedraw("current_cell");
        return true;
    }

    /// <summary>最新主行へ現存する同列を引き継ぎ、列消失時は先頭表示列へ移します。</summary>
    /// <param name="fallbackColumnId">旧編集の同期整列がhitを破棄した場合だけ使う、同じ行集合で捕捉した列ID。再入が作ったhitを優先します。</param>
    private void EnsureCurrentCellForCurrentRow(string fallbackColumnId = null)
    {
        int rowIndex = selectionModel.CurrentIndex;
        if (rowIndex < 0)
        {
            currentCellHit = null;
            return;
        }
        IReadOnlyList<CustomTableColumnLayoutEntry> entries = GetColumnLayoutSnapshot().Entries;
        string columnId = currentCellHit == null ? fallbackColumnId : currentCellHit.Column?.Id;
        CustomTableColumnLayoutEntry? first = null;
        CustomTableColumnLayoutEntry? target = null;
        foreach (CustomTableColumnLayoutEntry entry in entries)
        {
            if (entry.Column?.IsVisible != true)
            {
                continue;
            }
            first ??= entry;
            if (string.Equals(entry.Column.Id, columnId, StringComparison.Ordinal))
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
        // 並べ替えの列と方向は購読側のBindingの更新でだけ変える。要求が拒否された場合に矢印だけが先に変わらないようにする。
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
        SelectionChanged?.Invoke(this, new CustomTableSelectionChangedEventArgs(selectionModel.CurrentIndex, selectedRow, selectionModel.SelectedCount));
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

    private string activeEditOriginalText;

    private bool BeginCellEdit(CustomTableHitTestResult hit, string replacementText)
    {
        if (hit?.Kind != CustomTableHitKind.Cell || string.IsNullOrWhiteSpace(hit.Column?.EditPropertyName))
        {
            return false;
        }
        // 行グループの結合列は、行をまたぐ一つのセルとして描くため、行ごとの編集欄を重ねない。
        if (IsMergedColumn(hit.Column))
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
        string originalText = hit.Column.GetEditText(hit.Row) ?? string.Empty;
        TextBox textBox = CreateCellEditor(hit, replacementText ?? originalText);
        activeEditOriginalText = originalText;
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
        RaiseAutomationChanged(CustomTableAutomationChange.Structure);
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
        // ウィンドウは描画方式を指定しないため、編集欄にはセルの文字と同じ方式を明示する。
        TextOptions.SetTextFormattingMode(textBox, textStyle.TextFormattingMode);
        System.Windows.Automation.AutomationProperties.SetAutomationId(textBox, "CellEditor");
        System.Windows.Automation.AutomationProperties.SetName(textBox, hit.Column.Header ?? string.Empty);
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
            string originalText = activeEditOriginalText;
            activeEditOriginalText = null;
            CloseEditSuggestions();
            editor.LostKeyboardFocus -= ActiveEditorLostKeyboardFocus;
            editor.TextChanged -= ActiveEditorTextChanged;
            editorLayer.Children.Remove(editor);
            activeEditor = null;
            activeEditHit = null;
            if (hit != null)
            {
                rowCache.InvalidateRow(hit.Row);
                CellEditEnded?.Invoke(this, new CustomTableCellEditEndedEventArgs(hit, hit.Column?.EditPropertyName, text, commit, originalText));
            }
            // 編集欄が子から外れる。値の変更は行の変更通知で別に届く。
            RaiseAutomationChanged(CustomTableAutomationChange.Structure);
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
        CustomTableRowGeometry rowGeometry = RowGeometry;
        double rowY = rowGeometry.RowTop(rowIndex - FirstVisibleRowIndex);
        double visibleHeight = Math.Min(rowGeometry.RowHeight, Math.Max(0d, surface.ActualHeight - rowY));
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
        CustomTableRowGeometry rowGeometry = RowGeometry;
        double rowY = rowGeometry.RowTop(rowIndex - FirstVisibleRowIndex);
        double visibleHeight = Math.Min(rowGeometry.RowHeight, Math.Max(0d, surface.ActualHeight - rowY));
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

    /// <summary>実見出し境界のヒットから幅変更を開始し、旧セルの入力を一回確定してからドラッグ座標を捕捉します。</summary>
    /// <param name="hit">幅変更可能な見出し境界の実ヒット。</param>
    /// <param name="surfaceX">表の描画面を基準とする入力開始位置。</param>
    internal void BeginColumnResize(CustomTableHitTestResult hit, double surfaceX)
    {
        if (hit?.Column == null || hit.Column.Layout == null || !hit.Column.CanResize)
        {
            return;
        }
        CommitActiveEdit();
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

    /// <summary>開始位置からの実入力座標で列幅を更新します。同期の設定通知と直接列の経路を一つの局所失効へ揃えます。</summary>
    /// <param name="surfaceX">表の描画面を基準とする現在の入力位置。</param>
    internal void UpdateColumnResize(double surfaceX)
    {
        if (!isResizingColumn || resizingColumnLayout == null)
        {
            return;
        }
        double delta = surfaceX - resizingStartMouseX;
        int width = Math.Max(resizingColumnMinWidth, Math.Min(resizingColumnMaxWidth, (int)Math.Round(resizingStartWidth + delta)));
        if (resizingColumnLayout.Width != width)
        {
            ICustomTableColumnLayout layout = resizingColumnLayout;
            layout.Width = width;
            if (layout is not INotifyPropertyChanged notifying || !subscribedColumnLayouts.Contains(notifying))
            {
                ApplyColumnWidthChange(layout);
            }
        }
    }

    /// <summary>幅変更の入力捕捉とカーソルを解除し、確定した現在幅で再描画します。</summary>
    internal void EndColumnResize()
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
        RefreshAutomationChildren();
        RequestRedraw("column_resize");
    }

    /// <summary>セルのツールチップです。開閉の回数と内容を、窓の有無によらず観測するために公開します。</summary>
    internal ToolTip CellToolTip => cellToolTip;

    /// <summary>マウスの下のセルに合わせて、セルのツールチップを開く・保つ・出し直す・閉じるを決めます。マウスの移動が使う入口です。</summary>
    /// <param name="hit">マウスの下の実ヒット。</param>
    internal void UpdateCellToolTip(CustomTableHitTestResult hit)
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
        // 結合列のセルはグループの範囲全体で一つの当たり判定とし、同じグループの別の行へ動いても出し直さない。
        int toolTipCellKey = TryGetRowGroupSpan(hit.Column, hit.RowIndex, out int groupFirst, out _) ? groupFirst : hit.RowIndex;
        bool movedToAnotherCell = toolTipRowIndex != toolTipCellKey || !string.Equals(toolTipColumnId, hit.Column.Id, StringComparison.Ordinal);
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
        toolTipRowIndex = toolTipCellKey;
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

    /// <summary>実セルの明示tooltipを優先し、通常テキストは現在幅で省略される場合だけ元文字列を返します。</summary>
    /// <param name="hit">表示内容を調べる現存セルの実ヒット。</param>
    /// <returns>表示する補助文字列。必要がなければnull。</returns>
    internal string ResolveCellToolTip(CustomTableHitTestResult hit)
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
        IReadOnlyList<CustomTableColumnLayoutEntry> entries = GetColumnLayoutSnapshot().Entries;
        if (hit.ColumnIndex < 0 || hit.ColumnIndex >= entries.Count)
        {
            return null;
        }
        // 描画と同じ基準（セルの内側の幅から文字余白を引いた幅と、レイアウトの自然な幅）で省略を判定する。
        bool wouldTrim = surface.WouldTrim(cellValue.Text, ResolveTextStyle(cellValue.TextStyle), entries[hit.ColumnIndex]);
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

    /// <summary>
    /// 描画で通知した計数です。テストが描画一回分の結果を読むための観測点です。
    /// 可視行・可視列の数と文字のレイアウトのヒット数・ミス数は直近一回の描画の値で、描画回数（<see cref="CustomTableRenderStats.RenderCount"/>）だけは表を作ってからの累計です。
    /// </summary>
    internal CustomTableRenderStats LastRenderStats { get; private set; }

    /// <summary>表示面が現在WPFへ渡している画像です。解放後はnullです。</summary>
    internal ImageSource SurfaceImage => surface.Image;

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
        LastRenderStats = new CustomTableRenderStats(LastRenderStats.RenderCount + 1, visibleRowCount, visibleColumnCount, textCacheHits, textCacheMisses);
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

/// <summary>直近一回の描画で通知した計数です。</summary>
/// <param name="RenderCount">通知した描画の累計回数。</param>
/// <param name="VisibleRowCount">描いた行数。</param>
/// <param name="VisibleColumnCount">可視列数。</param>
/// <param name="TextCacheHits">文字のレイアウトを再利用した回数。</param>
/// <param name="TextCacheMisses">文字のレイアウトを新しく作った回数。</param>
internal readonly record struct CustomTableRenderStats(int RenderCount, int VisibleRowCount, int VisibleColumnCount, int TextCacheHits, int TextCacheMisses);

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
}
