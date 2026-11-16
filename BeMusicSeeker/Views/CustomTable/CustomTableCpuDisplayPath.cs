using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2D = Vortice.Direct2D1;
using DCommon = Vortice.DCommon;
using Wic = Vortice.WIC;

namespace BeMusicSeeker.Views;

/// <summary>
/// CPU側の表示経路です。物理画素の寸法を持つWICビットマップへDirect2Dで描き、描き終えた画素をWPFの <see cref="WriteableBitmap"/> へコピーします。
/// GPUを使えない環境と、GPU経路を作れない・復旧できなかった表で使います。描画先は不透明（アルファ無視）にして、ClearTypeで文字を描けるようにします。
/// </summary>
internal sealed class CustomTableCpuDisplayPath : CustomTableDisplayPath
{
    private Wic.IWICBitmap bitmap;
    private D2D.ID2D1RenderTarget target;
    private WriteableBitmap image;

    internal override D2D.ID2D1RenderTarget Target => target;

    internal override ImageSource Image => image;

    internal override void Prepare(int pixelWidth, int pixelHeight, double pixelsPerDip)
    {
        if (target != null && PixelWidth == pixelWidth && PixelHeight == pixelHeight && PixelsPerDip.Equals(pixelsPerDip))
        {
            return;
        }
        ReleaseBuffers();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PixelsPerDip = pixelsPerDip;
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
        image = new WriteableBitmap(pixelWidth, pixelHeight, 96d * pixelsPerDip, 96d * pixelsPerDip, PixelFormats.Bgr32, null);
    }

    internal override SharpGen.Runtime.Result EndDraw()
    {
        return target.EndDraw();
    }

    internal override void Present()
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

    public override void Dispose()
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
}
