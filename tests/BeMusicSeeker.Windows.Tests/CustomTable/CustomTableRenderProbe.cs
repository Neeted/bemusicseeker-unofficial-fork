using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>表示面の画素を物理画素の単位で読む結果です。座標は表示面の左上を原点とする物理画素です。</summary>
internal sealed class SurfacePixels(int width, int height, byte[] bgra)
{
    internal int Width { get; } = width;

    internal int Height { get; } = height;

    internal byte[] Bgra { get; } = bgra;

    internal Color GetColor(int x, int y)
    {
        int offset = (y * Width + x) * 4;
        return Color.FromRgb(Bgra[offset + 2], Bgra[offset + 1], Bgra[offset]);
    }

    /// <summary>指定の矩形に含まれる、指定の色と一致しない画素の外接矩形を返します。一致しない画素がなければnullです。</summary>
    internal Int32Rect? FindInkBounds(Int32Rect area, Color background)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = Math.Max(0, area.Y); y < Math.Min(Height, area.Y + area.Height); y++)
        {
            for (int x = Math.Max(0, area.X); x < Math.Min(Width, area.X + area.Width); x++)
            {
                if (GetColor(x, y) != background)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            }
        }
        return right < 0 ? null : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>指定の矩形で、指定の色と一致しない画素の数を返します。</summary>
    internal int CountNotEqual(Int32Rect area, Color background)
    {
        int count = 0;
        for (int y = Math.Max(0, area.Y); y < Math.Min(Height, area.Y + area.Height); y++)
        {
            for (int x = Math.Max(0, area.X); x < Math.Min(Width, area.X + area.Width); x++)
            {
                if (GetColor(x, y) != background) { count++; }
            }
        }
        return count;
    }

    /// <summary>指定の矩形で、背景から最も遠い画素の色（文字の支配的な色）を返します。全面が背景なら背景の色です。</summary>
    internal Color DominantInkColor(Int32Rect area, Color background)
    {
        int Distance(Color c) => Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
        int max = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++) { for (int x = area.X; x < area.X + area.Width; x++) { max = Math.Max(max, Distance(GetColor(x, y))); } }
        if (max == 0) { return background; }
        var counts = new Dictionary<Color, int>();
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                Color c = GetColor(x, y);
                if (Distance(c) >= max * 0.95d) { counts[c] = counts.GetValueOrDefault(c) + 1; }
            }
        }
        return counts.MaxBy(pair => pair.Value).Key;
    }
    /// <summary>指定の矩形で、赤・青の文字色が支配的な画素の数を数えます。</summary>
    internal (int Red, int Blue) CountDominant(Int32Rect area)
    {
        int red = 0, blue = 0;
        for (int y = Math.Max(0, area.Y); y < Math.Min(Height, area.Y + area.Height); y++)
        {
            for (int x = Math.Max(0, area.X); x < Math.Min(Width, area.X + area.Width); x++)
            {
                Color c = GetColor(x, y);
                if (c.R > 120 && c.G < 100 && c.B < 100) { red++; }
                if (c.B > 120 && c.G < 100 && c.R < 100) { blue++; }
            }
        }
        return (red, blue);
    }

    /// <summary>指定の矩形の画素を、別の画素列の指定の位置からの同じ大きさの矩形と比べ、一致するかを返します。</summary>
    internal bool RegionEquals(Int32Rect area, SurfacePixels other, int otherX, int otherY)
    {
        for (int y = 0; y < area.Height; y++)
        {
            for (int x = 0; x < area.Width; x++)
            {
                if (GetColor(area.X + x, area.Y + y) != other.GetColor(otherX + x, otherY + y)) { return false; }
            }
        }
        return true;
    }

    /// <summary>二つの画素列が全面で一致するかを返します。</summary>
    internal bool SameAs(SurfacePixels other)
    {
        return Width == other.Width && Height == other.Height && Bgra.AsSpan().SequenceEqual(other.Bgra);
    }
}

/// <summary>
/// 一覧の表示面を物理画素で読む補助です。表示面だけを読み、描画器が自分で申告する値を使いません。
/// 「描画を失効させてから読む」と「今の状態のまま読む」を分けます。
/// </summary>
internal static class CustomTableRenderProbe
{
    internal static CustomTableSurface Surface(CustomTableView table)
    {
        return table.Children.OfType<CustomTableSurface>().Single();
    }

    /// <summary>窓のない、親を持たない表の表示倍率を固定します。プロセスの表示倍率は変えません。</summary>
    internal static void SetDpi(CustomTableView table, double scale)
    {
        VisualTreeHelper.SetRootDpi(table, new DpiScale(scale, scale));
    }

    /// <summary>描画を失効させ、配置の確定（<c>UpdateLayout</c>）だけで描かれた表示面を読みます。</summary>
    internal static SurfacePixels Redraw(CustomTableView table)
    {
        Surface(table).InvalidateVisual();
        table.UpdateLayout();
        return Read(table);
    }

    /// <summary>描画の失効も配置の確定もせず、表示面が今保持している画像の画素を読みます。</summary>
    internal static SurfacePixels Read(CustomTableView table)
    {
        if (table.SurfaceImage is not BitmapSource image)
        {
            throw new AssertFailedException("表示面が画像を保持していません。");
        }
        int stride = image.PixelWidth * 4;
        byte[] pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return new SurfacePixels(image.PixelWidth, image.PixelHeight, pixels);
    }

    /// <summary>表の窓のない配置を確定します。倍率を固定する場合は、配置の前に <see cref="SetDpi"/> を呼びます。</summary>
    internal static void Arrange(CustomTableView table, double width, double height)
    {
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0d, 0d, width, height));
        table.UpdateLayout();
    }
}
