using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using D2D = Vortice.Direct2D1;
using D3D11 = Vortice.Direct3D11;
using D3D9 = Vortice.Direct3D9;
using DCommon = Vortice.DCommon;
using Dxgi = Vortice.DXGI;

namespace BeMusicSeeker.Views;

/// <summary>
/// GPU合成の表示経路の、表ごとの面です。<see cref="CustomTableGpuDevices"/> の共有デバイスの上に、Direct3D 11の共有テクスチャ、Direct2Dの描画先、
/// Direct3D 9Exの面、<see cref="D3DImage"/> を表ごとに持ち、Direct2Dで描いた内容をWPFが一枚の画像として合成します。窓は作りません。
/// <see cref="D3DImage"/> の裏面は「ソフトウェアの代替を有効」で設定します。リモート接続などWPFがソフトウェアで合成する場面と、
/// 実際に描かれた画像の読取りのためです。動作する組合せはDirect3D 11の <c>B8G8R8A8_UNorm</c> とDirect3D 9の <c>A8R8G8B8</c> だけで、
/// 全面を不透明で描く限りアルファは255になり、ClearTypeも効きます。解放では共有のデバイスを手放しません。UIスレッドだけで使います。
/// </summary>
internal sealed class CustomTableGpuDisplayPath : CustomTableDisplayPath
{
    private readonly Func<CustomTableDisplayFaults> getFaults;
    private readonly Action requestRedraw;
    private int generation;
    private bool subscribed;
    private D2D.ID2D1DeviceContext context2D;
    private D3D11.ID3D11Texture2D texture11;
    private D2D.ID2D1Bitmap1 bitmap2D;
    private D3D9.IDirect3DTexture9 texture9;
    private D3D9.IDirect3DSurface9 surface9;
    private D3DImage image;
    private bool backBufferStale;

    /// <param name="getFaults">故障注入の設定を返す関数。設定がなければnullを返します。</param>
    /// <param name="requestRedraw">前面バッファーが使えるようになったとき、共有のデバイスの世代が進んだときに、再描画を一回要求する処理。</param>
    internal CustomTableGpuDisplayPath(Func<CustomTableDisplayFaults> getFaults, Action requestRedraw)
    {
        this.getFaults = getFaults;
        this.requestRedraw = requestRedraw;
    }

    internal override D2D.ID2D1RenderTarget Target => context2D;

    internal override ImageSource Image => image;

    /// <summary>
    /// 描画の開始時に、面が現在の世代で寸法と倍率に合っているかを調べます。合っていれば何もしません。
    /// 世代が違えば（ほかの表の復旧でデバイスが作り直された）自分の面を解放し、共有のデバイスを取得して作り直します。寸法や倍率が変われば面だけを作り直します。
    /// </summary>
    internal override void Prepare(int pixelWidth, int pixelHeight, double pixelsPerDip)
    {
        if (bitmap2D != null && generation == CustomTableGpuDevices.Generation
            && PixelWidth == pixelWidth && PixelHeight == pixelHeight && PixelsPerDip.Equals(pixelsPerDip))
        {
            return;
        }
        int current = CustomTableGpuDevices.Acquire(getFaults?.Invoke());
        if (generation != current)
        {
            ReleaseSurface(releaseContext: true);
            generation = current;
        }
        ReleaseSurface(releaseContext: false);
        CreateSurface(pixelWidth, pixelHeight, pixelsPerDip);
    }

    internal override SharpGen.Runtime.Result EndDraw()
    {
        SharpGen.Runtime.Result result = context2D.EndDraw();
        CustomTableDisplayFaults faults = getFaults?.Invoke();
        return faults != null && faults.TryConsumeEndDrawLoss(out SharpGen.Runtime.Result injected) ? injected : result;
    }

    /// <summary>
    /// Direct3D 11側の描画を確定してから、<see cref="D3DImage"/> の更新領域を通知します。<c>Flush</c> を省くと、
    /// Direct3D 9Ex側から古い内容が見えます。<c>Lock</c> と <c>Unlock</c> は、通知が失敗しても必ず対にします。
    /// </summary>
    internal override void Present()
    {
        CustomTableGpuDevices.Context11.Flush();
        image.Lock();
        try
        {
            if (backBufferStale)
            {
                // 前面バッファーが使えなくなった間に、WPFが裏面との結び付きを失っている場合に備えて結び直す。
                image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9.NativePointer, true);
                backBufferStale = false;
            }
            image.AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
        }
        finally
        {
            image.Unlock();
        }
    }

    /// <summary>この表の面を、<see cref="D3DImage"/> の裏面の解除から順に解放します。共有のデバイスは解放しません。</summary>
    public override void Dispose()
    {
        ReleaseSurface(releaseContext: true);
    }

    private void CreateSurface(int pixelWidth, int pixelHeight, double pixelsPerDip)
    {
        getFaults?.Invoke()?.ThrowIfCreationFails(CustomTableDisplayFaultPoint.SharedSurface);
        if (context2D == null)
        {
            context2D = CustomTableGpuDevices.Device2D.CreateDeviceContext(D2D.DeviceContextOptions.None);
            context2D.TextAntialiasMode = D2D.TextAntialiasMode.Cleartype;
        }
        // Direct3D 9Exから開けるのは旧式の共有（NTハンドルを使わない共有）です。
        var description = new D3D11.Texture2DDescription
        {
            Width = (uint)pixelWidth,
            Height = (uint)pixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Dxgi.Format.B8G8R8A8_UNorm,
            SampleDescription = new Dxgi.SampleDescription(1, 0),
            Usage = D3D11.ResourceUsage.Default,
            BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
            CPUAccessFlags = D3D11.CpuAccessFlags.None,
            MiscFlags = D3D11.ResourceOptionFlags.Shared
        };
        texture11 = CustomTableGpuDevices.Device11.CreateTexture2D(description);
        IntPtr sharedHandle;
        using (Dxgi.IDXGIResource resource = texture11.QueryInterface<Dxgi.IDXGIResource>())
        {
            sharedHandle = resource.SharedHandle;
        }
        using (Dxgi.IDXGISurface dxgiSurface = texture11.QueryInterface<Dxgi.IDXGISurface>())
        {
            var properties = new D2D.BitmapProperties1(
                new DCommon.PixelFormat(Dxgi.Format.B8G8R8A8_UNorm, DCommon.AlphaMode.Ignore),
                96f,
                96f,
                D2D.BitmapOptions.Target | D2D.BitmapOptions.CannotDraw);
            bitmap2D = context2D.CreateBitmapFromDxgiSurface(dxgiSurface, properties);
        }
        context2D.Target = bitmap2D;
        texture9 = CustomTableGpuDevices.Device9.CreateTexture((uint)pixelWidth, (uint)pixelHeight, 1, D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref sharedHandle);
        surface9 = texture9.GetSurfaceLevel(0);
        // 画像は物理画素の寸法を持ち、倍率で割ったDIPの大きさでWPFへ置く。
        var newImage = new D3DImage(96d * pixelsPerDip, 96d * pixelsPerDip);
        newImage.Lock();
        try
        {
            newImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9.NativePointer, true);
        }
        finally
        {
            newImage.Unlock();
        }
        newImage.IsFrontBufferAvailableChanged += ImageIsFrontBufferAvailableChanged;
        image = newImage;
        backBufferStale = false;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PixelsPerDip = pixelsPerDip;
        if (!subscribed)
        {
            // 面を持つ間だけ、共有のデバイスの世代の変更を受ける。受けた先では再描画の要求だけを行う。
            CustomTableGpuDevices.GenerationChanged += GenerationChanged;
            subscribed = true;
        }
    }

    /// <summary>
    /// 面を解放します。<see cref="D3DImage"/> から裏面を外し、Direct3D 9Exの面とテクスチャ、Direct2Dのブラシ・描画先・コンテキスト、
    /// Direct3D 11のテクスチャの順です。先に外さないと、WPFの合成が解放済みの面を読みます。ファイナライザーには頼りません。
    /// テクスチャを解放した後は、<see cref="FlushReleasedTexture"/> で遅延した破棄を進めます。
    /// </summary>
    /// <param name="releaseContext">trueなら、Direct2Dのコンテキストとブラシの対応表も解放し、世代の通知の購読を外します。寸法の変更では保ちます。</param>
    private void ReleaseSurface(bool releaseContext)
    {
        try
        {
            DetachImage();
        }
        finally
        {
            try
            {
                surface9?.Dispose();
                surface9 = null;
                texture9?.Dispose();
                texture9 = null;
            }
            finally
            {
                try
                {
                    if (releaseContext)
                    {
                        DisposeBrushes();
                    }
                    if (context2D != null)
                    {
                        context2D.Target = null;
                    }
                    bitmap2D?.Dispose();
                    bitmap2D = null;
                    if (releaseContext)
                    {
                        context2D?.Dispose();
                        context2D = null;
                    }
                }
                finally
                {
                    if (texture11 != null)
                    {
                        texture11.Dispose();
                        texture11 = null;
                        FlushReleasedTexture();
                    }
                    PixelWidth = 0;
                    PixelHeight = 0;
                    if (releaseContext && subscribed)
                    {
                        CustomTableGpuDevices.GenerationChanged -= GenerationChanged;
                        subscribed = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 解放したDirect3D 11のテクスチャの破棄を、共有のコンテキストの <c>Flush</c> で進めます。Direct3D 11はテクスチャの破棄を遅らせるため、
    /// 次の描画まで大きなテクスチャのメモリが残り続けないようにします。テクスチャを作った世代の共有のデバイスがすでに手放された（世代が進んだ、
    /// またはデバイスがない）場合は、新しいコンテキストを呼んでも旧いテクスチャの破棄は進まないため呼びません。
    /// </summary>
    private void FlushReleasedTexture()
    {
        D3D11.ID3D11DeviceContext context = CustomTableGpuDevices.Context11;
        if (context != null && generation == CustomTableGpuDevices.Generation)
        {
            context.Flush();
        }
    }

    /// <summary>画像から裏面を外し、前面バッファーの通知の購読を外します。</summary>
    private void DetachImage()
    {
        D3DImage current = image;
        if (current == null)
        {
            return;
        }
        image = null;
        current.IsFrontBufferAvailableChanged -= ImageIsFrontBufferAvailableChanged;
        current.Lock();
        try
        {
            current.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        }
        finally
        {
            current.Unlock();
        }
    }

    private void GenerationChanged()
    {
        requestRedraw?.Invoke();
    }

    private void ImageIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is D3DImage { IsFrontBufferAvailable: true } && ReferenceEquals(sender, image))
        {
            backBufferStale = true;
            requestRedraw?.Invoke();
        }
    }
}
