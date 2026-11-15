using System;
using SharpGen.Runtime;
using D2DResultCode = Vortice.Direct2D1.ResultCode;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace BeMusicSeeker.Views;

/// <summary>故障注入で生成を失敗させる、表示経路の資源の生成箇所です。</summary>
internal enum CustomTableDisplayFaultPoint
{
    /// <summary>その表のDirect3D 11のデバイスの取得（共有の状態は変えない）。</summary>
    Direct3D11Device,

    /// <summary>その表のDirect3D 9Exのデバイスの取得（共有の状態は変えない）。</summary>
    Direct3D9Device,

    /// <summary>共有面（Direct3D 11のテクスチャ、Direct2Dの描画先、Direct3D 9Exの面、<c>D3DImage</c> の裏面）の生成。</summary>
    SharedSurface
}

/// <summary>故障注入で描画の終端に返す、描画資源の消失の種類です。</summary>
internal enum CustomTableDisplayLoss
{
    /// <summary>Direct2Dの描画先の作り直しの要求（<c>D2DERR_RECREATE_TARGET</c>）。</summary>
    RecreateTarget,

    /// <summary>グラフィックスデバイスの消失（<c>DXGI_ERROR_DEVICE_REMOVED</c>）。</summary>
    DeviceRemoved
}

/// <summary>
/// 表示経路の失敗を決定的に再現するための、表示面ごとの最小の切替点です。既定は設定なし（nullの参照）で、製品の経路には影響しません。
/// 生成の失敗と描画の終端の消失の二つだけを注入し、解放・作り直し・描き直し・CPU経路への切替えは実際の処理を通します。
/// UIスレッドだけで使います。
/// </summary>
internal sealed class CustomTableDisplayFaults
{
    private readonly int[] creationFailures = new int[Enum.GetValues<CustomTableDisplayFaultPoint>().Length];
    private CustomTableDisplayLoss endDrawLoss;

    /// <summary>描画の終端で、実際の結果の代わりに返す消失の残りの回数です。</summary>
    internal int RemainingEndDrawLosses { get; private set; }

    /// <summary>指定の箇所で、資源の生成を失敗させる回数を設定します。設定済みの回数は置き換えます。</summary>
    internal void FailCreation(CustomTableDisplayFaultPoint point, int count = 1)
    {
        creationFailures[(int)point] = Math.Max(0, count);
    }

    /// <summary>描画の試行ごとに一回確認する終端の結果を、指定の消失で置き換える回数を設定します。設定済みの回数は置き換えます。</summary>
    internal void LoseAtEndDraw(CustomTableDisplayLoss loss, int count = 1)
    {
        endDrawLoss = loss;
        RemainingEndDrawLosses = Math.Max(0, count);
    }

    /// <summary>指定の箇所で、生成を失敗させる残りの回数です。</summary>
    internal int RemainingCreationFailures(CustomTableDisplayFaultPoint point)
    {
        return creationFailures[(int)point];
    }

    /// <summary>指定の箇所の生成を失敗させる設定が残っていれば、回数を一つ減らして例外を出します。</summary>
    internal void ThrowIfCreationFails(CustomTableDisplayFaultPoint point)
    {
        if (creationFailures[(int)point] > 0)
        {
            creationFailures[(int)point]--;
            throw new InvalidOperationException("Injected display path creation failure: " + point);
        }
    }

    /// <summary>消失を返す設定が残っていれば、回数を一つ減らして実際の結果の代わりに返す結果を設定します。</summary>
    internal bool TryConsumeEndDrawLoss(out Result result)
    {
        if (RemainingEndDrawLosses <= 0)
        {
            result = default;
            return false;
        }
        RemainingEndDrawLosses--;
        result = endDrawLoss == CustomTableDisplayLoss.RecreateTarget ? D2DResultCode.RecreateTarget : DxgiResultCode.DeviceRemoved;
        return true;
    }
}
