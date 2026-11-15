using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color4 = Vortice.Mathematics.Color4;
using D2D = Vortice.Direct2D1;
using DCommon = Vortice.DCommon;
using Wic = Vortice.WIC;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一覧の表示経路です。物理画素の寸法を持つWICビットマップへDirect2Dで描き、描き終えた画素をWPFの <see cref="ImageSource"/> へ反映します。
/// 描画先、ビットマップ、色から引くブラシの対応表を一つにまとめ、<see cref="CustomTableSurface"/> が所有します。
/// 描画先は不透明（アルファ無視）にして、ClearTypeで文字を描けるようにします。UIスレッドだけで使い、<see cref="Dispose"/> で解放します。
/// </summary>
internal sealed class CustomTableDisplaySurface : IDisposable
{
    private const int MaxCachedBrushCount = 256;

    private readonly Dictionary<uint, D2D.ID2D1SolidColorBrush> brushes = [];
    private Wic.IWICBitmap bitmap;
    private D2D.ID2D1RenderTarget target;
    private WriteableBitmap image;
    private double pixelsPerDip;

    /// <summary>描画先です。<see cref="Prepare"/> の後に使います。</summary>
    internal D2D.ID2D1RenderTarget Target => target;

    /// <summary>WPFへ渡している画像です。解放後と、まだ描いていない間はnullです。</summary>
    internal ImageSource Image => image;

    /// <summary>描画先の幅（物理画素）です。</summary>
    internal int PixelWidth { get; private set; }

    /// <summary>描画先の高さ（物理画素）です。</summary>
    internal int PixelHeight { get; private set; }

    /// <summary>画像のWPF上の大きさ（DIP）です。</summary>
    internal Size ImageSize => new(PixelWidth / pixelsPerDip, PixelHeight / pixelsPerDip);

    /// <summary>
    /// 指定の寸法で描けるようにします。寸法か倍率が変わったときだけ、描画先とビットマップを作り直します。
    /// </summary>
    /// <param name="pixelWidth">幅（物理画素）。</param>
    /// <param name="pixelHeight">高さ（物理画素）。</param>
    /// <param name="newPixelsPerDip">表示倍率。画像のWPF上の大きさを決めます。</param>
    internal void Prepare(int pixelWidth, int pixelHeight, double newPixelsPerDip)
    {
        if (target != null && PixelWidth == pixelWidth && PixelHeight == pixelHeight && pixelsPerDip.Equals(newPixelsPerDip))
        {
            return;
        }
        ReleaseBuffers();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        pixelsPerDip = newPixelsPerDip;
        bitmap = CustomTableRenderResources.WicFactory.CreateBitmap((uint)pixelWidth, (uint)pixelHeight, Wic.PixelFormat.Format32bppBGR, Wic.BitmapCreateCacheOption.CacheOnLoad);
        var properties = new D2D.RenderTargetProperties(
            D2D.RenderTargetType.Software,
            new DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, DCommon.AlphaMode.Ignore),
            96f,
            96f,
            D2D.RenderTargetUsage.None,
            D2D.FeatureLevel.Default);
        target = CustomTableRenderResources.D2DFactory.CreateWicBitmapRenderTarget(bitmap, properties);
        target.TextAntialiasMode = D2D.TextAntialiasMode.Cleartype;
        image = new WriteableBitmap(pixelWidth, pixelHeight, 96d * newPixelsPerDip, 96d * newPixelsPerDip, PixelFormats.Bgr32, null);
    }

    /// <summary>
    /// 描画の開始前に、色の対応表が上限を超えていれば破棄します。一回の描画の途中では破棄しないため、
    /// 取得済みのブラシを描画中に失うことはありません。
    /// </summary>
    internal void TrimBrushes()
    {
        if (brushes.Count > MaxCachedBrushCount)
        {
            DisposeBrushes();
        }
    }

    /// <summary>色の単色のブラシを返します。同じ色のブラシは再利用し、描画先を作り直すと失効します。</summary>
    internal D2D.ID2D1SolidColorBrush GetBrush(Color color, double opacity = 1d)
    {
        double alpha = color.A / 255d * Math.Clamp(opacity, 0d, 1d);
        uint key = ((uint)Math.Round(alpha * 255d) << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (brushes.TryGetValue(key, out D2D.ID2D1SolidColorBrush brush))
        {
            return brush;
        }
        brush = target.CreateSolidColorBrush(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, (float)alpha), null);
        brushes.Add(key, brush);
        return brush;
    }

    /// <summary>
    /// 単色のWPFのブラシからDirect2Dのブラシを返します。単色でないブラシ（グラデーションなど）はnullを返し、
    /// 呼び出し側が既定の色で描く、または描かないを選びます。
    /// </summary>
    internal D2D.ID2D1SolidColorBrush GetBrush(Brush brush)
    {
        return brush is SolidColorBrush solid ? GetBrush(solid.Color, solid.Opacity) : null;
    }

    /// <summary>描いた画素を、WPFが保持する画像へ反映します。</summary>
    internal void Present()
    {
        image.Lock();
        try
        {
            bitmap.CopyPixels((uint)image.BackBufferStride, (uint)(image.BackBufferStride * PixelHeight), image.BackBuffer);
            image.AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
        }
        finally
        {
            image.Unlock();
        }
    }

    /// <summary>描画先、ビットマップ、ブラシ、画像を解放します。再び使うときは <see cref="Prepare"/> から始めます。</summary>
    public void Dispose()
    {
        ReleaseBuffers();
    }

    private void ReleaseBuffers()
    {
        DisposeBrushes();
        target?.Dispose();
        target = null;
        bitmap?.Dispose();
        bitmap = null;
        image = null;
        PixelWidth = 0;
        PixelHeight = 0;
    }

    private void DisposeBrushes()
    {
        foreach (D2D.ID2D1SolidColorBrush brush in brushes.Values)
        {
            brush.Dispose();
        }
        brushes.Clear();
    }
}
