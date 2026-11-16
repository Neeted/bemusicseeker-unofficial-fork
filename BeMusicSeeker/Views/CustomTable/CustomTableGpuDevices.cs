using System;
using System.Runtime.InteropServices;
using D2D = Vortice.Direct2D1;
using D3D = Vortice.Direct3D;
using D3D11 = Vortice.Direct3D11;
using D3D9 = Vortice.Direct3D9;
using Dxgi = Vortice.DXGI;

namespace BeMusicSeeker.Views;

/// <summary>
/// GPU経路が使う、プロセスで一組のデバイスです。Direct3D 11のデバイスとコンテキスト、Direct2Dのデバイス、Direct3D 9Exの本体とデバイスを、
/// 最初に必要になったときに作り、プロセスの終了まで保ちます（終了専用の解放は持ちません）。表ごとに持つのは、寸法に依存する面だけです。
/// 消失を検出した表が <see cref="Discard"/> で旧いデバイスを手放して <see cref="Generation"/> を進め、面を持つほかの表は
/// <see cref="GenerationChanged"/> で再描画を要求され、次の描画の開始時に世代の不一致から自分の面を作り直します。UIスレッドだけで使います。
/// </summary>
internal static partial class CustomTableGpuDevices
{
    private static D3D11.ID3D11Device device11;
    private static D3D11.ID3D11DeviceContext context11;
    private static D2D.ID2D1Device device2D;
    private static D3D9.IDirect3D9Ex direct3D9;
    private static D3D9.IDirect3DDevice9Ex device9;

    /// <summary>共有のデバイスの世代です。作り直すたびに進みます。面は、作った時点の世代を持ちます。</summary>
    internal static int Generation { get; private set; }

    /// <summary>世代が進んだときに通知します。受け手は再描画の要求だけを行い、資源の解放や生成は次の描画の中で行います。</summary>
    internal static event Action GenerationChanged;

    /// <summary>共有のDirect3D 11のデバイスです。最初の取得の前と手放した後はnullで、<see cref="Acquire"/> が返した世代の間だけ有効です。</summary>
    internal static D3D11.ID3D11Device Device11 => device11;

    /// <summary>共有のDirect3D 11のコンテキストです。有効な期間は <see cref="Device11"/> と同じです。</summary>
    internal static D3D11.ID3D11DeviceContext Context11 => context11;

    /// <summary>共有のDirect2Dのデバイスです。有効な期間は <see cref="Device11"/> と同じです。</summary>
    internal static D2D.ID2D1Device Device2D => device2D;

    /// <summary>共有のDirect3D 9Exのデバイスです。有効な期間は <see cref="Device11"/> と同じです。</summary>
    internal static D3D9.IDirect3DDevice9Ex Device9 => device9;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDesktopWindow();

    /// <summary>
    /// 共有のデバイスを取得し、世代を返します。なければ作ります。故障注入が設定された表の取得は、デバイスがあっても指定の回数だけ失敗し、
    /// 共有の状態（世代、既存のデバイス）は変えません。実際の生成に失敗したときは、部分的に作ったデバイスを手放して例外を出します。
    /// </summary>
    /// <param name="faults">取得する表の故障注入の設定。なければnull。</param>
    internal static int Acquire(CustomTableDisplayFaults faults)
    {
        faults?.ThrowIfCreationFails(CustomTableDisplayFaultPoint.Direct3D11Device);
        faults?.ThrowIfCreationFails(CustomTableDisplayFaultPoint.Direct3D9Device);
        if (device11 == null || device9 == null)
        {
            try
            {
                Create();
            }
            catch
            {
                ReleaseDevices();
                throw;
            }
        }
        return Generation;
    }

    /// <summary>
    /// 共有のデバイスが消失を報告しているかを返します。デバイスがないときはfalseです。
    /// </summary>
    internal static bool IsDeviceRemoved()
    {
        try
        {
            return device11 != null && device11.DeviceRemovedReason.Failure;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return true;
        }
    }

    /// <summary>
    /// 共有のデバイスを手放して世代を進め、通知します。次に取得する表が新しいデバイスを作ります。
    /// 呼び出し側は、先に自分の面を解放しておきます。ほかの表の旧い世代の面は、それぞれが自分で解放します。
    /// </summary>
    internal static void Discard()
    {
        ReleaseDevices();
        Generation++;
        GenerationChanged?.Invoke();
    }

    private static void Create()
    {
        if (device11 == null)
        {
            // Direct2Dと共用するため、BGRA対応が必須です。
            D3D11.D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                D3D.DriverType.Hardware,
                D3D11.DeviceCreationFlags.BgraSupport,
                [D3D.FeatureLevel.Level_11_0, D3D.FeatureLevel.Level_10_1, D3D.FeatureLevel.Level_10_0],
                out device11,
                out context11).CheckError();
        }
        if (device2D == null)
        {
            using Dxgi.IDXGIDevice dxgiDevice = device11.QueryInterface<Dxgi.IDXGIDevice>();
            device2D = CustomTableRenderResources.D2DFactory.CreateDevice(dxgiDevice);
        }
        if (device9 == null)
        {
            direct3D9 = D3D9.D3D9.Direct3DCreate9Ex();
            // 描画は共有テクスチャへ行い、Direct3D 9Exのバックバッファーは使いません。窓は作らず、デスクトップの窓を指定します。
            var presentParameters = new D3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = D3D9.SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = D3D9.PresentInterval.Default,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = D3D9.Format.Unknown
            };
            device9 = direct3D9.CreateDeviceEx(
                0,
                D3D9.DeviceType.Hardware,
                IntPtr.Zero,
                D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve,
                presentParameters);
        }
    }

    /// <summary>Direct3D 9Ex、Direct2D、Direct3D 11の順にデバイスを手放します。</summary>
    private static void ReleaseDevices()
    {
        try
        {
            device9?.Dispose();
            device9 = null;
            direct3D9?.Dispose();
            direct3D9 = null;
        }
        finally
        {
            try
            {
                device2D?.Dispose();
                device2D = null;
            }
            finally
            {
                if (context11 != null)
                {
                    context11.ClearState();
                    context11.Flush();
                    context11.Dispose();
                    context11 = null;
                }
                device11?.Dispose();
                device11 = null;
            }
        }
    }
}
