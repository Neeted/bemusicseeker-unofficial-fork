using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Diagnostics;
using D2D = Vortice.Direct2D1;
using RawRectF = Vortice.RawRectF;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一覧の表示面です。背景・見出し・行・挿入線の全面を、物理画素の寸法の描画先へDirect2D/DirectWriteで描き、
/// WPFへ一枚の画像として置きます。描画先と画像は表示経路（<see cref="CustomTableDisplaySurface"/>）が持ち、GPU合成かCPU側のビットマップかを選びます。
/// 描画は <see cref="OnRender"/> の中で同期的に完結し、描画の内容は経路によらず同じ描画先への呼出しです。
/// 行やセルの描画結果は保持せず、保持するのは文字のレイアウトだけです。
/// </summary>
internal sealed partial class CustomTableSurface : FrameworkElement
{
    private readonly CustomTableView owner;
    private readonly CustomTableTextLayoutCache textLayoutCache = new();
    private CustomTableDisplaySurface display;
    private CustomTablePalette palette;
    private CultureInfo culture;
    private CultureInfo uiCulture;
    private bool measureRenderDetails;

    // 描画一回の間だけ有効な、物理画素の寸法です。
    private D2D.ID2D1RenderTarget target;
    private double pixelsPerDip;
    private int widthPx;
    private int heightPx;
    private int gridLinePx;
    private int rowHeightPx;
    private int headerHeightPx;
    private int drawableRowCapacity;

    private int renderTextCacheHits;
    private int renderTextCacheMisses;
    private long renderRowHighlightTicks;
    private long renderCellValueTicks;
    private long renderTextLayoutTicks;
    private long renderDrawTextTicks;

    /// <summary>表の全体の背景・見出し・罫線・行のセルを描画します。</summary>
    internal CustomTableSurface(CustomTableView owner)
    {
        this.owner = owner;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        // 画像は物理画素と一対一で置くため、拡大縮小の補間で滲ませない。
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>表示面が現在WPFへ渡している画像です。解放後と、描く前はnullです。</summary>
    internal ImageSource Image => display?.Image;

    /// <summary>
    /// 表示経路の失敗を再現する、この表示面だけの故障注入の設定です。既定はnullで、テストだけが設定します。
    /// 設定の内容は <see cref="CustomTableDisplayFaults"/> が定めます。
    /// </summary>
    internal CustomTableDisplayFaults DisplayFaults { get; set; }

    /// <summary>表示倍率です。</summary>
    internal double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>保持している文字のレイアウトを破棄します。次の描画で、必要な分だけ作り直します。</summary>
    internal void ClearTextLayoutCache()
    {
        textLayoutCache.Clear();
    }

    /// <summary>
    /// 表示経路の面（描画先、画像、ブラシ）と文字のレイアウトをすべて解放します。プロセスで共有するGPUのデバイスは解放しません。再び描くときは最新の状態から作り直し、
    /// CPU経路へ切り替えていた表も、改めてGPU経路を試します。非表示・読み込み解除・<c>ItemsSource</c> のnullで呼ばれる、解放の唯一の経路です。
    /// </summary>
    internal void ReleaseDrawingResources()
    {
        display?.Dispose();
        display = null;
        target = null;
        textLayoutCache.Clear();
    }

    /// <summary>倍率が変わると行と見出しの高さの実効値が変わるため、スクロール範囲と可視行を更新して描き直します。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearTextLayoutCache();
        owner.HandleDpiChanged();
        InvalidateVisual();
    }

    /// <summary>
    /// 描画とツールチップで共通の基準で、セルの文字が幅に収まらず省略されるかを返します。
    /// セルの内側の幅から左右の文字余白を引いた幅と、レイアウトの自然な幅を比べます。描画のヒット数・ミス数には数えません。
    /// </summary>
    /// <param name="text">セルの文字列。</param>
    /// <param name="textStyle">セルの文字の書体。</param>
    /// <param name="entry">セルの列の配置。</param>
    internal bool WouldTrim(string text, CustomTableTextStyle textStyle, CustomTableColumnLayoutEntry entry)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        double ppd = PixelsPerDip;
        ConfigureTextEnvironment(ppd);
        int available = CustomTablePixelGeometry.AvailableTextWidth(
            CustomTablePixelGeometry.ToPixels(entry.ScreenX, ppd),
            CustomTablePixelGeometry.ToPixels(entry.ScreenX + entry.Width, ppd),
            ppd);
        CustomTableTextLayout layout = textLayoutCache.GetOrCreate(text, (textStyle ?? CustomTableTextStyle.Normal).Kind, TextAlignment.Left, 0, out _);
        return CustomTableTextLayoutCache.ExceedsAvailableWidth(layout.NaturalWidth, available);
    }

    private void ConfigureTextEnvironment(double ppd)
    {
        textLayoutCache.Configure(new CustomTableTextEnvironment(
            owner.TextFontFamily,
            owner.ScoreFontFamily,
            owner.TextFontSize,
            ppd,
            CultureInfo.CurrentCulture,
            CultureInfo.CurrentUICulture));
    }

    private void UpdateDrawingEnvironment(double ppd)
    {
        CustomTablePalette nextPalette = CustomTablePalette.Current;
        CultureInfo nextCulture = CultureInfo.CurrentCulture;
        CultureInfo nextUICulture = CultureInfo.CurrentUICulture;
        // 同名でも数値書式を変更したCultureInfoへ交換できるため、参照の変更も確認する。
        bool valuesChanged = palette != null
            && (!ReferenceEquals(culture, nextCulture)
                || !ReferenceEquals(uiCulture, nextUICulture)
                || !ReferenceEquals(palette, nextPalette));
        if (valuesChanged)
        {
            owner.InvalidateDisplayValues();
        }
        palette = nextPalette;
        culture = nextCulture;
        uiCulture = nextUICulture;
        ConfigureTextEnvironment(ppd);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // 保持範囲は描画の単位で、その時点の最新の位置に対して一回だけ更新する。
        owner.CommitRetainedRows();
        var renderStopwatch = Stopwatch.StartNew();
        measureRenderDetails = Net10PerformanceLog.IsEnabled;
        renderTextCacheHits = 0;
        renderTextCacheMisses = 0;
        renderRowHighlightTicks = 0L;
        renderCellValueTicks = 0L;
        renderTextLayoutTicks = 0L;
        renderDrawTextTicks = 0L;
        double ppd = PixelsPerDip;
        // 行と見出しの高さと表示面の高さは、当たり判定やスクロール範囲と同じ物理画素の整数を使う。
        CustomTableRowGeometry rowGeometry = owner.RowGeometry;
        int width = CustomTablePixelGeometry.ToPixels(ActualWidth, ppd);
        int height = rowGeometry.SurfaceHeightPx;
        if (ActualWidth <= 0d || ActualHeight <= 0d || width <= 0 || height <= 0)
        {
            owner.NotifySurfaceRendered(0, 0, 0L, -1d, owner.ConsumeRedrawReason());
            return;
        }
        string redrawReason = owner.ConsumeRedrawReason();
        CustomTableColumnLayoutSnapshot layout = owner.GetColumnLayoutSnapshot();
        UpdateDrawingEnvironment(ppd);
        // 保持範囲（可視行と上下の余白の行）の可視列のセルと見出しは、幅なしと幅つきの二つを持ち得る。
        textLayoutCache.EnsureCapacity(2 * (rowGeometry.DrawableRowCapacity + 2 * CustomTableView.RowSubscriptionOverscan + 1) * Math.Max(1, layout.VisibleColumnCount));
        display ??= new CustomTableDisplaySurface(() => DisplayFaults, InvalidateVisual);
        display.BeginRequest();
        pixelsPerDip = ppd;
        widthPx = width;
        heightPx = height;
        gridLinePx = CustomTablePixelGeometry.GridLineWidth(ppd);
        rowHeightPx = rowGeometry.RowHeightPx;
        headerHeightPx = rowGeometry.HeaderHeightPx;
        drawableRowCapacity = rowGeometry.DrawableRowCapacity;
        int visibleRowCount = 0;
        bool swapPending = owner.IsItemsSourceSwapPending;
        // 描画先の作り直しやデバイスの消失からの復旧で、同じ描画要求の中で一回だけ描き直す。文字のレイアウトと行の表示値は復旧で失わない。
        while (!TryDrawFrame(layout, width, height, ppd, swapPending, out visibleRowCount))
        {
        }
        drawingContext.DrawImage(display.Image, new Rect(new Point(0d, 0d), display.ImageSize));
        renderStopwatch.Stop();
        if (swapPending)
        {
            owner.NotifySurfaceRendered(
                visibleRowCount: 0,
                visibleColumnCount: layout.VisibleColumnCount,
                renderWorkMs: renderStopwatch.ElapsedMilliseconds,
                textCacheHitRate: -1d,
                redrawReason: "pending_items_source_swap_skipped");
            return;
        }
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

    /// <summary>
    /// 描画の試行を一回行います。表示経路を用意して全面を描き、終端が成功すれば画素をWPFへ反映してtrueを返します。
    /// 描画先の作り直しの要求かデバイスの消失で終端が失敗し、表示経路が復旧の準備を終えたときはfalseを返し、呼び出し側が描き直します。
    /// それ以外の失敗は、資源を解放して例外を再送出します。
    /// </summary>
    private bool TryDrawFrame(CustomTableColumnLayoutSnapshot layout, int width, int height, double ppd, bool swapPending, out int visibleRowCount)
    {
        visibleRowCount = 0;
        display.Prepare(width, height, ppd, this);
        display.TrimBrushes();
        target = display.Target;
        bool completed = false;
        SharpGen.Runtime.Result endResult;
        target.BeginDraw();
        try
        {
            DrawBackground();
            DrawHeader(layout);
            if (!swapPending)
            {
                visibleRowCount = DrawRows(layout);
                DrawRowDropInsertPreview();
            }
            completed = true;
        }
        finally
        {
            endResult = display.EndDraw();
            if (!completed)
            {
                // 描画の途中で失敗した資源は再利用せず、次の描画で作り直す。
                ReleaseDrawingResources();
            }
        }
        if (endResult.Failure)
        {
            if (display.TryRecover(endResult))
            {
                target = null;
                return false;
            }
            // 復旧できない終端の失敗は、資源を再利用せず、次の描画で作り直す。
            ReleaseDrawingResources();
            endResult.CheckError();
        }
        return display.TryPresent();
    }

    private static long StopwatchTicksToMilliseconds(long ticks)
    {
        return ticks <= 0L ? 0L : (ticks * 1000L) / Stopwatch.Frequency;
    }

    private void FillRect(float left, float top, float right, float bottom, Brush brush)
    {
        D2D.ID2D1SolidColorBrush solid = display.GetBrush(brush);
        FillRect(left, top, right, bottom, solid);
    }

    private void FillRect(float left, float top, float right, float bottom, D2D.ID2D1SolidColorBrush brush)
    {
        if (brush == null || right <= left || bottom <= top)
        {
            return;
        }
        target.FillRectangle(new RawRectF(left, top, right, bottom), brush);
    }

    private void FillRect(float left, float top, float right, float bottom, Pen pen)
    {
        FillRect(left, top, right, bottom, pen?.Brush);
    }

    private void DrawBackground()
    {
        FillRect(0f, 0f, widthPx, heightPx, palette.RowBackground);
    }

    private (int Left, int Right) GetSlotPixels(CustomTableColumnLayoutEntry entry)
    {
        return (CustomTablePixelGeometry.ToPixels(entry.ScreenX, pixelsPerDip),
            CustomTablePixelGeometry.ToPixels(entry.ScreenX + entry.Width, pixelsPerDip));
    }

    private void DrawHeader(CustomTableColumnLayoutSnapshot layout)
    {
        FillRect(0f, 0f, widthPx, headerHeightPx, palette.HeaderBackground);
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column == null)
            {
                continue;
            }
            (int left, int right) = GetSlotPixels(entry);
            int interiorRight = right - gridLinePx;
            int interiorBottom = headerHeightPx - gridLinePx;
            if (owner.IsReorderSourceColumn(column))
            {
                FillRect(Math.Max(0, left), 0f, Math.Min(widthPx, interiorRight), interiorBottom, palette.ReorderSourceHeader);
            }
            DrawCellText(column.Header, new PixelRect(left, 0, interiorRight, interiorBottom), left, right, palette.DefaultForeground, [], TextAlignment.Center, CustomTableTextKind.Normal, 0d);
            DrawSortGlyph(column, new PixelRect(left, 0, interiorRight, interiorBottom));
            if (right <= widthPx && interiorRight >= 0)
            {
                FillRect(interiorRight, 0f, right, headerHeightPx, palette.HeaderBorder);
            }
        }
        // ツールバーの段数や列の有無によらず、ヘッダー自身が全幅の上枠を所有する。
        if (gridLinePx <= heightPx)
        {
            FillRect(0f, 0f, widthPx, gridLinePx, palette.HeaderBorder);
        }
        if (headerHeightPx <= heightPx)
        {
            FillRect(0f, headerHeightPx - gridLinePx, widthPx, headerHeightPx, palette.HeaderBorder);
        }
        if (owner.IsColumnReorderPreviewActive)
        {
            double insertX = Math.Max(0d, Math.Min(ActualWidth, owner.ColumnReorderPreviewInsertX));
            int thickness = Math.Max(1, CustomTablePixelGeometry.ToPixels(palette.ColumnReorderInsert.Thickness, pixelsPerDip));
            int center = CustomTablePixelGeometry.ToPixels(insertX, pixelsPerDip);
            FillRect(center - thickness / 2, 0f, center - thickness / 2 + thickness, headerHeightPx, palette.ColumnReorderInsert);
        }
    }

    private int DrawRows(CustomTableColumnLayoutSnapshot layout)
    {
        IList rows = owner.ItemsSource;
        int rowCount = rows?.Count ?? 0;
        if (rowCount == 0)
        {
            return 0;
        }
        int y = headerHeightPx;
        int firstRowIndex = owner.FirstVisibleRowIndex;
        int lastRowExclusive = Math.Min(rowCount, firstRowIndex + drawableRowCapacity);
        int drawnRows = 0;
        // 行グループの提供元を持つ表だけが使う、描画中のグループの状態です。問合せは、表示範囲に現れるグループごとに一回だけです。
        ICustomTableRowGroupSource groups = owner.RowGroupSource;
        int groupFirst = 0;
        int groupEnd = -1;
        int groupOrdinal = 0;
        int segmentTop = y;
        object segmentFirstRow = null;
        bool groupFullySelected = false;
        CustomTableColumn groupCurrentColumn = null;
        for (int rowIndex = firstRowIndex; rowIndex < lastRowExclusive && y < heightPx; rowIndex++)
        {
            object row = owner.GetRowForRender(rowIndex);
            if (groups != null && rowIndex >= groupEnd)
            {
                CustomTableRowGroup group = groups.GetRowGroup(rowIndex);
                groupFirst = Math.Min(group.FirstRowIndex, rowIndex);
                groupEnd = Math.Max(group.EndRowIndex, rowIndex + 1);
                groupOrdinal = group.Ordinal;
                // 結合セルの選択と現在セルの強調は、画面外の行を含むグループ全体で決める。区間として一度だけ調べ、行を一件ずつ読まない。
                groupFullySelected = owner.IsRowSpanSelected(groupFirst, groupEnd);
                groupCurrentColumn = owner.GetCurrentCellColumnInRange(groupFirst, groupEnd);
                // 結合列の値は、表示中の範囲にあるグループの最初の行から読む。先頭行が画面外でも、先頭行を別に読み込まない。
                segmentFirstRow = row;
                segmentTop = y;
            }
            CustomTableRowEntry rowEntry = owner.GetRowEntry(row);
            bool selected = owner.IsRowSelected(rowIndex);
            CustomTableColumn currentColumn = owner.GetCurrentCellColumn(rowIndex);
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
                    : ((groups == null ? rowIndex : groupOrdinal) & 1) == 1
                        ? palette.AlternatingRowBackground
                        : palette.RowBackground;
            FillRect(0f, y, widthPx, Math.Min(y + rowHeightPx, heightPx), rowBackground);
            DrawRowCells(layout, rowEntry, selected, currentColumn, y, groups != null);
            int rowBottom = y + rowHeightPx;
            bool lastInGroup = groups == null || rowIndex + 1 >= groupEnd;
            // 横罫線は完全に見えるときだけ描く。部分表示の行は、罫線を描かず中身だけを見せる。
            // グループの内部の境界は、結合列には描かず、グループの下端の罫線だけを全幅に描く。
            if (rowBottom <= heightPx)
            {
                FillRowBottomBorder(layout, rowBottom - gridLinePx, rowBottom, skipMergedColumns: !lastInGroup);
            }
            if (groups != null && (lastInGroup || rowIndex + 1 >= lastRowExclusive || rowBottom >= heightPx))
            {
                // 表示中の部分の最後の行まで背景を塗り終えてから、結合列の内容を一度だけ描く。
                Brush groupBackground = (groupOrdinal & 1) == 1 ? palette.AlternatingRowBackground : palette.RowBackground;
                DrawMergedCells(layout, segmentFirstRow, segmentTop, rowBottom, lastInGroup && rowBottom <= heightPx, groupBackground, groupFullySelected, groupCurrentColumn);
            }
            y += rowHeightPx;
            drawnRows++;
        }
        return drawnRows;
    }

    private void DrawRowDropInsertPreview()
    {
        if (!owner.IsRowDropInsertPreviewActive || widthPx <= 0 || heightPx <= headerHeightPx)
        {
            return;
        }
        IList rows = owner.ItemsSource;
        int rowCount = rows?.Count ?? 0;
        int insertIndex = Math.Max(0, Math.Min(rowCount, owner.RowDropPreviewInsertIndex));
        int firstRowIndex = owner.FirstVisibleRowIndex;
        int lastVisibleExclusive = Math.Min(rowCount, firstRowIndex + drawableRowCapacity);
        int y;
        if (insertIndex <= firstRowIndex)
        {
            y = headerHeightPx;
        }
        else if (insertIndex >= lastVisibleExclusive)
        {
            y = headerHeightPx + Math.Max(0, lastVisibleExclusive - firstRowIndex) * rowHeightPx;
        }
        else
        {
            y = headerHeightPx + (insertIndex - firstRowIndex) * rowHeightPx;
        }
        y = Math.Max(headerHeightPx, Math.Min(heightPx, y));
        int thickness = Math.Max(1, CustomTablePixelGeometry.ToPixels(palette.ColumnReorderInsert.Thickness, pixelsPerDip));
        FillRect(0f, y - thickness / 2, widthPx, y - thickness / 2 + thickness, palette.ColumnReorderInsert);
    }

    /// <summary>
    /// 行の下端の横罫線を描きます。<paramref name="skipMergedColumns"/> がtrueなら、行グループの結合列の幅には描かず、グループの内部の境界にします。
    /// 結合列の右の縦罫線は、各行が描くため途切れません。
    /// </summary>
    private void FillRowBottomBorder(CustomTableColumnLayoutSnapshot layout, int top, int bottom, bool skipMergedColumns)
    {
        int x = 0;
        if (skipMergedColumns)
        {
            foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
            {
                if (entry.Column == null || !entry.Column.MergesRowGroup)
                {
                    continue;
                }
                (int left, int right) = GetSlotPixels(entry);
                int skipLeft = Math.Max(x, left);
                int skipRight = Math.Min(widthPx, right - gridLinePx);
                if (skipRight > skipLeft)
                {
                    FillRect(x, top, skipLeft, bottom, palette.CellBorder);
                    x = skipRight;
                }
            }
        }
        FillRect(x, top, widthPx, bottom, palette.CellBorder);
    }

    /// <summary>
    /// グループの全行にまたがる結合列を、グループの範囲のうち表示中の部分へ、背景と内容を一度だけ描きます。
    /// 背景は、現在セルが結合列にあれば結合セル全体の現在セルの強調、グループの全行が選択済みなら結合セル全体の選択色、それ以外はグループの交互色です。
    /// 行ごとの選択色は結合列に残さないため、一部だけが選択されたグループの結合セルは交互色のままです。
    /// 値は表示中の部分の最初の行から読み、グループの下端が見えるときは、下端の罫線を除いた範囲の縦中央に内容を描きます。
    /// </summary>
    private void DrawMergedCells(
        CustomTableColumnLayoutSnapshot layout,
        object firstRow,
        int top,
        int segmentBottom,
        bool bottomBorderDrawn,
        Brush groupBackground,
        bool groupFullySelected,
        CustomTableColumn currentColumn)
    {
        int interiorBottom = bottomBorderDrawn ? segmentBottom - gridLinePx : Math.Min(segmentBottom, heightPx);
        if (interiorBottom <= top)
        {
            return;
        }
        CustomTableRowEntry firstEntry = null;
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column == null || !column.MergesRowGroup)
            {
                continue;
            }
            (int left, int right) = GetSlotPixels(entry);
            bool currentCell = currentColumn != null && string.Equals(currentColumn.Id, column.Id, StringComparison.Ordinal);
            Brush fill = currentCell
                ? palette.CurrentCellBackground
                : groupFullySelected
                    ? palette.SelectedRowBackground
                    : groupBackground;
            FillRect(Math.Max(0, left), top, Math.Min(widthPx, right - gridLinePx), interiorBottom, fill);
            firstEntry ??= owner.GetRowEntry(firstRow);
            long cellValueStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
            CustomTableCellValue cellValue = firstEntry.GetValue(column, out _);
            if (measureRenderDetails)
            {
                renderCellValueTicks += Stopwatch.GetTimestamp() - cellValueStart;
            }
            // 結合セルの文字は、塗りの上で読めるよう、結合セルを塗る状態の文字色にする。色付きの区間は自身の色を使う。
            Brush foreground = cellValue.TextRuns.Count > 0
                ? cellValue.Foreground
                : currentCell
                    ? palette.CurrentCellForeground
                    : groupFullySelected
                        ? palette.SelectedForeground
                        : cellValue.Foreground;
            DrawCellContent(cellValue, new PixelRect(left, top, right - gridLinePx, interiorBottom), left, right, foreground);
        }
    }

    /// <summary>セルの種類ごとの内容（文字、アイコン、チェックボックス、ラジオボタン）を描きます。</summary>
    private void DrawCellContent(CustomTableCellValue cellValue, PixelRect interior, int slotLeft, int slotRight, Brush foreground)
    {
        switch (cellValue.CellKind)
        {
            case CustomTableCellKind.DownloadIcon:
                DrawDownloadIcon(cellValue.Text, interior, foreground);
                break;
            case CustomTableCellKind.StatusIcon:
                DrawStatusIcon(cellValue.Text, interior, foreground);
                break;
            case CustomTableCellKind.CheckBox:
                DrawCheckBox(cellValue.IsChecked, cellValue.IsEnabled, interior);
                break;
            case CustomTableCellKind.RadioButton:
                DrawRadioButton(cellValue.IsChecked, cellValue.IsEnabled, interior);
                break;
            default:
                CustomTableTextStyle textStyle = cellValue.TextStyle ?? CustomTableTextStyle.Normal;
                DrawCellText(cellValue.Text, interior, slotLeft, slotRight, foreground, cellValue.TextRuns, cellValue.Alignment, textStyle.Kind, textStyle.VerticalOffset);
                break;
        }
    }

    private void DrawRowCells(CustomTableColumnLayoutSnapshot layout, CustomTableRowEntry row, bool selected, CustomTableColumn currentColumn, int y, bool hasRowGroups)
    {
        foreach (CustomTableColumnLayoutEntry entry in layout.VisibleEntries)
        {
            CustomTableColumn column = entry.Column;
            if (column == null)
            {
                continue;
            }
            (int left, int right) = GetSlotPixels(entry);
            var interior = new PixelRect(left, y, right - gridLinePx, y + rowHeightPx - gridLinePx);
            bool currentCell = currentColumn != null && string.Equals(currentColumn.Id, column.Id, StringComparison.Ordinal);
            if (hasRowGroups && column.MergesRowGroup)
            {
                // 結合列の背景（選択・現在セルの強調を含む）と内容は、グループの表示中の部分をまとめて一度だけ描く。ここでは縦罫線だけを行ごとに描く。
                if (right <= widthPx && interior.Right >= 0)
                {
                    FillRect(interior.Right, y, right, y + rowHeightPx, palette.CellBorder);
                }
                continue;
            }
            long cellValueStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
            CustomTableCellValue cellValue = row.GetValue(column, out _);
            if (measureRenderDetails)
            {
                renderCellValueTicks += Stopwatch.GetTimestamp() - cellValueStart;
            }
            float visibleLeft = Math.Max(0, interior.Left);
            float visibleRight = Math.Min(widthPx, interior.Right);
            if (!selected && cellValue.Background != null)
            {
                // 単色でないブラシは、セルの背景を描かず行の背景を見せる。
                FillRect(visibleLeft, interior.Top, visibleRight, interior.Bottom, cellValue.Background);
            }
            if (currentCell)
            {
                FillRect(visibleLeft, interior.Top, visibleRight, interior.Bottom, palette.CurrentCellBackground);
            }
            bool hasTextRuns = cellValue.TextRuns.Count > 0;
            Brush foreground = hasTextRuns
                ? cellValue.Foreground
                : currentCell
                    ? palette.CurrentCellForeground
                    : selected
                        ? palette.SelectedForeground
                        : cellValue.Foreground;
            DrawCellContent(cellValue, interior, left, right, foreground);
            if (right <= widthPx && interior.Right >= 0)
            {
                FillRect(interior.Right, y, right, y + rowHeightPx, palette.CellBorder);
            }
        }
    }

    /// <summary>描画先の物理画素の矩形です。</summary>
    private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;

        internal int Height => Bottom - Top;
    }

    private D2D.ID2D1SolidColorBrush ResolveTextBrush(Brush brush)
    {
        // 単色でないブラシは例外にせず、既定の文字色で描く。
        return display.GetBrush(brush)
            ?? display.GetBrush(palette.DefaultForeground)
            ?? display.GetBrush(Colors.Black);
    }

    /// <summary>
    /// セルの文字を一行で描き、セルの内側の矩形でクリップします。省略が要るセルだけ幅つきのレイアウトを使い、
    /// 通常の文字は描く位置を物理画素へ丸め、スコア用の文字は丸めません。
    /// </summary>
    private void DrawCellText(
        string text,
        PixelRect interior,
        int slotLeft,
        int slotRight,
        Brush foreground,
        IReadOnlyList<CustomTableTextRunStyle> textRuns,
        TextAlignment alignment,
        CustomTableTextKind kind,
        double verticalOffset)
    {
        if (string.IsNullOrEmpty(text) || interior.Width <= 2d * pixelsPerDip || interior.Height <= 1d * pixelsPerDip)
        {
            return;
        }
        int padding = CustomTablePixelGeometry.TextPadding(pixelsPerDip);
        int available = CustomTablePixelGeometry.AvailableTextWidth(slotLeft, slotRight, pixelsPerDip);
        long layoutStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
        CustomTableTextLayout drawn = textLayoutCache.GetOrCreate(text, kind, TextAlignment.Left, 0, out bool hit);
        CountLayoutLookup(hit);
        double x = slotLeft + padding;
        if (CustomTableTextLayoutCache.ExceedsAvailableWidth(drawn.NaturalWidth, available))
        {
            drawn = textLayoutCache.GetOrCreate(text, kind, alignment, available, out hit);
            CountLayoutLookup(hit);
        }
        else if (alignment == TextAlignment.Center)
        {
            x += (available - drawn.NaturalWidth) / 2d;
        }
        else if (alignment == TextAlignment.Right)
        {
            x += available - drawn.NaturalWidth;
        }
        double y = interior.Top + (interior.Height - drawn.LineHeight) / 2d + verticalOffset * pixelsPerDip;
        if (kind != CustomTableTextKind.Score)
        {
            // 画素に合わせて描く文字は、小数の位置へ置くと滲むため、画素の境界へ丸める。
            x = Math.Round(x, MidpointRounding.AwayFromZero);
            y = Math.Round(y, MidpointRounding.AwayFromZero);
        }
        if (measureRenderDetails)
        {
            renderTextLayoutTicks += Stopwatch.GetTimestamp() - layoutStart;
        }
        long drawTextStart = measureRenderDetails ? Stopwatch.GetTimestamp() : 0L;
        bool effectApplied = ApplyTextRuns(drawn, textRuns);
        target.PushAxisAlignedClip(new RawRectF(interior.Left, interior.Top, interior.Right, interior.Bottom), D2D.AntialiasMode.Aliased);
        target.DrawTextLayout(
            new Vector2((float)x, (float)y),
            drawn.Layout,
            ResolveTextBrush(foreground),
            kind == CustomTableTextKind.Score ? D2D.DrawTextOptions.NoSnap : D2D.DrawTextOptions.None);
        target.PopAxisAlignedClip();
        if (effectApplied)
        {
            // レイアウトは色に依存せず共有するため、区間の色は描いた直後に外す。
            drawn.Layout.SetDrawingEffect(null, new Vortice.DirectWrite.TextRange(0u, (uint)drawn.TextLength));
        }
        if (measureRenderDetails)
        {
            renderDrawTextTicks += Stopwatch.GetTimestamp() - drawTextStart;
        }
    }

    private void CountLayoutLookup(bool hit)
    {
        if (hit)
        {
            renderTextCacheHits++;
        }
        else
        {
            renderTextCacheMisses++;
        }
    }

    /// <summary>色付きの区間をレイアウトへ適用します。単色でないブラシの区間は、既定の文字色のままにします。</summary>
    private bool ApplyTextRuns(CustomTableTextLayout drawn, IReadOnlyList<CustomTableTextRunStyle> textRuns)
    {
        if (textRuns == null || textRuns.Count == 0 || drawn.TextLength <= 0)
        {
            return false;
        }
        bool applied = false;
        foreach (CustomTableTextRunStyle run in textRuns)
        {
            if (run.Foreground == null || run.Length <= 0 || run.StartIndex >= drawn.TextLength)
            {
                continue;
            }
            D2D.ID2D1SolidColorBrush brush = display.GetBrush(run.Foreground);
            if (brush == null)
            {
                continue;
            }
            int length = Math.Min(run.Length, drawn.TextLength - run.StartIndex);
            if (length > 0)
            {
                drawn.Layout.SetDrawingEffect(brush, new Vortice.DirectWrite.TextRange((uint)run.StartIndex, (uint)length));
                applied = true;
            }
        }
        return applied;
    }
}
