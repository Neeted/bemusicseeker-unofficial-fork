using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// 一覧の表示経路（GPU合成とCPU側のビットマップ）の検証が共有する、窓のない標準の表と画素の表明です。
/// 表は赤・青・色付きの区間・見出し・選択行・罫線を持ち、色の入替え、位置のずれ、不透明でない画素を検出できます。
/// 作った表は、必ず <see cref="Release"/> で解放します。
/// </summary>
internal static class CustomTableDisplayPathTestKit
{
    /// <summary>表の幅（DIP）です。</summary>
    internal const double TableWidth = 320d;

    /// <summary>表の高さ（DIP）です。</summary>
    internal const double TableHeight = 100d;

    private const double HeaderHeight = 20d;
    private const double RowHeight = 19d;
    private static readonly int[] ColumnEdges = [0, 120, 220, 300];

    private static Color Solid(Brush brush) => ((SolidColorBrush)brush).Color;

    private static Color Solid(Pen pen) => Solid(pen.Brush);

    /// <summary>
    /// 標準の表を作り、窓なしで配置して描きます。<paramref name="beforeFirstRender"/> は、最初の描画の前に表示面へ故障注入などを設定します。
    /// 列Aは左寄せの赤い「MMMM」、列Bは右寄せの青い「WWWW」、列Cは前半が赤・後半が青の「RRBB」で、見出しは「HEAD」「BODY」「MIXED」です。行は三つで、選択行を指定できます。
    /// <paramref name="valueFetches"/> を渡すと、列の表示値の取得関数が呼ばれた回数を数えます。
    /// </summary>
    internal static CustomTableView Create(double dpi, int selectedIndex = 1, Action<CustomTableSurface>? beforeFirstRender = null,
        double width = TableWidth, double height = TableHeight, StrongBox<int>? valueFetches = null)
    {
        CustomTableView table = Build(selectedIndex, width, height, valueFetches);
        try
        {
            CustomTableRenderProbe.SetDpi(table, dpi);
            beforeFirstRender?.Invoke(CustomTableRenderProbe.Surface(table));
            CustomTableRenderProbe.Arrange(table, width, height);
            table.ApplyVisibilityChanged(true);
            table.UpdateLayout();
            return table;
        }
        catch
        {
            // 呼出し元へ表を返せないため、最初の描画で作った面をここで解放する。
            Release(table);
            throw;
        }
    }

    /// <summary>標準の表を作ります。配置も描画もしないため、実際の窓へ置く場合に使います。内容は <see cref="Create"/> と同じです。</summary>
    internal static CustomTableView Build(int selectedIndex = 1, double width = TableWidth, double height = TableHeight, StrongBox<int>? valueFetches = null)
    {
        CustomTableColumn[] columns =
        [
            CreateColumn("A", 120, TextAlignment.Left, "HEAD", "MMMM", Brushes.Red, null, valueFetches),
            CreateColumn("B", 100, TextAlignment.Right, "BODY", "WWWW", Brushes.Blue, null, valueFetches),
            CreateColumn("C", 80, TextAlignment.Left, "MIXED", "RRBB", Brushes.Black, [new CustomTableTextRunStyle(0, 2, Brushes.Red), new CustomTableTextRunStyle(2, 2, Brushes.Blue)], valueFetches)
        ];
        return new CustomTableView
        {
            Width = width,
            Height = height,
            RowHeight = RowHeight,
            HeaderHeight = HeaderHeight,
            Columns = [.. columns],
            ItemsSource = new object[] { new object(), new object(), new object() },
            SelectedIndex = selectedIndex
        };
    }

    /// <summary>
    /// 表を非表示にして <c>ItemsSource</c> をnullにし、表示面の資源を解放します。
    /// 複数の表を <c>try</c> の中で順に作るケースが、生成の失敗でnullのままの変数も <c>finally</c> でそのまま渡せるよう、nullには何もしません。
    /// </summary>
    internal static void Release(CustomTableView? table)
    {
        if (table is null)
        {
            return;
        }
        table.ApplyVisibilityChanged(false);
        table.ItemsSource = null;
    }

    /// <summary>標準の表の寸法・背景・罫線・選択・見出し・文字の色と位置が、物理画素で契約を満たすことを表明します。</summary>
    internal static void AssertContract(CustomTableView table, SurfacePixels pixels, double dpi)
    {
        CustomTablePalette palette = CustomTablePalette.Current;
        CustomTableRowGeometry geometry = table.RowGeometry;
        int pad = CustomTablePixelGeometry.TextPadding(dpi);
        Assert.AreEqual(CustomTablePixelGeometry.ToPixels(TableWidth, dpi), pixels.Width, "画像の幅は表の幅の物理画素です。");
        Assert.AreEqual(geometry.SurfaceHeightPx, pixels.Height, "画像の高さは表示面の高さの物理画素です。");

        int headerMiddle = geometry.HeaderHeightPx / 2;
        Assert.AreEqual(Solid(palette.HeaderBorder), pixels.GetColor(Px(10d, dpi), 0), "見出しの上端の線");
        Assert.AreEqual(Solid(palette.HeaderBorder), pixels.GetColor(Px(10d, dpi), geometry.HeaderHeightPx - 1), "見出しの下端の線");
        Assert.AreEqual(Solid(palette.HeaderBackground), pixels.GetColor(Px(5d, dpi), headerMiddle), "見出しの背景");
        Assert.AreEqual(Solid(palette.HeaderBorder), pixels.GetColor(Px(120d, dpi) - 1, headerMiddle), "見出しの列の縦罫線");

        int RowMiddle(int row) => geometry.HeaderHeightPx + row * geometry.RowHeightPx + geometry.RowHeightPx / 2;
        int emptyX = Px(110d, dpi);
        Assert.AreEqual(Solid(palette.RowBackground), pixels.GetColor(emptyX, RowMiddle(0)), "行0の背景");
        Assert.AreEqual(Solid(palette.SelectedRowBackground), pixels.GetColor(emptyX, RowMiddle(1)), "選択行の背景");
        Assert.AreEqual(Solid(palette.RowBackground), pixels.GetColor(emptyX, RowMiddle(2)), "行2の背景");
        Assert.AreEqual(Solid(palette.CellBorder), pixels.GetColor(Px(50d, dpi), geometry.HeaderHeightPx + geometry.RowHeightPx - 1), "行0の下端の罫線");
        Assert.AreEqual(Solid(palette.CellBorder), pixels.GetColor(Px(120d, dpi) - 1, RowMiddle(0)), "行0の列Aの縦罫線");
        Assert.AreEqual(Solid(palette.RowBackground), pixels.GetColor(pixels.Width - 2, RowMiddle(0)), "列の外の行の背景");

        Int32Rect[] interiors = CellInteriors(table, dpi).Where(rect => rect.Y >= geometry.HeaderHeightPx).Take(3).ToArray();
        Color rowBackground = Solid(palette.RowBackground);
        for (int column = 0; column < 3; column++)
        {
            Int32Rect cell = interiors[column];
            Int32Rect? ink = pixels.FindInkBounds(cell, rowBackground);
            Assert.IsNotNull(ink, "行0の列" + column + "に文字があります。");
            Assert.IsTrue(ink.Value.Y >= cell.Y && ink.Value.Y + ink.Value.Height <= cell.Y + cell.Height, "文字は行の内側にあります。列" + column);
            double verticalCenter = ink.Value.Y + ink.Value.Height / 2d;
            Assert.IsTrue(Math.Abs(verticalCenter - (cell.Y + cell.Height / 2d)) <= 2d * dpi, "文字は行の上下中央にあります。列" + column);
        }
        Int32Rect leftInk = pixels.FindInkBounds(interiors[0], rowBackground)!.Value;
        Assert.IsTrue(leftInk.X >= interiors[0].X + pad && leftInk.X <= interiors[0].X + pad + 3d * dpi, "左寄せの文字は左の余白から始まります。");
        Int32Rect rightInk = pixels.FindInkBounds(interiors[1], rowBackground)!.Value;
        double rightGap = interiors[1].X + interiors[1].Width - pad - (rightInk.X + rightInk.Width);
        Assert.IsTrue(rightGap >= 0d && rightGap <= 3d * dpi, "右寄せの文字は右の余白で終わります。");

        (int red, int blue) = pixels.CountDominant(interiors[0]);
        Assert.IsTrue(red > 0 && blue == 0, "列Aは赤い文字だけです。 red=" + red + " blue=" + blue);
        (red, blue) = pixels.CountDominant(interiors[1]);
        Assert.IsTrue(blue > 0 && red == 0, "列Bは青い文字だけです。 red=" + red + " blue=" + blue);
        Int32Rect mixed = interiors[2];
        (red, blue) = pixels.CountDominant(mixed);
        Assert.IsTrue(red > 0 && blue > 0, "列Cは赤と青の区間を持ちます。");
        Int32Rect mixedInk = pixels.FindInkBounds(mixed, rowBackground)!.Value;
        int middle = mixedInk.X + mixedInk.Width / 2;
        Int32Rect first = new(mixed.X, mixed.Y, middle - mixed.X, mixed.Height);
        Int32Rect second = new(middle, mixed.Y, mixed.X + mixed.Width - middle, mixed.Height);
        Assert.IsTrue(pixels.CountDominant(first).Red > pixels.CountDominant(first).Blue, "色付きの区間の前半は赤です。");
        Assert.IsTrue(pixels.CountDominant(second).Blue > pixels.CountDominant(second).Red, "色付きの区間の後半は青です。");

        Int32Rect headerInterior = CellInteriors(table, dpi)[0];
        Assert.IsTrue(pixels.CountNotEqual(headerInterior, Solid(palette.HeaderBackground)) > 0, "見出しの文字を描きます。");
    }

    /// <summary>
    /// 二つの画素列が、同じ状態を描いた結果として同等であることを表明します。文字のセルの外（背景・罫線・選択・見出しの線）は画素が完全に一致し、
    /// 文字のセルは外接矩形が各辺±1画素以内で、赤と青の有無が一致します。字形のアンチエイリアスの画素単位の差だけを許容します。
    /// </summary>
    internal static void AssertEquivalent(SurfacePixels actual, SurfacePixels expected, CustomTableView table, double dpi)
    {
        Assert.AreEqual(expected.Width, actual.Width);
        Assert.AreEqual(expected.Height, actual.Height);
        Int32Rect[] interiors = [.. CellInteriors(table, dpi)];
        bool InsideAnyCell(int x, int y) => interiors.Any(rect => x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (!InsideAnyCell(x, y))
                {
                    Assert.AreEqual(expected.GetColor(x, y), actual.GetColor(x, y), $"文字のセルの外は完全に一致します。x={x}, y={y}");
                }
            }
        }
        foreach (Int32Rect cell in interiors)
        {
            Color background = expected.GetColor(cell.X, cell.Y);
            Assert.AreEqual(background, actual.GetColor(cell.X, cell.Y), "セルの背景が一致します。" + cell);
            Int32Rect? expectedInk = expected.FindInkBounds(cell, background);
            Int32Rect? actualInk = actual.FindInkBounds(cell, background);
            Assert.AreEqual(expectedInk.HasValue, actualInk.HasValue, "文字の有無が一致します。" + cell);
            if (expectedInk is not { } e || actualInk is not { } a)
            {
                continue;
            }
            Assert.IsTrue(Math.Abs(e.X - a.X) <= 1 && Math.Abs(e.Y - a.Y) <= 1
                && Math.Abs(e.X + e.Width - (a.X + a.Width)) <= 1 && Math.Abs(e.Y + e.Height - (a.Y + a.Height)) <= 1,
                $"文字の外接矩形が±1画素以内です。期待={e} 実際={a} セル={cell}");
            (int expectedRed, int expectedBlue) = expected.CountDominant(cell);
            (int actualRed, int actualBlue) = actual.CountDominant(cell);
            Assert.AreEqual(expectedRed > 0, actualRed > 0, $"赤い文字の有無が一致します。セル={cell}");
            Assert.AreEqual(expectedBlue > 0, actualBlue > 0, $"青い文字の有無が一致します。セル={cell}");
        }
    }

    /// <summary>標準の表の、見出しと三行の各セルの内側（罫線を除く）の矩形を、見出しの三列、行0の三列、行1の三列、行2の三列の順で返します。</summary>
    internal static IReadOnlyList<Int32Rect> CellInteriors(CustomTableView table, double dpi)
    {
        CustomTableRowGeometry geometry = table.RowGeometry;
        int grid = CustomTablePixelGeometry.GridLineWidth(dpi);
        var result = new List<Int32Rect>();
        for (int row = -1; row < 3; row++)
        {
            // 見出しは、上端と下端の線を除いた内側だけを文字の領域にする。
            int top = row < 0 ? grid : geometry.HeaderHeightPx + row * geometry.RowHeightPx;
            int height = row < 0 ? geometry.HeaderHeightPx - 2 * grid : geometry.RowHeightPx - grid;
            for (int column = 0; column < 3; column++)
            {
                int left = Px(ColumnEdges[column], dpi);
                int right = Px(ColumnEdges[column + 1], dpi);
                result.Add(new Int32Rect(left, top, right - grid - left, height));
            }
        }
        return result;
    }

    private static int Px(double dip, double dpi) => CustomTablePixelGeometry.ToPixels(dip, dpi);

    private static CustomTableColumn CreateColumn(string id, int width, TextAlignment alignment, string header, string text, Brush foreground,
        IReadOnlyList<CustomTableTextRunStyle>? runs, StrongBox<int>? valueFetches)
    {
        return new CustomTableColumn(id, header, new CustomTableColumnSettings.ColumnLayout { Width = width, Visibility = Visibility.Visible }, 0, null, alignment,
            _ =>
            {
                if (valueFetches != null) { valueFetches.Value++; }
                return text;
            }, foregroundSelector: _ => foreground, textRunSelector: runs == null ? null : (_, _) => runs);
    }
}
