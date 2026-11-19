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
        Func<object, IReadOnlyList<CustomTableTextRunStyle>>? runs = null, bool merged = false, Func<object, bool>? enabled = null)
    {
        return new CustomTableColumn(id, header, new CustomTableColumnSettings.ColumnLayout { Width = width, Visibility = Visibility.Visible }, 0, sortMemberPath, alignment,
            text ?? (_ => string.Empty), foregroundSelector: foreground, backgroundSelector: background, cellKind: kind, textStyle: style, checkedSelector: isChecked,
            textRunSelector: runs == null ? null : (row, _) => runs(row), mergesRowGroup: merged, enabledSelector: enabled);
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

    /// <summary>
    /// ダウンロード・状態の各種類・チェックボックスのセルで、前景色の描画がセルの内側にあり、種類ごとに異なり、値のないチェックボックスは何も描かないことを確認します。
    /// ラジオボタンは、選択済みが輪郭と中央の塗り、未選択が輪郭だけ、値なしが何もなしで、チェックボックスとは別の形です。
    /// 操作できないチェックボックスとラジオボタンは、同じ状態でも有効時と異なる画素で描きます。
    /// オンのチェックと選択済みラジオボタンの中央の塗りは、列の前景色（赤）ではなくパレットのチェックの色で、無効は無効色です。
    /// </summary>
    /// <remarks>
    /// アイコンを描かない誤り、種類を取り違えて同じ形を描く誤り、値のないチェックボックスに枠を描く誤りを検出します。
    /// ラジオボタンをチェックボックスの形で描く誤り、選択の有無で中央の塗りが変わらない誤り、値なしに輪郭を描く誤り、
    /// 無効を有効と同じ画素で描く誤り、チェックや中央の塗りへ列の前景色を適用する誤りも検出します。
    /// </remarks>
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
            // 既存の表明が数える列はここまでです。ラジオボタンと無効のセルは後ろへ足し、既存の列の位置を変えません。
            int baseCount = columns.Count;
            columns.Add(Column("RadioOn", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.RadioButton, isChecked: _ => true));
            columns.Add(Column("RadioOff", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.RadioButton, isChecked: _ => false));
            columns.Add(Column("RadioNull", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.RadioButton, isChecked: _ => null));
            columns.Add(Column("CheckOnDisabled", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.CheckBox, isChecked: _ => true, enabled: _ => false));
            columns.Add(Column("CheckOffDisabled", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.CheckBox, isChecked: _ => false, enabled: _ => false));
            columns.Add(Column("RadioOnDisabled", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.RadioButton, isChecked: _ => true, enabled: _ => false));
            columns.Add(Column("RadioOffDisabled", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.RadioButton, isChecked: _ => false, enabled: _ => false));
            columns.Add(Column("RadioNullDisabled", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.RadioButton, isChecked: _ => null, enabled: _ => false));
            int Index(string id) => columns.FindIndex(column => column.Id == id);
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
                for (int first = 0; first < baseCount; first++)
                {
                    for (int second = first + 1; second < baseCount; second++)
                    {
                        Assert.IsFalse(pixels.RegionEquals(Cell(first), pixels, second * 40, 20), columns[first].Id + " と " + columns[second].Id + " は異なる形です。");
                    }
                }
                int on = baseCount - 3, off = baseCount - 2, none = baseCount - 1;
                Color checkColor = Solid(CustomTablePalette.Current.CheckBoxCheck), disabledColor = Solid(CustomTablePalette.Current.CheckBoxDisabledCheck);
                Assert.AreNotEqual(Colors.Red, checkColor, "前提: パレットのチェックの色は列の前景色（赤）と異なります。");
                Assert.AreEqual(0, RedInk(on), "オンのチェックは列の前景色を使いません。");
                Assert.IsTrue(CountColor(pixels, Cell(on), checkColor) > 0, "オンのチェックはパレットのチェックの色で描きます。");
                Assert.AreEqual(0, pixels.CountNotEqual(Cell(none), background), "値のないチェックボックスは何も描きません。");
                Assert.IsTrue(pixels.CountNotEqual(Cell(off), background) > 0, "オフのチェックボックスは枠を描きます。");

                // ラジオボタン。未選択は輪郭だけ、選択済みは中央を前景色で塗り、値なしは何も描かない。チェックボックスとは別の形。
                int radioOn = Index("RadioOn"), radioOff = Index("RadioOff"), radioNull = Index("RadioNull");
                Assert.AreEqual(0, RedInk(radioOn), "選択済みのラジオボタンの中央は列の前景色を使いません。");
                Assert.AreEqual(checkColor, pixels.GetColor(radioOn * 40 + 19, 29), "選択済みのラジオボタンはパレットのチェックの色で中央を塗ります。");
                Assert.AreEqual(0, RedInk(radioOff), "未選択のラジオボタンは中央を塗りません。");
                Assert.IsTrue(pixels.CountNotEqual(Cell(radioOff), background) > 0, "未選択のラジオボタンは輪郭を描きます。");
                Assert.AreEqual(0, pixels.CountNotEqual(Cell(radioNull), background), "値なしのラジオボタンは何も描きません。");
                Assert.IsFalse(pixels.RegionEquals(Cell(radioOn), pixels, radioOff * 40, 20), "選択済みと未選択は異なる形です。");
                Assert.IsFalse(pixels.RegionEquals(Cell(radioOn), pixels, on * 40, 20), "選択済みのラジオボタンはチェックボックスと別の形です。");
                Assert.IsFalse(pixels.RegionEquals(Cell(radioOff), pixels, off * 40, 20), "未選択のラジオボタンはチェックボックスと別の形です（円と四角）。");
                Int32Rect? radioBounds = pixels.FindInkBounds(Cell(radioOff), background);
                Int32Rect? checkBounds = pixels.FindInkBounds(Cell(off), background);
                Assert.IsTrue(radioBounds.HasValue && checkBounds.HasValue
                    && Math.Abs(radioBounds.Value.Width - checkBounds.Value.Width) <= 1 && Math.Abs(radioBounds.Value.Height - checkBounds.Value.Height) <= 1,
                    "ラジオボタンの大きさはチェックボックスと同じです。" + radioBounds + " / " + checkBounds);

                // 無効のセルは、同じ状態の有効のセルと異なる画素で、前景色（赤）を使わない。値なしは無効でも何も描かない。
                foreach ((string enabledId, string disabledId) in new[] { ("On", "CheckOnDisabled"), ("Off", "CheckOffDisabled"), ("RadioOn", "RadioOnDisabled"), ("RadioOff", "RadioOffDisabled") })
                {
                    int enabledColumn = Index(enabledId), disabledColumn = Index(disabledId);
                    Assert.IsTrue(pixels.CountNotEqual(Cell(disabledColumn), background) > 0, disabledId + " も図形を描きます。");
                    Assert.IsFalse(pixels.RegionEquals(Cell(enabledColumn), pixels, disabledColumn * 40, 20), disabledId + " は有効時と異なる画素です。");
                    Assert.AreEqual(0, RedInk(disabledColumn), disabledId + " は前景色を使いません。");
                }
                Assert.AreEqual(disabledColor, pixels.GetColor(Index("RadioOnDisabled") * 40 + 19, 29), "無効の選択済みラジオボタンは無効色で中央を塗ります。");
                Assert.AreEqual(0, pixels.CountNotEqual(Cell(Index("RadioNullDisabled")), background), "値なしのラジオボタンは無効でも何も描きません。");
            }
            finally { Release(table); }
        });
    }

    /// <summary>
    /// 行グループの提供元を持つ表の交互背景が、行番号ではなくグループの序数の偶奇で決まり、偶数は <c>RowBackground</c>、奇数は <c>AlternatingRowBackground</c> になることを確認します。
    /// グループの大きさ [2,1,3,2,1,3,2,1,3] で、行番号と序数の偶奇が食い違う行（2・3・5・6）を含め、選択（行3）と警告（行5・6）が交互色より優先されること、
    /// 奇数の序数のグループが先頭に来るまでスクロールした後（先頭行2）と、グループの途中が先頭に来た後（先頭行5）も、先頭からの序数で決まることを確認します。
    /// </summary>
    /// <remarks>
    /// グループを無視して行番号の偶奇で塗る誤り、表示中の先頭行や画面上の位置から数え直した序数で塗る誤り（スクロールで色が反転する）、
    /// グループ内の行ごとに色を替える誤り、選択・警告の優先を交互色の下へ落とす誤りを検出します。
    /// </remarks>
    [TestMethod]
    public void RowGroups_AlternateBackgroundByGroupOrdinalWithSelectionAndWarningPriority()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            int[] ordinals = [0, 0, 1, 2, 2, 2, 3, 3, 4, 5, 5, 5, 6, 6, 7, 8, 8, 8];
            HashSet<int> warnings = [5, 6];
            var rows = new GroupedRows([2, 1, 3, 2, 1, 3, 2, 1, 3], index => new WarningRow(warnings.Contains(index)));
            CustomTableView table = CreateTable([Column("A", 60), Column("B", 80)], rows, 140d, 20d + 19d * 8d);
            try
            {
                CustomTablePalette palette = CustomTablePalette.Current;
                table.SelectedIndex = 3;
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                foreach (int first in new[] { 0, 2, 5 })
                {
                    vertical.Value = first;
                    SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);
                    Assert.AreEqual(first, table.FirstVisibleRowIndex);
                    int headerPx = table.RowGeometry.HeaderHeightPx, rowPx = table.RowGeometry.RowHeightPx;
                    for (int row = first; row < first + 8; row++)
                    {
                        Brush expected = row == 3 ? palette.SelectedRowBackground
                            : warnings.Contains(row) ? palette.WarningRowBackground
                            : (ordinals[row] & 1) == 1 ? palette.AlternatingRowBackground : palette.RowBackground;
                        Assert.AreEqual(Solid(expected), pixels.GetColor(100, headerPx + (row - first) * rowPx + 3), $"先頭行{first} 行{row}（序数{ordinals[row]}）の背景");
                    }
                }
            }
            finally { Release(table); }
        });
    }

    /// <summary>
    /// グループの全行にまたがる結合列（文字・チェックボックス）が、グループごとに一つのセルとして描かれることを、倍率1.0と1.25で確認します。
    /// グループの内部の境界には横罫線がなく（通常の列には罫線がある）、グループの下端には罫線があり、右の縦罫線は途切れません。
    /// 内容は一度だけ、グループの範囲のうち表示中の部分の縦中央（±1画素）に描かれます。先頭行が画面外のとき、1000行のグループの中ほどが先頭のとき、
    /// グループが表示行数より大きく最後の行が部分表示のときも同じです。選択と現在セルの強調は <see cref="MergedColumns_PaintSelectionAndCurrentCellOverTheWholeMergedCell"/> が確かめます。
    /// </summary>
    /// <remarks>
    /// 行ごとに内容を描く誤り、内部の罫線を引く誤り、下端や縦罫線を落とす誤り、グループの先頭行を基準に中央を決めて先頭行が画面外だと位置がずれる誤り、
    /// グループ全体の高さ（画面外を含む）の中央に描く誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    public void RowGroups_MergedColumnsDrawOneCellPerGroupCenteredOnTheVisiblePart(double dpi)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var rows = new GroupedRows([3, 2, 1000, 5]);
            // 7行と、10 DIPだけ見える8行目。
            CustomTableView table = CreateTable(
                [Column("A", 60), Column("T", 60, text: _ => "H", foreground: _ => Brushes.Red, merged: true),
                    Column("K", 40, foreground: _ => Brushes.Red, kind: CustomTableCellKind.CheckBox, isChecked: _ => true, merged: true)],
                rows, 180d, 20d + 19d * 7d + 10d, dpi);
            try
            {
                CustomTablePalette palette = CustomTablePalette.Current;
                Color cellBorder = Solid(palette.CellBorder);
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                int gl = CustomTablePixelGeometry.GridLineWidth(dpi);
                (int Left, int Right) T = (Px(60d, dpi), Px(120d, dpi)), K = (Px(120d, dpi), Px(160d, dpi));

                // slotCount行のグループの部分（先頭は表示中の slot 番目の行）を確かめる。bottomBorder は、グループの下端が完全に見えるか。
                void AssertSegment(SurfacePixels pixels, int firstSlot, int slotCount, bool bottomBorder, Color background, string label)
                {
                    int headerPx = table.RowGeometry.HeaderHeightPx, rowPx = table.RowGeometry.RowHeightPx, surfacePx = table.RowGeometry.SurfaceHeightPx;
                    int top = headerPx + firstSlot * rowPx;
                    int bottom = Math.Min(surfacePx, top + slotCount * rowPx);
                    int interiorBottom = bottomBorder ? bottom - gl : bottom;
                    foreach ((string name, (int Left, int Right) slot) in new[] { ("T", T), ("K", K) })
                    {
                        for (int slotIndex = firstSlot; slotIndex < firstSlot + slotCount; slotIndex++)
                        {
                            int lineTop = headerPx + (slotIndex + 1) * rowPx - gl;
                            if (lineTop + gl > surfacePx) { continue; }
                            bool isBottom = slotIndex == firstSlot + slotCount - 1;
                            Color expected = isBottom && bottomBorder ? cellBorder : background;
                            for (int y = lineTop; y < lineTop + gl; y++)
                            {
                                Assert.AreEqual(expected, pixels.GetColor(slot.Left, y), $"{label} {name} スロット{slotIndex}の下端の罫線 y={y}");
                                Assert.AreEqual(expected, pixels.GetColor(slot.Right - gl - 1, y), $"{label} {name} スロット{slotIndex}の下端の罫線 右 y={y}");
                                if (!isBottom || !bottomBorder)
                                {
                                    // 通常の列は、同じ位置に罫線がある。
                                    Assert.AreEqual(cellBorder, pixels.GetColor(5, y), $"{label} 通常の列の罫線 y={y}");
                                }
                            }
                        }
                        for (int y = top; y < bottom; y++)
                        {
                            Assert.AreEqual(cellBorder, pixels.GetColor(slot.Right - 1, y), $"{label} {name} 右の縦罫線 y={y}");
                        }
                        var area = new Int32Rect(slot.Left, top, slot.Right - gl - slot.Left, interiorBottom - top);
                        Int32Rect? ink = pixels.FindInkBounds(area, background);
                        Assert.IsNotNull(ink, $"{label} {name} の内容");
                        double expectedCenter = top + (interiorBottom - top) / 2d;
                        Assert.IsTrue(Math.Abs(ink.Value.Y + ink.Value.Height / 2d - expectedCenter) <= 1d,
                            $"{label} {name} の内容が表示中の部分の縦中央 {ink.Value.Y + ink.Value.Height / 2d} / {expectedCenter}");
                        Assert.IsTrue(ink.Value.Height <= Math.Max(rowPx, Px(14d, dpi)), $"{label} {name} の内容は一度だけ（高さ{ink.Value.Height}）");
                    }
                }

                Color row = Solid(palette.RowBackground), alternating = Solid(palette.AlternatingRowBackground);
                SurfacePixels first = CustomTableRenderProbe.Read(table);
                AssertSegment(first, 0, 3, true, row, "先頭");
                AssertSegment(first, 3, 2, true, alternating, "二番目");
                AssertSegment(first, 5, 3, false, row, "最後（部分表示）");

                // グループの先頭行が画面外。
                vertical.Value = 1;
                AssertSegment(CustomTableRenderProbe.Redraw(table), 0, 2, true, row, "先頭行が画面外");

                // 1000行のグループの中ほどが先頭。表示中の全行がグループの内部で、罫線も下端もない。
                vertical.Value = 100;
                SurfacePixels middle = CustomTableRenderProbe.Redraw(table);
                AssertSegment(middle, 0, 8, false, row, "1000行のグループの中ほど");
            }
            finally { Release(table); }
        });
    }

    /// <summary>指定の矩形で、指定の色と完全に一致する画素の数を返します。</summary>
    private static int CountColor(SurfacePixels pixels, Int32Rect area, Color color)
    {
        int count = 0;
        for (int y = Math.Max(0, area.Y); y < Math.Min(pixels.Height, area.Y + area.Height); y++)
        {
            for (int x = Math.Max(0, area.X); x < Math.Min(pixels.Width, area.X + area.Width); x++)
            {
                if (pixels.GetColor(x, y) == color) { count++; }
            }
        }
        return count;
    }

    /// <summary>表の実座標からヒットを作り、本番のセル押下処理へ渡します。物理入力に依存しません。</summary>
    private static void PressAt(CustomTableView table, double x, double y, ModifierKeys modifiers = ModifierKeys.None)
    {
        var point = new Point(x, y);
        CustomTableHitTestResult hit = table.HitTestTable(point);
        Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
        Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, modifiers, 1, point));
    }

    /// <summary>
    /// チェックボックスのチェックとラジオボタンの中央の塗りが、行の選択・現在セルのどちらでも、列の前景色や強調の文字色ではなくパレットのチェックの色（無効は無効色）で描かれ、
    /// 枠の内側が未選択のときと画素単位で一致することを確認します。行グループの提供元の有無（結合列のチェックを含む）と、有効・無効を切り替えます。
    /// 前提として、選択時・現在セルの文字色がチェックの色と異なることを表明します（同じなら検出できないため）。
    /// </summary>
    /// <remarks>
    /// 現在セルや選択時の文字色を印へ使う誤り（ライト系のテーマでラジオボタンの中央の塗りが背景と同化して消える）、結合列だけ別の色で描く誤り、
    /// 提供元の有無で色を変える誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CheckAndRadioMarks_UseThePaletteColorInEverySelectionState(bool grouped)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTablePalette palette = CustomTablePalette.Current;
            Color checkColor = Solid(palette.CheckBoxCheck), disabledColor = Solid(palette.CheckBoxDisabledCheck);
            Assert.AreNotEqual(Solid(palette.SelectedForeground), checkColor, "前提: 選択時の文字色はチェックの色と異なります。");
            Assert.AreNotEqual(Solid(palette.CurrentCellForeground), checkColor, "前提: 現在セルの文字色はチェックの色と異なります。");
            Assert.AreNotEqual(Solid(palette.SelectedForeground), disabledColor, "前提: 選択時の文字色は無効色と異なります。");
            Assert.AreNotEqual(Solid(palette.CurrentCellForeground), disabledColor, "前提: 現在セルの文字色は無効色と異なります。");
            foreach (bool enabled in new[] { true, false })
            {
                var columns = new List<CustomTableColumn>
                {
                    Column("A", 60, text: _ => "a"),
                    Column("Check", 40, kind: CustomTableCellKind.CheckBox, isChecked: _ => true, enabled: _ => enabled),
                    Column("Radio", 40, kind: CustomTableCellKind.RadioButton, isChecked: _ => true, enabled: _ => enabled)
                };
                if (grouped)
                {
                    columns.Add(Column("MergedCheck", 40, kind: CustomTableCellKind.CheckBox, isChecked: _ => true, enabled: _ => enabled, merged: true));
                }
                System.Collections.IList rows = grouped ? new GroupedRows([2, 2]) : Rows(4);
                CustomTableView table = CreateTable(columns, rows, 60d + 40d * (columns.Count - 1), 20d + 19d * 4d);
                try
                {
                    SurfacePixels baseline = CustomTableRenderProbe.Read(table);
                    Color rowBackground = Solid(palette.RowBackground);
                    int headerPx = table.RowGeometry.HeaderHeightPx, rowPx = table.RowGeometry.RowHeightPx;
                    double RowCenterY(int row) => table.HeaderHeight + (row + 0.5d) * table.RowHeight;
                    for (int column = 1; column < columns.Count; column++)
                    {
                        string id = columns[column].Id;
                        bool merged = columns[column].MergesRowGroup;
                        double left = 60d + 40d * (column - 1);
                        var area = new Int32Rect((int)left, headerPx, 39, merged ? rowPx * 2 - 1 : rowPx - 1);
                        Int32Rect? bounds = baseline.FindInkBounds(area, rowBackground);
                        Assert.IsNotNull(bounds, id + " は未選択でも描かれます。");
                        int centerX = bounds.Value.X + bounds.Value.Width / 2, centerY = bounds.Value.Y + bounds.Value.Height / 2;
                        var inside = new Int32Rect(centerX - 4, centerY - 4, 8, 8);
                        bool isRadio = columns[column].CellKind == CustomTableCellKind.RadioButton;
                        Color expected = enabled ? checkColor : disabledColor;
                        int baselineInk = CountColor(baseline, inside, expected);
                        Assert.IsTrue(baselineInk > 0, id + " の印は期待の色で描かれます（未選択）。");

                        void AssertState(SurfacePixels pixels, string state)
                        {
                            string label = $"{id} 有効={enabled} 提供元={grouped} {state}";
                            if (isRadio)
                            {
                                Assert.AreEqual(expected, pixels.GetColor(centerX, centerY), label + " 中央の塗り");
                            }
                            Assert.AreEqual(baselineInk, CountColor(pixels, inside, expected), label + " 印の画素数");
                            if (enabled)
                            {
                                Assert.IsTrue(baseline.RegionEquals(inside, pixels, inside.X, inside.Y), label + " 枠の内側は未選択と画素単位で一致");
                            }
                        }

                        PressAt(table, 30d, RowCenterY(0));
                        AssertState(CustomTableRenderProbe.Redraw(table), "行の選択");
                        PressAt(table, left + 20d, RowCenterY(0));
                        Assert.IsTrue(table.IsCurrentCell(0, columns[column]), id + " が現在セル");
                        AssertState(CustomTableRenderProbe.Redraw(table), "現在セル");
                    }
                }
                finally { Release(table); }
            }
        });
    }

    /// <summary>
    /// 行グループの結合列が、全行が選択されたグループだけ結合セル全体を選択色で塗り、一部だけが選択されたグループは交互色のままで、現在セルが結合列にあれば結合セル全体を現在セルの強調で塗ることを、
    /// 倍率1.0と1.25で確認します。全選択は画面外の行を含めて判定し（表示中の行だけを選んだグループは塗らず、画面外を含め全行を選ぶと塗る）、行の境目に隙間を作りません。
    /// 結合列以外は行ごとの選択・現在セルのままで、下端の罫線と右の縦罫線は残り、文字は塗りと同じ色で消えません。
    /// </summary>
    /// <remarks>
    /// 結合列へ行ごとの選択色を塗る誤り、表示中の行だけで全選択と判定する誤り、一部選択でも塗る誤り、現在セルを一行分だけ塗る誤り、
    /// 行の境目の隙間、罫線を消す誤り、文字が塗りと同色で消える誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    public void MergedColumns_PaintSelectionAndCurrentCellOverTheWholeMergedCell(double dpi)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            // グループは [4, 3, 40] 行。表示は、見出しと7行が物理画素でちょうど収まる高さ。
            var rows = new GroupedRows([4, 3, 40], index => new IndexedRow(index));
            CustomTableView table = CreateTable(
                [Column("A", 60, text: _ => "a"), Column("T", 60, text: _ => "H", merged: true),
                    Column("K", 40, kind: CustomTableCellKind.CheckBox, isChecked: _ => true, merged: true)],
                rows, 160d, (Px(20d, dpi) + 7 * Px(19d, dpi)) / dpi, dpi);
            try
            {
                CustomTablePalette palette = CustomTablePalette.Current;
                Color rowColor = Solid(palette.RowBackground), alternating = Solid(palette.AlternatingRowBackground), selectedColor = Solid(palette.SelectedRowBackground);
                Color currentColor = Solid(palette.CurrentCellBackground), cellBorder = Solid(palette.CellBorder);
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                int gl = CustomTablePixelGeometry.GridLineWidth(dpi);
                (int Left, int Right) A = (0, Px(60d, dpi)), T = (Px(60d, dpi), Px(120d, dpi)), K = (Px(120d, dpi), Px(160d, dpi));
                int headerPx = table.RowGeometry.HeaderHeightPx, rowPx = table.RowGeometry.RowHeightPx;
                double SlotCenterY(int slot) => table.HeaderHeight + (slot + 0.5d) * table.RowHeight;

                // 結合列の表示中の部分 [firstSlot, firstSlot+slotCount) の全画素が fill で、下端の罫線と右の縦罫線が残ることを確かめる。
                void AssertMergedFill(SurfacePixels pixels, (int Left, int Right) slot, string name, int firstSlot, int slotCount, bool bottomBorder, Color fill, string label)
                {
                    int top = headerPx + firstSlot * rowPx;
                    int bottom = Math.Min(table.RowGeometry.SurfaceHeightPx, top + slotCount * rowPx);
                    int interiorBottom = bottomBorder ? bottom - gl : bottom;
                    for (int y = top; y < interiorBottom; y++)
                    {
                        Assert.AreEqual(fill, pixels.GetColor(slot.Left, y), $"{label} {name} 左端 y={y}（行の境目に隙間がない）");
                        Assert.AreEqual(fill, pixels.GetColor(slot.Right - gl - 1, y), $"{label} {name} 右端の内側 y={y}");
                        Assert.AreEqual(cellBorder, pixels.GetColor(slot.Right - 1, y), $"{label} {name} 右の縦罫線 y={y}");
                    }
                    if (bottomBorder)
                    {
                        for (int y = interiorBottom; y < bottom; y++)
                        {
                            Assert.AreEqual(cellBorder, pixels.GetColor(slot.Left, y), $"{label} {name} 下端の罫線 y={y}");
                        }
                    }
                }

                // 行 slot の通常の列（A）の色。
                void AssertRowColumn(SurfacePixels pixels, int slot, Color expected, string label)
                    => Assert.AreEqual(expected, pixels.GetColor(A.Left + 2, headerPx + slot * rowPx + 2), $"{label} 通常の列 スロット{slot}");

                // 文字「H」が塗りと違う色で描かれていることを確かめる（消えていない）。
                void AssertTextVisible(SurfacePixels pixels, int firstSlot, int slotCount, bool bottomBorder, Color fill, Color expectedInk, string label)
                {
                    int top = headerPx + firstSlot * rowPx;
                    int bottom = Math.Min(table.RowGeometry.SurfaceHeightPx, top + slotCount * rowPx);
                    var area = new Int32Rect(T.Left, top, T.Right - gl - T.Left, (bottomBorder ? bottom - gl : bottom) - top);
                    Assert.IsTrue(pixels.CountNotEqual(area, fill) > 0, $"{label} 文字が塗りと同じ色で消えていない");
                    // 小数の倍率では文字の輪郭の混色で最頻の色が僅かにずれるため、各成分16以内で一致を見る。
                    Color ink = pixels.DominantInkColor(area, fill);
                    Assert.IsTrue(Math.Abs(ink.R - expectedInk.R) <= 16 && Math.Abs(ink.G - expectedInk.G) <= 16 && Math.Abs(ink.B - expectedInk.B) <= 16,
                        $"{label} 文字の色 期待={expectedInk} 実際={ink}");
                }

                // 1. 全選択（グループ0）で現在セルが結合列以外。
                PressAt(table, 90d, SlotCenterY(1));
                Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
                Assert.IsTrue(table.IsCurrentCell(1, table.VisibleColumns[0]));
                SurfacePixels p1 = CustomTableRenderProbe.Redraw(table);
                AssertMergedFill(p1, T, "T", 0, 4, true, selectedColor, "全選択・現在セルが結合列以外");
                AssertMergedFill(p1, K, "K", 0, 4, true, selectedColor, "全選択・現在セルが結合列以外");
                AssertTextVisible(p1, 0, 4, true, selectedColor, Solid(palette.SelectedForeground), "全選択・現在セルが結合列以外");
                AssertRowColumn(p1, 0, selectedColor, "全選択・現在セルが結合列以外");
                AssertRowColumn(p1, 1, currentColor, "全選択・現在セルが結合列以外");
                AssertRowColumn(p1, 3, selectedColor, "全選択・現在セルが結合列以外");
                AssertRowColumn(p1, 4, alternating, "全選択・現在セルが結合列以外");

                // 2. 一部選択（グループ1の行5だけ）。結合列は交互色のまま、通常の列は行ごと。
                PressAt(table, 30d, SlotCenterY(5));
                SurfacePixels p2 = CustomTableRenderProbe.Redraw(table);
                AssertMergedFill(p2, T, "T", 4, 3, true, alternating, "一部選択");
                AssertMergedFill(p2, K, "K", 4, 3, true, alternating, "一部選択");
                AssertMergedFill(p2, T, "T", 0, 4, true, rowColor, "一部選択 グループ0");
                AssertRowColumn(p2, 4, alternating, "一部選択");
                AssertRowColumn(p2, 5, currentColor, "一部選択");
                AssertRowColumn(p2, 6, alternating, "一部選択");

                // 3. 先頭行が画面外のまま、表示中の行だけを選ぶ（グループ2は行7〜46、表示は行9〜15）と塗らず、画面外を含め全行を選ぶと塗る。
                vertical.Value = 9;
                CustomTableRenderProbe.Redraw(table);
                Assert.AreEqual(9, table.FirstVisibleRowIndex);
                table.SelectRowsByPredicate(row => ((IndexedRow)row).Index is >= 9 and <= 15, row => ((IndexedRow)row).Index == 9);
                Assert.AreEqual(9, table.FirstVisibleRowIndex, "選択で表示位置が動かない前提");
                SurfacePixels p3 = CustomTableRenderProbe.Redraw(table);
                AssertMergedFill(p3, T, "T", 0, 7, false, rowColor, "表示中の行だけを選択");
                AssertMergedFill(p3, K, "K", 0, 7, false, rowColor, "表示中の行だけを選択");
                AssertRowColumn(p3, 3, selectedColor, "表示中の行だけを選択");
                table.SelectRowsByPredicate(row => ((IndexedRow)row).Index is >= 7 and <= 46, row => ((IndexedRow)row).Index == 9);
                Assert.AreEqual(9, table.FirstVisibleRowIndex, "選択で表示位置が動かない前提");
                SurfacePixels p3b = CustomTableRenderProbe.Redraw(table);
                AssertMergedFill(p3b, T, "T", 0, 7, false, selectedColor, "画面外を含め全行を選択");
                AssertMergedFill(p3b, K, "K", 0, 7, false, selectedColor, "画面外を含め全行を選択");
                vertical.Value = 0;
                CustomTableRenderProbe.Redraw(table);

                // 4. 全選択で現在セルが結合列。結合セル全体が現在セルの強調、他の結合列は選択色。
                PressAt(table, 90d, SlotCenterY(1));
                SurfacePixels p4 = CustomTableRenderProbe.Redraw(table);
                Assert.IsTrue(table.IsCurrentCell(1, table.VisibleColumns[1]));
                AssertMergedFill(p4, T, "T", 0, 4, true, currentColor, "全選択・現在セルが結合列");
                AssertMergedFill(p4, K, "K", 0, 4, true, selectedColor, "全選択・現在セルが結合列");
                AssertTextVisible(p4, 0, 4, true, currentColor, Solid(palette.CurrentCellForeground), "全選択・現在セルが結合列");
                AssertRowColumn(p4, 1, selectedColor, "全選択・現在セルが結合列");

                // 5. 一部選択で現在セルが結合列。結合セル全体が現在セルの強調、他の結合列は交互色（グループ0は行の背景）のまま。
                table.SelectRowsByPredicate(row => ((IndexedRow)row).Index is 0 or 1, row => ((IndexedRow)row).Index == 0);
                Assert.IsTrue(table.IsCurrentCell(0, table.VisibleColumns[1]), "前提: 現在セルは結合列のまま");
                SurfacePixels p5 = CustomTableRenderProbe.Redraw(table);
                AssertMergedFill(p5, T, "T", 0, 4, true, currentColor, "一部選択・現在セルが結合列");
                AssertMergedFill(p5, K, "K", 0, 4, true, rowColor, "一部選択・現在セルが結合列");
                AssertRowColumn(p5, 1, selectedColor, "一部選択・現在セルが結合列");
                AssertRowColumn(p5, 2, rowColor, "一部選択・現在セルが結合列");
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

    /// <summary>行の位置を持つ行です。</summary>
    private sealed class IndexedRow(int index)
    {
        internal int Index { get; } = index;
    }

    /// <summary>警告の強調を持つ行です。</summary>
    private sealed class WarningRow(bool highlighted)
    {
        public bool HasHighlightedWarning { get; } = highlighted;
    }

    /// <summary>グループの大きさの並びから、連続したグループを返す行集合です。</summary>
    private sealed class GroupedRows : System.Collections.ObjectModel.Collection<object>, ICustomTableRowGroupSource
    {
        private readonly int[] starts;

        internal GroupedRows(int[] sizes, Func<int, object>? factory = null)
        {
            starts = new int[sizes.Length];
            int total = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                starts[i] = total;
                total += sizes[i];
            }
            for (int i = 0; i < total; i++) { Add(factory?.Invoke(i) ?? new object()); }
        }

        public CustomTableRowGroup GetRowGroup(int rowIndex)
        {
            int ordinal = Array.BinarySearch(starts, rowIndex);
            if (ordinal < 0) { ordinal = ~ordinal - 1; }
            int end = ordinal + 1 < starts.Length ? starts[ordinal + 1] : Count;
            return new CustomTableRowGroup(starts[ordinal], end - starts[ordinal], ordinal);
        }
    }
}
