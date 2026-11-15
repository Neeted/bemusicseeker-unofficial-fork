using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 一覧の表示面の描画結果を、物理画素で確かめます。表示面の画像だけを読み、描画器が申告する値を使いません。
/// 窓のない表は <c>VisualTreeHelper.SetRootDpi</c> で倍率を明示し、通常は100%、丸めの検査は125%と175%です。
/// 画素の比較はこの描画器どうしに限り、WPFの描画との比較はしません。
/// </summary>
[TestClass]
public sealed class CustomTableRenderingTests
{
    private static Color Solid(Brush brush) => ((SolidColorBrush)brush).Color;

    private static Color Solid(Pen pen) => Solid(pen.Brush);

    private static CustomTableColumn Column(string id, int width, TextAlignment alignment = TextAlignment.Left, Func<object, string>? text = null, string header = "",
        Func<object, Brush>? foreground = null, Func<object, Brush>? background = null, CustomTableCellKind kind = CustomTableCellKind.Text,
        CustomTableTextStyle? style = null, string? sortMemberPath = null, Func<object, bool?>? isChecked = null,
        Func<object, IReadOnlyList<CustomTableTextRunStyle>>? runs = null)
    {
        return new CustomTableColumn(id, header, new CustomTableColumnSettings.ColumnLayout { Width = width, Visibility = Visibility.Visible }, 0, sortMemberPath, alignment,
            text ?? (_ => string.Empty), foregroundSelector: foreground, backgroundSelector: background, cellKind: kind, textStyle: style, checkedSelector: isChecked,
            textRunSelector: runs == null ? null : (row, _) => runs(row));
    }

    private static CustomTableView CreateTable(IReadOnlyList<CustomTableColumn> columns, System.Collections.IList rows, double width, double height,
        double dpi = 1d, double rowHeight = 19d, double headerHeight = 20d, FontFamily? font = null)
    {
        var table = new CustomTableView
        {
            Width = width,
            Height = height,
            RowHeight = rowHeight,
            HeaderHeight = headerHeight,
            Columns = [.. columns],
            ItemsSource = rows
        };
        if (font != null) { table.TextFontFamily = font; }
        CustomTableRenderProbe.SetDpi(table, dpi);
        CustomTableRenderProbe.Arrange(table, width, height);
        table.ApplyVisibilityChanged(true);
        return table;
    }

    private static void Release(CustomTableView table)
    {
        table.ApplyVisibilityChanged(false);
        table.ItemsSource = null;
    }

    private static object[] Rows(int count) => [.. Enumerable.Range(0, count).Select(_ => new object())];

    /// <summary>物理画素の整数の位置の矩形（x, y, 幅, 高さ）を、表示倍率で換算した値で作ります。</summary>
    private static int Px(double dip, double dpi) => CustomTablePixelGeometry.ToPixels(dip, dpi);

    /// <summary>
    /// 配置の確定（<c>UpdateLayout</c>）だけで、Dispatcherを回さずに表示面の画素が新しい状態になり、描画要求一回につき描画は一回であることを確認します。
    /// 要求しただけでは描かず、同じ状態で配置を確定し直しても再描画しません。
    /// </summary>
    /// <remarks>描画がDispatcherやレンダリングイベントの後ろへ回って窓のない表で描けない誤りと、要求のたびに複数回描く誤りを検出します。</remarks>
    [TestMethod]
    public void Redraw_CompletesInUpdateLayoutAndRendersOncePerRequest()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable([Column("A", 120, text: _ => "Row")], Rows(3), 200d, 100d);
            try
            {
                table.UpdateLayout();
                int count = table.LastRenderStats.RenderCount;
                SurfacePixels before = CustomTableRenderProbe.Read(table);
                int row1Y = 20 + 19 + 5;
                Assert.AreEqual(Solid(CustomTablePalette.Current.AlternatingRowBackground), before.GetColor(150, row1Y));

                table.SelectedIndex = 1;
                Assert.AreEqual(count, table.LastRenderStats.RenderCount, "描画の要求だけでは描きません。");
                table.UpdateLayout();
                Assert.AreEqual(count + 1, table.LastRenderStats.RenderCount, "描画の要求一回につき描画は一回です。");
                SurfacePixels after = CustomTableRenderProbe.Read(table);
                Assert.AreEqual(Solid(CustomTablePalette.Current.SelectedRowBackground), after.GetColor(150, row1Y), "Dispatcherを回さずに新しい状態の画素になります。");

                table.UpdateLayout();
                Assert.AreEqual(count + 1, table.LastRenderStats.RenderCount, "同じ状態で配置を確定し直しても再描画しません。");
                CustomTableRenderProbe.Redraw(table);
                Assert.AreEqual(count + 2, table.LastRenderStats.RenderCount);
            }
            finally { Release(table); }
        });
    }

    /// <summary>差替えの待機中と空の一覧では、本文の領域が行の背景だけで文字も線もなく、見出しは描くことを確認します。待機の前に描いた行の画素が残らないことも確認します。</summary>
    /// <remarks>差替えの待機中に古い行を描き続ける誤りと、空の一覧に罫線を描く誤り、見出しを描かない誤りを検出します。</remarks>
    [TestMethod]
    public void Body_IsEmptyWhileItemsSourceSwapIsPendingAndWhenListIsEmpty()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable([Column("A", 120, text: _ => "Cell", header: "HEAD")], Rows(4), 200d, 100d);
            try
            {
                Color rowBackground = Solid(CustomTablePalette.Current.RowBackground);
                var body = new Int32Rect(0, 20, 200, 80);
                SurfacePixels shown = CustomTableRenderProbe.Redraw(table);
                Assert.IsTrue(shown.CountNotEqual(body, rowBackground) > 0, "行を描いています。");

                table.PrepareForItemsSourceSwap();
                SurfacePixels pending = CustomTableRenderProbe.Redraw(table);
                Assert.AreEqual(0, pending.CountNotEqual(body, rowBackground), "差替えの待機中は本文を描きません。古い行の画素も残りません。");
                Assert.IsTrue(pending.CountNotEqual(new Int32Rect(0, 1, 119, 17), Solid(CustomTablePalette.Current.HeaderBackground)) > 0, "見出しは描きます。");
                Assert.IsTrue(table.IsItemsSourceSwapPending);

                table.CancelPendingItemsSourceSwapPreparation();
                Assert.IsTrue(CustomTableRenderProbe.Redraw(table).CountNotEqual(body, rowBackground) > 0);

                table.ItemsSource = new List<object>();
                SurfacePixels empty = CustomTableRenderProbe.Redraw(table);
                Assert.AreEqual(0, empty.CountNotEqual(body, rowBackground), "空の一覧では本文を描きません。");
                Assert.IsTrue(empty.CountNotEqual(new Int32Rect(0, 1, 119, 17), Solid(CustomTablePalette.Current.HeaderBackground)) > 0, "空の一覧でも見出しは描きます。");
            }
            finally { Release(table); }
        });
    }

    /// <summary>
    /// 見出しの上端に全幅の1 DIPの線、下端の線、列の縦罫線を描き、横罫線は完全に見える行だけに描き、部分表示の行にも縦罫線と文字を描くことを確認します。
    /// 縦スクロールで部分表示の行が通常の行になっても、欠けないことを確認します。
    /// </summary>
    /// <remarks>見出しの上端の線を落とす誤り、部分表示の行へ下端の線をはみ出させる誤り、スクロール後に罫線が欠ける誤りを検出します。</remarks>
    [TestMethod]
    public void Borders_AreDrawnOnHeaderAndOnlyFullyVisibleRows()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable([Column("A", 60, text: _ => "MMMM", foreground: _ => Brushes.Red), Column("B", 80)], Rows(5), 140d, 70d);
            try
            {
                Color headerBorder = Solid(CustomTablePalette.Current.HeaderBorder), cellBorder = Solid(CustomTablePalette.Current.CellBorder);
                SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                for (int x = 0; x < 140; x++)
                {
                    Assert.AreEqual(headerBorder, pixels.GetColor(x, 0), "見出しの上端の線 x=" + x);
                    Assert.AreEqual(headerBorder, pixels.GetColor(x, 19), "見出しの下端の線 x=" + x);
                }
                for (int y = 1; y < 19; y++) { Assert.AreEqual(headerBorder, pixels.GetColor(59, y), "見出しの列の縦罫線 y=" + y); }
                // 行0と行1は完全に見える。
                for (int x = 0; x < 140; x++)
                {
                    Assert.AreEqual(cellBorder, pixels.GetColor(x, 38), "行0の下端 x=" + x);
                    Assert.AreEqual(cellBorder, pixels.GetColor(x, 57), "行1の下端 x=" + x);
                }
                // 行2は上から12行だけ見える部分表示で、横罫線は描かないが、縦罫線と文字は描く。
                Color alternating = Solid(CustomTablePalette.Current.RowBackground);
                Assert.AreEqual(alternating, pixels.GetColor(100, 69), "部分表示の行の最下行は罫線ではなく行の背景");
                for (int y = 58; y < 70; y++) { Assert.AreEqual(cellBorder, pixels.GetColor(59, y), "部分表示の行の縦罫線 y=" + y); }
                Assert.IsTrue(pixels.CountDominant(new Int32Rect(0, 58, 58, 12)).Red > 0, "部分表示の行にも文字を描きます。");

                // スクロールして通常の行になっても欠けない。
                table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical).Value = 1d;
                SurfacePixels scrolled = CustomTableRenderProbe.Redraw(table);
                for (int x = 0; x < 140; x++) { Assert.AreEqual(cellBorder, scrolled.GetColor(x, 57), "スクロール後の行の下端 x=" + x); }
                for (int y = 39; y < 57; y++) { Assert.AreEqual(cellBorder, scrolled.GetColor(59, y), "スクロール後の縦罫線 y=" + y); }
                Assert.IsTrue(scrolled.CountDominant(new Int32Rect(0, 39, 58, 18)).Red > 0, "スクロール後も文字が残ります。");
            }
            finally { Release(table); }
        });
    }

    /// <summary>
    /// 175%（罫線は2画素）で横スクロールした状態で、左端で半分隠れた縦罫線と、右端で半分切れた列の縦罫線は見出しにも本文にも描かず、
    /// 完全に見える列の縦罫線は見出しと本文に描くことを、表示面の物理画素で確認します。
    /// </summary>
    /// <remarks>
    /// 横位置 <c>40 − 4/7</c> DIPで、列の右端は物理画素の 1・106・211・316 になり、表示面の幅は315画素です。
    /// 罫線は右端の内側の2画素なので、一本目は −1〜0、四本目は 314〜315 にまたがって半分だけ見えます。
    /// 見える側の半分だけを描く誤り、表示面の端へ寄せて描く誤り、見える罫線を落とす誤りを検出します。
    /// </remarks>
    [TestMethod]
    public void VerticalBorders_AreDrawnOnlyWhenFullyVisibleWhileScrolledHorizontally()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const double dpi = 1.75d;
            CustomTableView table = CreateTable([Column("A", 40), Column("B", 60), Column("C", 60), Column("D", 60), Column("E", 60)], Rows(2), 180d, 80d, dpi);
            try
            {
                ScrollBar horizontal = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Horizontal);
                Assert.AreEqual(Visibility.Visible, horizontal.Visibility);
                horizontal.Value = 40d - 4d / 7d;
                SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);
                Assert.AreEqual(315, pixels.Width);
                Assert.AreEqual(2, CustomTablePixelGeometry.GridLineWidth(dpi));
                Assert.AreEqual(35, table.RowGeometry.HeaderHeightPx);
                Assert.AreEqual(33, table.RowGeometry.RowHeightPx);
                CustomTablePalette palette = CustomTablePalette.Current;
                Color headerBorder = Solid(palette.HeaderBorder), cellBorder = Solid(palette.CellBorder);
                // 見出しの上下の線を避けた見出しの中段と、行0・行1の中段の画素行。
                (int Y, Color Border, Color Background)[] bands =
                [
                    (17, headerBorder, Solid(palette.HeaderBackground)),
                    (35 + 16, cellBorder, Solid(palette.RowBackground)),
                    (35 + 33 + 16, cellBorder, Solid(palette.AlternatingRowBackground)),
                ];
                foreach ((int y, Color border, Color background) in bands)
                {
                    foreach (int right in new[] { 106, 211 })
                    {
                        Assert.AreEqual(border, pixels.GetColor(right - 2, y), $"完全に見える縦罫線 右端{right} y={y}");
                        Assert.AreEqual(border, pixels.GetColor(right - 1, y), $"完全に見える縦罫線 右端{right} y={y}");
                        Assert.AreEqual(background, pixels.GetColor(right - 3, y), $"罫線のすぐ左は背景 右端{right} y={y}");
                        Assert.AreEqual(background, pixels.GetColor(right, y), $"罫線のすぐ右は背景 右端{right} y={y}");
                    }
                    Assert.AreEqual(background, pixels.GetColor(0, y), $"左端で半分隠れた縦罫線は描きません y={y}");
                    Assert.AreEqual(background, pixels.GetColor(314, y), $"右端で半分切れた列の縦罫線は描きません y={y}");
                    Assert.AreEqual(background, pixels.GetColor(313, y), $"右端で切れた列の内側は背景 y={y}");
                }
            }
            finally { Release(table); }
        });
    }

    /// <summary>並べ替えの矢印を並べ替え中の列だけに描き、昇順と降順で向きが違うこと、行ドロップの挿入線が指定位置に <c>ColumnReorderInsert</c> の色で描かれることを確認します。</summary>
    /// <remarks>並べ替えていない列にも矢印を描く誤り、昇順と降順の向きの取り違え、挿入線の位置と色の誤りを検出します。</remarks>
    [TestMethod]
    public void SortGlyphAndRowDropInsertLine_AreDrawnAtTheirPositions()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable([Column("A", 60, sortMemberPath: "A"), Column("B", 60, sortMemberPath: "B")], Rows(5), 120d, 120d);
            try
            {
                Color header = Solid(CustomTablePalette.Current.HeaderBackground);
                // 矢印は見出しの上端から1 DIP下の三角形で、列の中央に描かれる。
                int InkInRow(SurfacePixels pixels, int column, int y) => pixels.CountNotEqual(new Int32Rect(column * 60 + 20, y, 20, 1), header);
                table.SetCurrentValue(CustomTableView.SortColumnNameProperty, "A");
                table.SetCurrentValue(CustomTableView.SortDirectionProperty, ListSortDirection.Ascending);
                SurfacePixels ascending = CustomTableRenderProbe.Redraw(table);
                Assert.IsTrue(InkInRow(ascending, 0, 4) > InkInRow(ascending, 0, 1), "昇順は先端が上の三角形です。");
                Assert.AreEqual(0, InkInRow(ascending, 1, 2), "並べ替えていない列には描きません。");
                table.SetCurrentValue(CustomTableView.SortDirectionProperty, ListSortDirection.Descending);
                SurfacePixels descending = CustomTableRenderProbe.Redraw(table);
                Assert.IsTrue(InkInRow(descending, 0, 1) > InkInRow(descending, 0, 4), "降順は先端が下の三角形です。");
                Assert.AreEqual(0, InkInRow(descending, 1, 2));
                Assert.IsFalse(ascending.SameAs(descending));

                // 挿入線は、指定の挿入位置の境界を中心に、3 DIPの太さで描かれる。
                table.SetRowDropInsertPreview(2);
                SurfacePixels line = CustomTableRenderProbe.Redraw(table);
                Color insert = Solid(CustomTablePalette.Current.ColumnReorderInsert);
                int y = 20 + 2 * 19;
                for (int x = 0; x < 120; x += 17)
                {
                    foreach (int dy in new[] { -1, 0, 1 }) { Assert.AreEqual(insert, line.GetColor(x, y + dy), $"挿入線 x={x} y={y + dy}"); }
                    Assert.AreNotEqual(insert, line.GetColor(x, y + 3));
                    Assert.AreNotEqual(insert, line.GetColor(x, y - 3));
                }
                table.ClearRowDropInsertPreview();
                Assert.AreNotEqual(insert, CustomTableRenderProbe.Redraw(table).GetColor(10, y));
            }
            finally { Release(table); }
        });
    }

    /// <summary>ダウンロード・状態の各種類・チェックボックスのセルで、前景色の描画がセルの内側にあり、種類ごとに異なり、値のないチェックボックスは何も描かないことを確認します。</summary>
    /// <remarks>アイコンを描かない誤り、種類を取り違えて同じ形を描く誤り、値のないチェックボックスに枠を描く誤りを検出します。</remarks>
    [TestMethod]
    public void Icons_AreDrawnPerKindInsideTheCell()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var columns = new List<CustomTableColumn> { Column("Download", 40, text: _ => "d", foreground: _ => Brushes.Red, kind: CustomTableCellKind.DownloadIcon) };
            string[] kinds = [nameof(CustomTableStatusIconKind.Forward), nameof(CustomTableStatusIconKind.Backward), nameof(CustomTableStatusIconKind.Play), nameof(CustomTableStatusIconKind.Loading),
                nameof(CustomTableStatusIconKind.Pause), nameof(CustomTableStatusIconKind.Searching), nameof(CustomTableStatusIconKind.ScoreUnsent)];
            columns.AddRange(kinds.Select(kind => Column(kind, 40, text: _ => kind, foreground: _ => Brushes.Red, kind: CustomTableCellKind.StatusIcon)));
            columns.Add(Column("On", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.CheckBox, isChecked: _ => true));
            columns.Add(Column("Off", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.CheckBox, isChecked: _ => false));
            columns.Add(Column("Null", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.CheckBox, isChecked: _ => null));
            CustomTableView table = CreateTable(columns, Rows(1), 40d * columns.Count, 60d);
            try
            {
                SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                Color background = Solid(CustomTablePalette.Current.RowBackground);
                Int32Rect Cell(int column) => new(column * 40, 20, 39, 18);
                int RedInk(int column)
                {
                    int count = 0;
                    Int32Rect cell = Cell(column);
                    for (int y = cell.Y; y < cell.Y + cell.Height; y++)
                    {
                        for (int x = cell.X; x < cell.X + cell.Width; x++) { Color c = pixels.GetColor(x, y); if (c.R - Math.Max(c.G, c.B) >= 40) { count++; } }
                    }
                    return count;
                }
                for (int column = 0; column <= kinds.Length; column++)
                {
                    Assert.IsTrue(RedInk(column) > 0, columns[column].Id + " の前景色の描画がセルの内側にあります。");
                    Assert.IsNotNull(pixels.FindInkBounds(Cell(column), background));
                }
                for (int first = 0; first < columns.Count; first++)
                {
                    for (int second = first + 1; second < columns.Count; second++)
                    {
                        Assert.IsFalse(pixels.RegionEquals(Cell(first), pixels, second * 40, 20), columns[first].Id + " と " + columns[second].Id + " は異なる形です。");
                    }
                }
                int on = columns.Count - 3, off = columns.Count - 2, none = columns.Count - 1;
                Assert.IsTrue(RedInk(on) > 0, "オンのチェックは前景色で描きます。");
                Assert.AreEqual(0, pixels.CountNotEqual(Cell(none), background), "値のないチェックボックスは何も描きません。");
                Assert.IsTrue(pixels.CountNotEqual(Cell(off), background) > 0, "オフのチェックボックスは枠を描きます。");
            }
            finally { Release(table); }
        });
    }

    /// <summary>左・中央・右の配置で文字がセルの内側の余白の基準に沿い、大文字Hの上下中央がセルの内側の中央にあり、狭い列の文字が隣のセルへはみ出さないことを、100%と125%、二つのフォント、二つの行の高さで確認します。</summary>
    /// <remarks>配置の基準のずれ、行の高さによる上下のずれ、クリップの漏れを検出します。ArialとMeiryo UIのインストールを前提にします。</remarks>
    [TestMethod]
    [DataRow(1d, 19d, false)]
    [DataRow(1d, 20d, true)]
    [DataRow(1.25d, 19d, false)]
    [DataRow(1.25d, 20d, true)]
    public void Text_FollowsAlignmentAndVerticalCenterAndStaysInsideTheCell(double dpi, double rowHeight, bool arial)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            FontFamily font = arial ? AppFontService.CreateFontFamily("Arial") : AppFontService.DefaultFontFamily;
            CustomTableView table = CreateTable(
                [Column("L", 80, TextAlignment.Left, _ => "HHHH"), Column("C", 80, TextAlignment.Center, _ => "HHHH"), Column("R", 80, TextAlignment.Right, _ => "HHHH"),
                    Column("N", 40, TextAlignment.Left, _ => "HHHHHHHHHHHHHHHHHHHH"), Column("E", 60)],
                Rows(2), 340d, 80d, dpi, rowHeight, font: font);
            try
            {
                SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                Color background = Solid(CustomTablePalette.Current.RowBackground);
                int pad = CustomTablePixelGeometry.TextPadding(dpi), lw = CustomTablePixelGeometry.GridLineWidth(dpi);
                int top = Px(20d, dpi), rowPx = table.RowGeometry.RowHeightPx;
                int[] edges = [0, 80, 160, 240, 280, 340];
                (int Left, int Right) Slot(int column) => (Px(edges[column], dpi), Px(edges[column + 1], dpi));
                Int32Rect Interior(int column) => new(Slot(column).Left, top, Slot(column).Right - Slot(column).Left - lw, rowPx - lw);
                for (int column = 0; column < 3; column++)
                {
                    Int32Rect? ink = pixels.FindInkBounds(Interior(column), background);
                    Assert.IsNotNull(ink, "列" + column);
                    (int left, int right) = Slot(column);
                    int avail = CustomTablePixelGeometry.AvailableTextWidth(left, right, dpi);
                    Assert.IsTrue(ink.Value.X >= left + pad && ink.Value.X + ink.Value.Width <= right - lw - pad + 1, "余白の内側に収まる 列" + column);
                    int inkLeft = ink.Value.X, inkRight = ink.Value.X + ink.Value.Width;
                    if (column == 0) { double gap = inkLeft - (left + pad); Assert.IsTrue(gap >= 0d && gap <= 2d * dpi, "左寄せ " + gap); }
                    else if (column == 1) { Assert.IsTrue(Math.Abs((inkLeft + inkRight) / 2d - (left + pad + avail / 2d)) <= 1d * dpi, "中央寄せ"); }
                    else { double gap = (right - lw - pad) - inkRight; Assert.IsTrue(gap >= 0d && gap <= 2d * dpi, "右寄せ " + gap); }
                    double interiorCenter = Interior(column).Y + Interior(column).Height / 2d;
                    Assert.IsTrue(Math.Abs(ink.Value.Y + ink.Value.Height / 2d - interiorCenter) <= 1d, "大文字Hの上下中央 " + (ink.Value.Y + ink.Value.Height / 2d) + " / " + interiorCenter);
                }
                // 狭い列の長い文字は省略され、列の外（隣の空の列）へはみ出さない。
                Int32Rect neighbor = new(Slot(4).Left, top, Slot(4).Right - Slot(4).Left - lw, rowPx - lw);
                Assert.AreEqual(0, pixels.CountNotEqual(neighbor, background), "隣のセルへ描画がはみ出しません。" + pixels.FindInkBounds(neighbor, background) + " neighbor=" + neighbor + " narrowInk=" + pixels.FindInkBounds(Interior(3), background));
                (int narrowLeft, int narrowRight) = Slot(3);
                for (int y = top; y < top + rowPx; y++) { Assert.AreEqual(Solid(CustomTablePalette.Current.CellBorder), pixels.GetColor(narrowRight - 1, y), "狭い列の右の罫線"); }
                Int32Rect? narrowInk = pixels.FindInkBounds(Interior(3), background);
                Assert.IsNotNull(narrowInk);
                Assert.IsTrue(narrowInk.Value.X + narrowInk.Value.Width <= narrowRight - lw - pad + 1);
            }
            finally { Release(table); }
        });
    }

    /// <summary>列幅を文字の自然な幅の前後で動かし、100%と125%の各幅で「ツールチップがある」ことと「幅つきのレイアウトを新しく作った」ことが一致することを確認します。高さに余裕のある行でも一行で描き、省略した文字は文字領域の右端を越えません。</summary>
    /// <remarks>描画とツールチップで省略の判定が食い違う誤り、行の高さに余裕があると折り返す誤り、省略した文字が右の余白へはみ出す誤りを検出します。</remarks>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    public void Trimming_AgreesBetweenDrawingAndTooltipAndStaysOnOneLine(double dpi)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const string text = "one two three four five six seven";
            double natural;
            using (var cache = new CustomTableTextLayoutCache())
            {
                CultureInfo culture = CultureInfo.CurrentCulture;
                cache.Configure(new CustomTableTextEnvironment(AppFontService.DefaultFontFamily, null!, BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize, dpi, culture, CultureInfo.CurrentUICulture));
                natural = cache.GetOrCreate(text, CustomTableTextKind.Normal, TextAlignment.Left, 0, out _).NaturalWidth;
            }
            int pad = CustomTablePixelGeometry.TextPadding(dpi), lw = CustomTablePixelGeometry.GridLineWidth(dpi);
            int centerWidth = (int)Math.Round((natural + 2 * pad + lw) / dpi);
            int trimmedCount = 0, fitCount = 0;
            for (int width = centerWidth - 4; width <= centerWidth + 4; width++)
            {
                CustomTableView table = CreateTable([Column("A", width, text: _ => text)], Rows(1), width, 100d, dpi, rowHeight: 60d);
                try
                {
                    // 非表示と再表示で文字のレイアウトを破棄し、再表示の最初の描画の計数を読む。幅なしの一件に、省略が要るときだけ幅つきの一件が加わる。
                    table.ApplyVisibilityChanged(false);
                    table.ApplyVisibilityChanged(true);
                    table.UpdateLayout();
                    Assert.AreEqual(0, table.LastRenderStats.TextCacheHits);
                    int misses = table.LastRenderStats.TextCacheMisses;
                    bool tooltip = table.ResolveCellToolTip(table.HitTestTable(new Point(5d, table.RowGeometry.HeaderHeight + 5d))) != null;
                    Assert.AreEqual(tooltip, misses == 2, "幅 " + width + " の描画とツールチップの判定 misses=" + misses + " hits=" + table.LastRenderStats.TextCacheHits + " natural=" + natural + " renders=" + table.LastRenderStats.RenderCount);
                    Assert.IsTrue(misses is 1 or 2);
                    if (tooltip) { trimmedCount++; } else { fitCount++; }

                    SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                    Color background = Solid(CustomTablePalette.Current.RowBackground);
                    int slotRight = Px(width, dpi);
                    int top = table.RowGeometry.HeaderHeightPx, rowPx = table.RowGeometry.RowHeightPx;
                    var area = new Int32Rect(0, top, slotRight - lw, rowPx - lw);
                    Int32Rect? ink = pixels.FindInkBounds(area, background);
                    Assert.IsNotNull(ink);
                    Assert.IsTrue(ink.Value.Height <= 24d * dpi, "高さに余裕があっても一行で描きます。高さ=" + ink.Value.Height);
                    Assert.IsTrue(ink.Value.X + ink.Value.Width <= slotRight - lw - pad, "文字領域の右端を越えません。右=" + (ink.Value.X + ink.Value.Width));
                }
                finally { Release(table); }
            }
            Assert.IsTrue(trimmedCount > 0 && fitCount > 0, "走査した幅に、省略する幅と収まる幅の両方があります。");
        });
    }

    /// <summary>
    /// 中央揃えの列幅の偶奇（40と41）や行の高さ（19と20）を変えても、字形の画素の並びが整数の平行移動で一致することを確認します。
    /// 文字を小数の位置へ描いて、位置によって字形の見え方が変わる誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    public void CenteredText_HasTheSameGlyphPixelsUnderIntegerTranslation(double dpi)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Color background = Solid(CustomTablePalette.Current.RowBackground);
            (SurfacePixels Pixels, Int32Rect Ink) Render(int width, double rowHeight)
            {
                CustomTableView table = CreateTable([Column("A", width, TextAlignment.Center, _ => "Hello Wg")], Rows(1), width, 60d, dpi, rowHeight);
                try
                {
                    SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                    int top = table.RowGeometry.HeaderHeightPx, rowPx = table.RowGeometry.RowHeightPx;
                    Int32Rect? ink = pixels.FindInkBounds(new Int32Rect(0, top, Px(width, dpi) - 1, rowPx - 1), background);
                    Assert.IsNotNull(ink);
                    return (pixels, ink.Value);
                }
                finally { Release(table); }
            }
            (SurfacePixels basePixels, Int32Rect baseInk) = Render(40, 19d);
            foreach ((int width, double rowHeight) in new[] { (41, 19d), (40, 20d), (41, 20d) })
            {
                (SurfacePixels pixels, Int32Rect ink) = Render(width, rowHeight);
                Assert.AreEqual(baseInk.Width, ink.Width, $"幅{width} 高さ{rowHeight}");
                Assert.AreEqual(baseInk.Height, ink.Height, $"幅{width} 高さ{rowHeight}");
                Assert.IsTrue(basePixels.RegionEquals(baseInk, pixels, ink.X, ink.Y), $"幅{width} 高さ{rowHeight} の字形の画素の並びが一致します。");
            }
        });
    }

    /// <summary>
    /// 未インストールの名前と空の名前から作ったフォントは、既定のフォントの表と画素が一致し、選択したフォントでは画素が違うことを確認します。
    /// 存在しないフォント名で別のフォントや代替の字形に落ちる誤りを検出します。ArialとMeiryo UIのインストールを前提にします。
    /// </summary>
    [TestMethod]
    public void UnknownOrEmptyFontName_RendersLikeTheDefaultFont()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            SurfacePixels Render(FontFamily font)
            {
                CustomTableView table = CreateTable([Column("A", 200, text: _ => "Hello World 日本語 123", header: "TITLE")], Rows(2), 200d, 80d, font: font);
                try { return CustomTableRenderProbe.Redraw(table); }
                finally { Release(table); }
            }
            SurfacePixels defaultPixels = Render(AppFontService.DefaultFontFamily);
            Assert.IsFalse(defaultPixels.SameAs(Render(AppFontService.CreateFontFamily("Arial"))), "選択したフォントでは画素が違います。");
            foreach (string name in new[] { "BeMusicSeeker No Such Font", string.Empty, "  " })
            {
                Assert.IsTrue(defaultPixels.SameAs(Render(AppFontService.CreateFontFamily(name))), "名前 '" + name + "' は既定のフォントと同じ画素です。");
            }
        });
    }

    /// <summary>同梱のSovjet Boxで描いたスコア列は表示フォントで描いた場合と画素が違い、読み込めないスコア書体では <c>ScoreFontFamily=null</c> の表と画素が一致し、未処理例外がないことを確認します。</summary>
    /// <remarks>スコア用のフォントを読み込まず表示フォントで描き続ける誤りと、読み込めない書体で例外にする誤りを検出します。</remarks>
    [TestMethod]
    public void ScoreFont_UsesTheBundledFontAndFallsBackToTheDisplayFontWhenItCannotBeLoaded()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            SurfacePixels Render(FontFamily? scoreFont)
            {
                CustomTableView table = CreateTable([Column("S", 100, TextAlignment.Center, _ => "AAA 1234", style: CustomTableTextStyle.Score)], Rows(2), 100d, 80d);
                try
                {
                    table.ScoreFontFamily = scoreFont;
                    return CustomTableRenderProbe.Redraw(table);
                }
                finally { Release(table); }
            }
            var fontsFolder = new Uri("pack://application:,,,/BeMusicSeeker;component/Assets/Fonts/");
            SurfacePixels display = Render(null);
            SurfacePixels bundled = Render(new FontFamily(fontsFolder, "./#Sovjet Box"));
            Assert.IsFalse(display.SameAs(bundled), "同梱のスコア用のフォントで描きます。");
            Assert.IsTrue(bundled.SameAs(Render(new FontFamily(fontsFolder, "./#Sovjet Box"))), "同じ書体は同じ画素です。");
            foreach (FontFamily unloadable in new[] { new FontFamily(fontsFolder, "./#BeMusicSeeker No Such Score Font"), new FontFamily(new Uri("pack://application:,,,/NoSuchAssembly;component/Assets/"), "./#Missing") })
            {
                Assert.IsTrue(display.SameAs(Render(unloadable)), "読み込めないスコア書体は表示フォントで描きます。" + unloadable.Source);
            }
            TestUiDispatcherHost.AssertNoUnhandledExceptions("score-font-fallback");
        });
    }

    /// <summary>
    /// 125%と175%で、行の高さと見出しの高さが <c>round(h×倍率)</c> の物理画素になり、行が丁度 m 行収まる高さの表で、完全表示行容量と描画行数が m、
    /// 縦の最大値が <c>行数 − m</c>、m 行だけの一覧では縦スクロールバーが出ず、m+1 行で初めて出ることを確認します。
    /// 行の境界ちょうどの画素行の当たり判定は境界から始まる行を返し、その一つ上の画素行は前の行（行0なら見出し）を返します。
    /// 同じ画素の値で罫線の位置、編集欄の上端と高さが一致し、1 DIPの線は <c>max(1, round(倍率))</c> 画素の幅で厳密な色を持ち、隣の画素と混色せず、列の境界は物理画素に揃います。
    /// <c>RowHeight</c> と <c>HeaderHeight</c> の値は変わりません。期待値は倍率・高さ・行数から手で求めた物理画素の定数です。
    /// </summary>
    /// <remarks>
    /// 丸めた高さをDIPの浮動小数で割って、行が丁度収まる高さで容量が一つ少なくなる誤り（全行が収まるのに縦バーが出る、縦の最大値が一つ大きい）、
    /// 境界の画素行で前の行を返す誤り、描画と当たり判定が別の高さを使ってずれる誤り、スクロール範囲だけ丸め前の高さで求める誤り、罫線が隣の画素と混ざる誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(1.25d, 21d, 21d, 26, 26, 7)]
    [DataRow(1.75d, 19d, 20d, 33, 35, 11)]
    public void RoundedHeightsAreSharedByDrawingHitTestScrollRangeAndEditor(double dpi, double rowHeight, double headerHeight, int rowPx, int headerPx, int fitRows)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            // 見出しと m 行が丁度収まる表の高さ（DIP）。物理画素では headerPx + m × rowPx。
            double surfaceHeight = (headerPx + fitRows * rowPx) / dpi;
            int rowCount = 60;
            CustomTableColumn first = Column("A", 41, text: _ => string.Empty, kind: CustomTableCellKind.Text);
            var editable = new CustomTableColumn("B", string.Empty, new CustomTableColumnSettings.ColumnLayout { Width = 63, Visibility = Visibility.Visible }, 1, null, TextAlignment.Left, _ => "edit", editPropertyName: "B");
            CustomTableView table = CreateTable([first, editable], new List<object>(Rows(rowCount)), 150d, surfaceHeight, dpi, rowHeight, headerHeight);
            try
            {
                Assert.AreEqual(rowHeight, table.RowHeight);
                Assert.AreEqual(headerHeight, table.HeaderHeight);
                Assert.AreEqual(rowPx, table.RowGeometry.RowHeightPx);
                Assert.AreEqual(headerPx, table.RowGeometry.HeaderHeightPx);
                Assert.AreEqual(headerPx + fitRows * rowPx, table.RowGeometry.SurfaceHeightPx);

                // 完全表示行容量と縦の最大値。
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                Assert.AreEqual(fitRows, table.CalculateFullVisibleRowCapacity(), "行が丁度収まる高さの完全表示行容量");
                Assert.AreEqual(fitRows, table.CalculateDrawableRowCapacity(), "行が丁度収まる高さの描画行数");
                Assert.AreEqual(rowCount - fitRows, vertical.Maximum, "縦の最大値");
                Assert.AreEqual(fitRows, vertical.ViewportSize);

                // 当たり判定の境界。境界の画素行は境界から始まる行に、その一つ上の画素行は前の行に属する。
                Assert.AreEqual(CustomTableHitKind.Header, table.HitTestTable(new Point(10d, (headerPx - 1) / dpi)).Kind);
                for (int row = 0; row < fitRows; row++)
                {
                    int top = headerPx + row * rowPx;
                    CustomTableHitTestResult atBoundary = table.HitTestTable(new Point(10d, top / dpi));
                    Assert.AreEqual(CustomTableHitKind.Cell, atBoundary.Kind, "境界の画素行 行" + row);
                    Assert.AreEqual(row, atBoundary.RowIndex, "境界の画素行 行" + row);
                    Assert.AreEqual(top / dpi, atBoundary.CellRect.Top, 1e-9, "行" + row + " の上端");
                    Assert.AreEqual(row, table.HitTestTable(new Point(10d, (top + rowPx - 1) / dpi)).RowIndex, "行の最後の画素行 行" + row);
                    if (row > 0)
                    {
                        Assert.AreEqual(row - 1, table.HitTestTable(new Point(10d, (top - 1) / dpi)).RowIndex, "境界の一つ上の画素行 行" + row);
                    }
                }

                // 罫線の画素位置と混色のなさ。
                SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);
                int lw = CustomTablePixelGeometry.GridLineWidth(dpi);
                Color cellBorder = Solid(CustomTablePalette.Current.CellBorder), headerBorder = Solid(CustomTablePalette.Current.HeaderBorder);
                Color row0 = Solid(CustomTablePalette.Current.RowBackground), row1 = Solid(CustomTablePalette.Current.AlternatingRowBackground);
                int inner = Px(41d, dpi) - lw - 3;
                for (int dy = 0; dy < lw; dy++)
                {
                    Assert.AreEqual(headerBorder, pixels.GetColor(inner, headerPx - 1 - dy), "見出しの下端の線");
                    Assert.AreEqual(cellBorder, pixels.GetColor(inner, headerPx + rowPx - 1 - dy), "行0の下端の線");
                    Assert.AreEqual(cellBorder, pixels.GetColor(inner, headerPx + 2 * rowPx - 1 - dy), "行1の下端の線");
                    Assert.AreEqual(cellBorder, pixels.GetColor(inner, headerPx + fitRows * rowPx - 1 - dy), "最後の完全表示行の下端の線");
                }
                Assert.AreNotEqual(headerBorder, pixels.GetColor(inner, headerPx - lw - 1), "見出しの線の外側の画素と混色しません。");
                Assert.AreEqual(row0, pixels.GetColor(inner, headerPx + rowPx - lw - 1), "行0の線のすぐ上は行の背景そのもの");
                Assert.AreEqual(row1, pixels.GetColor(inner, headerPx + 2 * rowPx - lw - 1), "行1の線のすぐ上は交互の背景そのもの");
                Assert.AreEqual(row1, pixels.GetColor(inner, headerPx + rowPx), "行1の最初の画素行は交互の背景");
                // 列の境界は物理画素へ丸められ、線は列の右端の内側に揃う。
                foreach (double edgeDip in new[] { 41d, 104d })
                {
                    int edge = Px(edgeDip, dpi);
                    for (int dx = 0; dx < lw; dx++) { Assert.AreEqual(cellBorder, pixels.GetColor(edge - 1 - dx, headerPx + rowPx / 2), "列の右の罫線 " + edgeDip); }
                    Assert.AreEqual(row0, pixels.GetColor(edge - lw - 1, headerPx + 3), "列の罫線のすぐ内側は行の背景そのもの " + edgeDip);
                }

                // 編集欄の上端と高さは、当たり判定と同じ丸めた高さを使う。
                table.HandleKeyDown(Key.Down, ModifierKeys.None);
                Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                TextBox editor = table.Children.OfType<System.Windows.Controls.Canvas>().SelectMany(canvas => canvas.Children.OfType<System.Windows.Controls.TextBox>()).Single();
                Assert.AreEqual(headerPx / dpi, System.Windows.Controls.Canvas.GetTop(editor), 1e-9);
                Assert.AreEqual(rowPx / dpi - CustomTableColumnLayout.GridLineThickness, editor.Height, 1e-9);
                Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));

                // m 行の一覧は全行が収まり、縦スクロールバーを出さない。m+1 行で初めて出し、最大値は1。
                table.ItemsSource = new List<object>(Rows(fitRows));
                table.UpdateLayout();
                Assert.AreEqual(Visibility.Collapsed, vertical.Visibility, "全行が収まる一覧に縦スクロールバーを出しません。");
                Assert.AreEqual(0d, vertical.Maximum);
                table.ItemsSource = new List<object>(Rows(fitRows + 1));
                table.UpdateLayout();
                Assert.AreEqual(Visibility.Visible, vertical.Visibility, "一行でも収まらなければ縦スクロールバーを出します。");
                Assert.AreEqual(1d, vertical.Maximum);
            }
            finally { Release(table); }
        });
    }

    /// <summary>前景・背景・色付きの区間に単色でないブラシ（グラデーション）を渡すと、文字は既定の文字色、セルの背景は描かず行の背景が見え、例外にならないことを確認します。</summary>
    /// <remarks>単色でないブラシで例外にする誤り、背景のグラデーションを描こうとして行の背景を隠す誤りを検出します。</remarks>
    [TestMethod]
    public void NonSolidBrushes_FallBackToDefaultForegroundAndNoCellBackground()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var gradient = new LinearGradientBrush(Colors.Red, Colors.Blue, 0d);
            CustomTableView table = CreateTable(
                [Column("F", 80, text: _ => "MMMM", foreground: _ => gradient),
                    Column("B", 80, text: _ => "MMMM", background: _ => gradient),
                    Column("R", 80, text: _ => "MMMM", runs: _ => [new CustomTableTextRunStyle(0, 4, gradient)]),
                    Column("P", 80, text: _ => "MMMM")],
                Rows(2), 320d, 80d);
            try
            {
                SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);
                // 単色の既定の文字色で描いた列と、全く同じ画素になる（文字は既定の文字色、背景は描かず行の背景）。
                foreach (int column in new[] { 0, 1, 2 })
                {
                    Assert.IsTrue(pixels.RegionEquals(new Int32Rect(column * 80, 20, 79, 37), pixels, 240, 20), "列" + column + " は既定の文字色で描いた列と同じ画素です。");
                }
                Assert.IsTrue(pixels.CountNotEqual(new Int32Rect(240, 22, 70, 15), Solid(CustomTablePalette.Current.RowBackground)) > 0, "既定の文字色で文字を描いています。");
                TestUiDispatcherHost.AssertNoUnhandledExceptions("non-solid-brush");
            }
            finally { Release(table); }
        });
    }
    /// <summary>実際に表示した表を読み込み解除すると表示面の画像と描画先を保持せず、解除の間の変更が再読み込み後の最初の描画へ反映されることを確認します。</summary>
    /// <remarks>読み込み解除のあとも大きな描画先や画像を持ち続ける誤りと、再表示で古い描画を使う誤りを検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void Unloaded_ReleasesTheSurfaceImageAndReloadDrawsTheLatestState()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CustomTableView table = new()
            {
                Width = 200d,
                Height = 100d,
                HeaderHeight = 20d,
                RowHeight = 19d,
                Columns = [Column("A", 120, text: _ => "Row")],
                ItemsSource = Rows(4)
            };
            var window = new Window { Width = 260d, Height = 200d, Content = table };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                Assert.IsNotNull(table.SurfaceImage);
                window.Content = null;
                TestUiDispatcherHost.ProcessQueuedBackground();
                Assert.IsFalse(table.IsLoaded);
                Assert.IsNull(table.SurfaceImage, "読み込み解除のあとは表示面の画像と描画先を保持しません。");
                table.SelectedIndex = 2;
                window.Content = table;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                TestUiDispatcherHost.ProcessQueuedBackground();
                table.UpdateLayout();
                Assert.IsTrue(table.IsLoaded);
                SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                double dpi = VisualTreeHelper.GetDpi(table).PixelsPerDip;
                int y = Px(table.RowGeometry.HeaderHeight + 2.5d * table.RowGeometry.RowHeight, dpi);
                Assert.AreEqual(Solid(CustomTablePalette.Current.SelectedRowBackground), pixels.GetColor(Px(150d, dpi), y), "解除の間の選択の変更が反映されます。");
            }
            finally { window.Content = null; table.ItemsSource = null; window.Close(); }
        });
    }

    /// <summary>テーマを変えると、表を作り直さずに新しい配色の画素になることを確認します。</summary>
    /// <remarks>パレットの更新を描画へ伝えず、古い配色が残る誤りを検出します。共有ホストのテーマを変えるため、元のテーマへ戻します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ThemeChange_RedrawsWithTheNewPaletteWithoutRecreatingTheTable()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string originalTheme = BeMusicSeeker.Properties.Settings.Default.AppearanceTheme;
            CustomTableView table = new()
            {
                Width = 200d,
                Height = 100d,
                HeaderHeight = 20d,
                RowHeight = 19d,
                Columns = [Column("A", 120, text: _ => "Row")],
                ItemsSource = Rows(4)
            };
            var window = new Window { Width = 260d, Height = 200d, Content = table };
            try
            {
                AppThemeService.ApplyTheme(AppThemeService.Light);
                scope.ShowAndWaitForContentRendered(window);
                double dpi = VisualTreeHelper.GetDpi(table).PixelsPerDip;
                int x = Px(150d, dpi), y = Px(table.RowGeometry.HeaderHeight + 0.5d * table.RowGeometry.RowHeight, dpi);
                Color light = CustomTableRenderProbe.Read(table).GetColor(x, y);
                Assert.AreEqual(Solid(CustomTablePalette.Current.RowBackground), light);

                AppThemeService.ApplyTheme(AppThemeService.Dark);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                table.UpdateLayout();
                Color dark = CustomTableRenderProbe.Read(table).GetColor(x, y);
                Assert.AreEqual(Solid(CustomTablePalette.Current.RowBackground), dark, "新しい配色の画素です。");
                Assert.AreNotEqual(light, dark, "ライトとダークで行の背景が違うことが前提です。");
                Assert.AreSame(table, window.Content);
            }
            finally
            {
                AppThemeService.ApplyTheme(originalTheme);
                window.Content = null; table.ItemsSource = null; window.Close();
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }
        });
    }

    /// <summary>
    /// GPU経路の資源の生成（Direct3D 11のデバイス、Direct3D 9Exのデバイス、共有面）が失敗しても、表が例外を出さずにCPU側のビットマップで描かれ、
    /// 100%と175%で背景・罫線・選択・見出し・文字の位置と色が契約どおりで、切替えを診断ログへ一回だけ記録することを確認します。
    /// 再描画してもCPU経路を保ち、ログを増やしません。注入した失敗が使われた（GPU経路を試した）ときだけ、ログが一回あることを求めます。
    /// </summary>
    /// <remarks>
    /// 生成の失敗で表示を失う誤り、例外を表の外へ出す誤り、CPU経路の画素が契約を外れる誤り、切替えを記録しない・繰り返し記録する誤りを検出します。
    /// WPFがソフトウェアで描画している環境ではGPU経路を試さないため、注入は使われず、ログも出ません。
    /// </remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(nameof(CustomTableDisplayFaultPoint.Direct3D11Device), 1d)]
    [DataRow(nameof(CustomTableDisplayFaultPoint.Direct3D9Device), 1d)]
    [DataRow(nameof(CustomTableDisplayFaultPoint.SharedSurface), 1d)]
    [DataRow(nameof(CustomTableDisplayFaultPoint.Direct3D11Device), 1.75d)]
    [DataRow(nameof(CustomTableDisplayFaultPoint.SharedSurface), 1.75d)]
    public async Task GpuCreationFailure_DrawsWithTheCpuBitmapAndLogsTheSwitchOnce(string pointName, double dpi)
    {
        CustomTableDisplayFaultPoint point = Enum.Parse<CustomTableDisplayFaultPoint>(pointName);
        await ApplicationLoggingTestScope.RunAsync(scope =>
        {
            string logPath = scope.ConfigureFileLog();
            var faults = new CustomTableDisplayFaults();
            faults.FailCreation(point);
            TestUiDispatcherHost.Invoke(() =>
            {
                CustomTableView table = CustomTableDisplayPathTestKit.Create(dpi, beforeFirstRender: surface => surface.DisplayFaults = faults);
                try
                {
                    Assert.IsInstanceOfType<WriteableBitmap>(table.SurfaceImage, "生成に失敗した表はCPU側のビットマップで描きます。");
                    CustomTableDisplayPathTestKit.AssertContract(table, CustomTableRenderProbe.Read(table), dpi);
                    CustomTableDisplayPathTestKit.AssertContract(table, CustomTableRenderProbe.Redraw(table), dpi);
                    Assert.IsInstanceOfType<WriteableBitmap>(table.SurfaceImage, "再描画してもCPU経路を保ちます。");
                }
                finally { CustomTableDisplayPathTestKit.Release(table); }
            });
            TestUiDispatcherHost.AssertNoUnhandledExceptions("GPU経路の生成の失敗");
            bool gpuAttempted = faults.RemainingCreationFailures(point) == 0;
            string log = ApplicationLoggingTestScope.ReadFileLog(logPath);
            Assert.AreEqual(gpuAttempted ? 1 : 0, System.Text.RegularExpressions.Regex.Count(log, "custom table display switched to the CPU path"),
                "切替えは表ごとに一度だけ、診断ログへ記録します。");
            return Task.CompletedTask;
        });
    }

    /// <summary>実際の窓の描画方式（<c>HwndTarget.RenderMode</c>）をソフトウェアにすると、その窓の中の表がCPU側のビットマップで描かれ、画素が契約を満たすことを確認します。</summary>
    /// <remarks>WPFがソフトウェアで描画している窓でGPU経路を選び、合成が遅くなる・描けない誤りを検出します。窓は所有する実Windowだけで、プロセスの描画方式は変えません。</remarks>
    [TestMethod]
    public void WindowInSoftwareRenderMode_DrawsWithTheCpuBitmap()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CustomTableView table = CustomTableDisplayPathTestKit.Build();
            var window = new Window { Width = 420d, Height = 200d, Content = table };
            window.SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(window) is HwndSource { CompositionTarget: { } target })
                {
                    target.RenderMode = RenderMode.SoftwareOnly;
                }
            };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                var source = (HwndSource)PresentationSource.FromVisual(window)!;
                Assert.AreEqual(RenderMode.SoftwareOnly, source.CompositionTarget.RenderMode, "窓の描画方式がソフトウェアになっています（前提）。");
                // 最初の描画は窓の描画方式を設定する前に終わり得るため、資源を解放して、ソフトウェアの窓の中で生成し直させる。
                table.ApplyVisibilityChanged(false);
                Assert.IsNull(table.SurfaceImage);
                table.ApplyVisibilityChanged(true);
                table.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsInstanceOfType<WriteableBitmap>(table.SurfaceImage, "ソフトウェアで描画する窓の表はCPU経路です。");
                CustomTableDisplayPathTestKit.AssertContract(table, CustomTableRenderProbe.Read(table), VisualTreeHelper.GetDpi(table).PixelsPerDip);
            }
            finally { CustomTableDisplayPathTestKit.Release(table); }
        });
    }

    /// <summary>
    /// 窓のない表を寸法Aで描いた後に寸法Bで配置し直すと、画像の画素寸法がBになり、右下を含む全面がBを新しく描いた表と画素まで一致し、Aの画素が残らないことを、100%と175%で確認します。
    /// </summary>
    /// <remarks>寸法が変わっても描画先と画像を作り直さない誤り、新しい寸法で全面を描かず古い画素が残る誤り、画像の寸法が倍率とずれる誤りを検出します。経路は製品が選んだものを使い、同じ経路どうしを比べます。</remarks>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.75d)]
    public void Resize_RecreatesTheTargetAndDrawsTheWholeNewSurface(double dpi)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView? table = null;
            CustomTableView? fresh = null;
            try
            {
                table = CustomTableDisplayPathTestKit.Create(dpi);
                fresh = CustomTableDisplayPathTestKit.Create(dpi, width: 360d, height: 130d);
                SurfacePixels small = CustomTableRenderProbe.Read(table);
                table.Width = 360d;
                table.Height = 130d;
                CustomTableRenderProbe.Arrange(table, 360d, 130d);
                SurfacePixels resized = CustomTableRenderProbe.Read(table);
                Assert.AreNotEqual(small.Width, resized.Width, "寸法が変わっています。");
                Assert.AreEqual(Px(360d, dpi), resized.Width, "画像の幅は新しい寸法の物理画素です。");
                Assert.AreEqual(table.RowGeometry.SurfaceHeightPx, resized.Height, "画像の高さは新しい寸法の物理画素です。");
                Assert.IsTrue(resized.SameAs(CustomTableRenderProbe.Read(fresh)), "全面が、新しい寸法で描いた表と画素まで一致します。古い寸法の画素は残りません。");
                Assert.AreEqual(Solid(CustomTablePalette.Current.RowBackground), resized.GetColor(resized.Width - 2, resized.Height - 2), "右下の画素も新しい寸法で描きます。");
            }
            finally
            {
                CustomTableDisplayPathTestKit.Release(fresh);
                CustomTableDisplayPathTestKit.Release(table);
            }
        });
    }

    /// <summary>
    /// 二つの表のうち表Aを非表示にして資源を解放しても、表Bの再描画が新しい状態の正しい画素になり、表Aを再表示すると、非表示の間に変えた最新の状態で描かれることを確認します。
    /// </summary>
    /// <remarks>表どうしで面（描画先・ブラシ・画像）を共有し、片方の面の解放がもう片方の描画を壊す誤り、表の解放で共有のデバイスまで解放する誤りと、再表示で古い状態を描く誤りを検出します。</remarks>
    [TestMethod]
    public void HidingOneTable_DoesNotAffectAnotherTableAndRevealRedrawsTheLatestState()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView? first = null;
            CustomTableView? second = null;
            CustomTableView? expectedSecond = null;
            CustomTableView? expectedFirst = null;
            try
            {
                first = CustomTableDisplayPathTestKit.Create(1d);
                second = CustomTableDisplayPathTestKit.Create(1d, selectedIndex: 2);
                // 比較の対象は、解放を挟まずに同じ操作をした表です。現在セルなどの状態が、初期の選択だけの表とは異なるためです。
                expectedSecond = CustomTableDisplayPathTestKit.Create(1d, selectedIndex: 2);
                expectedFirst = CustomTableDisplayPathTestKit.Create(1d, selectedIndex: 1);
                first.ApplyVisibilityChanged(false);
                Assert.IsNull(first.SurfaceImage, "非表示にした表は画像と描画先を解放します。");
                second.SelectedIndex = 0;
                expectedSecond.SelectedIndex = 0;
                SurfacePixels secondPixels = CustomTableRenderProbe.Redraw(second);
                SurfacePixels expectedSecondPixels = CustomTableRenderProbe.Redraw(expectedSecond);
                Assert.IsTrue(secondPixels.SameAs(expectedSecondPixels), "解放した表の影響を受けず、新しい状態を描きます。" + secondPixels.DescribeDifference(expectedSecondPixels));

                first.SelectedIndex = 2;
                expectedFirst.SelectedIndex = 2;
                first.ApplyVisibilityChanged(true);
                first.UpdateLayout();
                Assert.IsNotNull(first.SurfaceImage, "再表示で描き直します。");
                SurfacePixels firstPixels = CustomTableRenderProbe.Read(first);
                SurfacePixels expectedFirstPixels = CustomTableRenderProbe.Redraw(expectedFirst);
                Assert.IsTrue(firstPixels.SameAs(expectedFirstPixels), "非表示の間に変えた最新の状態で描きます。" + firstPixels.DescribeDifference(expectedFirstPixels));
            }
            finally
            {
                CustomTableDisplayPathTestKit.Release(expectedFirst);
                CustomTableDisplayPathTestKit.Release(expectedSecond);
                CustomTableDisplayPathTestKit.Release(second);
                CustomTableDisplayPathTestKit.Release(first);
            }
        });
    }
}
