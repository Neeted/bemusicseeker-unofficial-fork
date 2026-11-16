using System;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一覧の寸法を物理画素へ丸める規則です。行と見出しの高さ、列の境界、罫線の幅を、
/// 描画・当たり判定・編集欄・スクロール範囲・UI Automationが同じ値で使えるよう一か所に置きます。
/// 倍率が100%なら、すべて従来のDIPの値と一致します。
/// </summary>
internal static class CustomTablePixelGeometry
{
    /// <summary>DIPの長さを、四捨五入（0.5は絶対値の大きい側）した物理画素の整数へ換算します。</summary>
    internal static int ToPixels(double dip, double pixelsPerDip)
    {
        return (int)Math.Round(dip * pixelsPerDip, MidpointRounding.AwayFromZero);
    }

    /// <summary>行や見出しの高さの実効値（物理画素）です。表示倍率で物理画素の整数へ丸め、1画素より小さくはしません。</summary>
    /// <param name="height">設定された高さ（DIP）。</param>
    /// <param name="pixelsPerDip">表示倍率。</param>
    internal static int HeightPixels(double height, double pixelsPerDip)
    {
        return Math.Max(1, ToPixels(Math.Max(1d, height), pixelsPerDip));
    }

    /// <summary>1 DIPの罫線の幅（物理画素）です。倍率を丸めた値で、1画素より小さくはしません。</summary>
    internal static int GridLineWidth(double pixelsPerDip)
    {
        return Math.Max(1, ToPixels(1d, pixelsPerDip));
    }

    /// <summary>セルの文字の左右の余白（物理画素）です。</summary>
    internal static int TextPadding(double pixelsPerDip)
    {
        return ToPixels(CustomTableView.CellTextHorizontalPadding, pixelsPerDip);
    }

    /// <summary>
    /// 一つの列のセルの文字を描ける幅（物理画素）です。セルの内側（罫線を除く）の幅から左右の文字余白を引きます。
    /// 描画とツールチップが同じ値で省略を判定します。
    /// </summary>
    /// <param name="slotLeftPx">列の左端（物理画素）。</param>
    /// <param name="slotRightPx">列の右端（物理画素）。</param>
    /// <param name="pixelsPerDip">表示倍率。</param>
    internal static int AvailableTextWidth(int slotLeftPx, int slotRightPx, double pixelsPerDip)
    {
        int interior = slotRightPx - slotLeftPx - GridLineWidth(pixelsPerDip);
        return Math.Max(1, interior - TextPadding(pixelsPerDip) * 2);
    }
}

/// <summary>
/// 一覧の縦の寸法（行の高さ、見出しの高さ、表示面の高さ）を物理画素の整数で持ち、
/// 完全表示行容量・描画行数・行の位置・座標にある行を求めます。
/// 描画・当たり判定・編集欄の位置・スクロール範囲・行ドロップの位置・UI Automationの矩形は、すべてこの値を使います。
/// </summary>
/// <remarks>
/// 倍率が100%でないときに丸めた高さをDIPの浮動小数で割ると、行が丁度収まる高さで商が整数をわずかに下回り、
/// 容量や行の添字が一つずれます。そのため割り算は物理画素の整数で行い、DIPの値は画素の整数を倍率で割って換算します。
/// </remarks>
internal readonly struct CustomTableRowGeometry
{
    /// <summary>設定値（DIP）と表示倍率から、縦の寸法を物理画素の整数で作ります。</summary>
    /// <param name="rowHeight">行の高さの設定値（DIP）。</param>
    /// <param name="headerHeight">見出しの高さの設定値（DIP）。</param>
    /// <param name="surfaceHeight">表示面の実高（DIP）。</param>
    /// <param name="pixelsPerDip">表示倍率。</param>
    internal CustomTableRowGeometry(double rowHeight, double headerHeight, double surfaceHeight, double pixelsPerDip)
    {
        PixelsPerDip = pixelsPerDip;
        RowHeightPx = CustomTablePixelGeometry.HeightPixels(rowHeight, pixelsPerDip);
        HeaderHeightPx = CustomTablePixelGeometry.HeightPixels(headerHeight, pixelsPerDip);
        SurfaceHeightPx = Math.Max(0, CustomTablePixelGeometry.ToPixels(surfaceHeight, pixelsPerDip));
        HorizontalScrollBarHeightPx = Math.Max(0, CustomTablePixelGeometry.ToPixels(CustomTableView.ScrollBarThickness, pixelsPerDip));
    }

    /// <summary>表示倍率です。</summary>
    internal double PixelsPerDip { get; }

    /// <summary>行の高さの実効値（物理画素）です。</summary>
    internal int RowHeightPx { get; }

    /// <summary>見出しの高さの実効値（物理画素）です。</summary>
    internal int HeaderHeightPx { get; }

    /// <summary>表示面の高さ（物理画素）です。</summary>
    internal int SurfaceHeightPx { get; }

    /// <summary>横スクロールバーの入力領域の高さ（物理画素）です。</summary>
    internal int HorizontalScrollBarHeightPx { get; }

    /// <summary>行の高さの実効値（DIP）です。物理画素の整数を倍率で割った値です。</summary>
    internal double RowHeight => RowHeightPx / PixelsPerDip;

    /// <summary>見出しの高さの実効値（DIP）です。物理画素の整数を倍率で割った値です。</summary>
    internal double HeaderHeight => HeaderHeightPx / PixelsPerDip;

    /// <summary>見出しを除く本体の高さ（物理画素）です。</summary>
    internal int BodyHeightPx => Math.Max(0, SurfaceHeightPx - HeaderHeightPx);

    /// <summary>横バーの手前まで完全に見える行数です。横バーが必要な場合だけ、その入力領域の高さを引きます。</summary>
    /// <param name="hasHorizontalScrollBar">横バーが必要か。</param>
    internal int FullVisibleRowCapacity(bool hasHorizontalScrollBar)
    {
        return CustomTableView.CalculateFullVisibleRowCapacity(BodyHeightPx - (hasHorizontalScrollBar ? HorizontalScrollBarHeightPx : 0), RowHeightPx);
    }

    /// <summary>バーの背後と部分表示の行を含む、全面の描画行数です。</summary>
    internal int DrawableRowCapacity => CustomTableView.CalculateDrawableRowCapacity(BodyHeightPx, RowHeightPx);

    /// <summary>先頭の可視行からの相対位置にある行の上端（DIP）です。物理画素の整数の位置を倍率で割った値です。</summary>
    /// <param name="slot">先頭の可視行を0とする相対位置。</param>
    internal double RowTop(int slot)
    {
        return (HeaderHeightPx + (long)slot * RowHeightPx) / PixelsPerDip;
    }

    /// <summary>
    /// 本体の中の座標（DIP）にある行の、先頭の可視行からの相対位置を返します。行の境界の座標は、境界から始まる行に属します。
    /// 見出しの中の座標を渡した場合は負の値です。
    /// </summary>
    /// <param name="y">表示面の上端からの座標（DIP）。</param>
    internal int RowSlotAt(double y)
    {
        int slot = (int)Math.Floor((y * PixelsPerDip - HeaderHeightPx) / RowHeightPx);
        // DIPから画素への換算は境界の前後へ丸め誤差を含むため、画素の整数から換算した行の上端と比べて決める。
        if (RowTop(slot + 1) <= y)
        {
            slot++;
        }
        else if (RowTop(slot) > y)
        {
            slot--;
        }
        return slot;
    }
}
