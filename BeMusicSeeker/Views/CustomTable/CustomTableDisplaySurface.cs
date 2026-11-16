using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Ribbit.Logging;
using D2D = Vortice.Direct2D1;
using D2DResultCode = Vortice.Direct2D1.ResultCode;
using DxgiResultCode = Vortice.DXGI.ResultCode;
using Result = SharpGen.Runtime.Result;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一覧の表示経路の選択と復旧を受け持ち、<see cref="CustomTableSurface"/> が表ごとに所有します。
/// WPFがソフトウェアで描画している環境ではCPU経路、それ以外ではGPU経路を使い、GPU経路を作れない・復旧できない表は、
/// 資源が解放されるまでCPU経路を保ちます（再表示で改めてGPU経路を試します）。
/// 描画先の作り直しの要求とデバイスの消失は、この表の面を解放して共有のデバイスを作り直し、同じ描画要求の中で一回だけ描き直させます。
/// UIスレッドだけで使い、<see cref="Dispose"/> で、文字のレイアウトを除く表示経路の資源をすべて解放します。
/// </summary>
internal sealed class CustomTableDisplaySurface : IDisposable
{
    private readonly Func<CustomTableDisplayFaults> getFaults;
    private readonly Action requestRedraw;
    private CustomTableDisplayPath path;
    private bool cpuOnly;
    private bool recovered;

    /// <param name="getFaults">故障注入の設定を返す関数。設定がなければnullを返します。</param>
    /// <param name="requestRedraw">GPU経路の前面バッファーが使えるようになったときに、再描画を一回要求する処理。</param>
    internal CustomTableDisplaySurface(Func<CustomTableDisplayFaults> getFaults, Action requestRedraw)
    {
        this.getFaults = getFaults;
        this.requestRedraw = requestRedraw;
    }

    /// <summary>描画先です。<see cref="Prepare"/> の後に使います。</summary>
    internal D2D.ID2D1RenderTarget Target => path?.Target;

    /// <summary>WPFへ渡している画像です。解放後と、まだ描いていない間はnullです。</summary>
    internal ImageSource Image => path?.Image;

    /// <summary>画像のWPF上の大きさ（DIP）です。</summary>
    internal Size ImageSize => path.ImageSize;

    /// <summary>描画要求の始めに呼び、この要求の中で復旧を試みた回数を数え直します。復旧は一つの要求で一回までです。</summary>
    internal void BeginRequest()
    {
        recovered = false;
    }

    /// <summary>
    /// 指定の寸法で描けるようにします。経路がなければ選んで作り、寸法か倍率が変わったときだけ描画先を作り直します。
    /// GPU経路の面の生成に失敗したときは、作り直さずにCPU経路へ切り替えます。
    /// </summary>
    /// <param name="pixelWidth">幅（物理画素）。</param>
    /// <param name="pixelHeight">高さ（物理画素）。</param>
    /// <param name="pixelsPerDip">表示倍率。</param>
    /// <param name="presenter">表の表示面。表が属する窓の描画の方式を調べます。</param>
    internal void Prepare(int pixelWidth, int pixelHeight, double pixelsPerDip, Visual presenter)
    {
        while (true)
        {
            path ??= !cpuOnly && !IsWpfSoftwareRendering(presenter)
                ? new CustomTableGpuDisplayPath(getFaults, requestRedraw)
                : new CustomTableCpuDisplayPath();
            CustomTableDisplayPath current = path;
            try
            {
                current.Prepare(pixelWidth, pixelHeight, pixelsPerDip);
                return;
            }
            catch (Exception exception) when (current is CustomTableGpuDisplayPath && exception is not OutOfMemoryException)
            {
                // 面の生成の失敗は、種類によらず作り直さずCPU経路へ切り替える。消失を示す失敗なら、共有のデバイスも作り直せるようにする。
                DisposePath();
                DiscardDevicesIfLost(exception);
                SwitchToCpu("surface creation failed", exception);
            }
        }
    }

    /// <summary>描画の開始前に、色の対応表が上限を超えていれば破棄します。</summary>
    internal void TrimBrushes()
    {
        path.TrimBrushes();
    }

    /// <summary>色の単色のブラシを返します。経路を作り直すと失効します。</summary>
    internal D2D.ID2D1SolidColorBrush GetBrush(Color color, double opacity = 1d)
    {
        return path.GetBrush(color, opacity);
    }

    /// <summary>単色のWPFのブラシからDirect2Dのブラシを返します。単色でないブラシはnullを返します。</summary>
    internal D2D.ID2D1SolidColorBrush GetBrush(Brush brush)
    {
        return path.GetBrush(brush);
    }

    /// <summary>描画を終え、結果を返します。</summary>
    internal Result EndDraw()
    {
        return path.EndDraw();
    }

    /// <summary>
    /// 描いた画素を、WPFが合成する画像へ反映します。GPU経路の反映が失敗したときは、その表の面を解放してCPU経路へ切り替え、
    /// 理由を診断ログへ記録してfalseを返します（呼び出し側が同じ描画要求の中でCPU経路で描き直します）。CPU経路の失敗は例外にします。
    /// </summary>
    internal bool TryPresent()
    {
        CustomTableDisplayPath current = path;
        try
        {
            current.Present();
            return true;
        }
        catch (Exception exception) when (current is CustomTableGpuDisplayPath && exception is not OutOfMemoryException)
        {
            DisposePath();
            DiscardDevicesIfLost(exception);
            SwitchToCpu("presenting the surface failed", exception);
            return false;
        }
    }

    /// <summary>失敗が消失を示す（または共有のデバイスが消失を報告している）なら、共有のデバイスを手放して世代を進めます。故障注入の失敗では手放しません。</summary>
    private static void DiscardDevicesIfLost(Exception exception)
    {
        if ((exception is SharpGen.Runtime.SharpGenException sharpGen && IsLoss(sharpGen.ResultCode)) || CustomTableGpuDevices.IsDeviceRemoved())
        {
            CustomTableGpuDevices.Discard();
        }
    }

    /// <summary>
    /// 描画の終端の失敗が復旧できるものなら、次の <see cref="Prepare"/> で作り直せるようにして、描き直せることを返します。
    /// 作り直しの要求とデバイスの消失のとき、この表の面を解放し、共有のデバイスを手放して世代を進めます。
    /// この要求で最初なら同じ経路で（新しいデバイスで）作り直し、二度目ならCPU経路へ切り替えます。それ以外の失敗とCPU経路の失敗は復旧せず、falseを返します。
    /// </summary>
    /// <param name="result">描画の終端の結果。</param>
    internal bool TryRecover(Result result)
    {
        if (path is not CustomTableGpuDisplayPath || !IsLoss(result))
        {
            return false;
        }
        // この表の面を先に解放してから、共有のデバイスを作り直せるようにする（ほかの表は世代の通知で追従する）。
        DisposePath();
        CustomTableGpuDevices.Discard();
        if (!recovered)
        {
            recovered = true;
            return true;
        }
        SwitchToCpu("device lost again after recovery (0x" + result.Code.ToString("X8", CultureInfo.InvariantCulture) + ")", null);
        return true;
    }

    /// <summary>描画先、画像、ブラシ（GPU経路の共有のデバイスは除く）を解放し、経路の選択も初めに戻します。再び使うときは <see cref="Prepare"/> から始めます。</summary>
    public void Dispose()
    {
        DisposePath();
        cpuOnly = false;
    }

    /// <summary>描画先の作り直しの要求またはデバイスの消失（リセットを含む）を表す結果かを返します。</summary>
    internal static bool IsLoss(Result result)
    {
        return result.Failure
            && (result.Code == D2DResultCode.RecreateTarget.Code
                || result.Code == DxgiResultCode.DeviceRemoved.Code
                || result.Code == DxgiResultCode.DeviceReset.Code);
    }

    /// <summary>
    /// WPF自身がソフトウェアで描画しているかを返します。描画の階層（Tier）が0、プロセスの描画方式がソフトウェア、
    /// 表が属する窓の描画方式がソフトウェアのいずれかが当たります。資源を生成する時点で評価します。
    /// </summary>
    private static bool IsWpfSoftwareRendering(Visual presenter)
    {
        return (RenderCapability.Tier >> 16) == 0
            || RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly
            || (PresentationSource.FromVisual(presenter) is HwndSource { CompositionTarget: { } target } && target.RenderMode == RenderMode.SoftwareOnly);
    }

    private void DisposePath()
    {
        CustomTableDisplayPath released = path;
        path = null;
        released?.Dispose();
    }

    /// <summary>以後の生成をCPU経路で行い、切替えを、理由を含めて診断ログへ一度だけ記録します。利用者へは通知しません。</summary>
    private void SwitchToCpu(string reason, Exception exception)
    {
        cpuOnly = true;
        try
        {
            string message = "custom table display switched to the CPU path: " + reason;
            if (exception != null)
            {
                NLogWrapper.FileLogger?.Warn(exception, message);
            }
            else
            {
                NLogWrapper.FileLogger?.Warn(message);
            }
        }
        catch
        {
            // 診断の失敗でCPU経路への切替えを妨げない。
        }
    }
}
