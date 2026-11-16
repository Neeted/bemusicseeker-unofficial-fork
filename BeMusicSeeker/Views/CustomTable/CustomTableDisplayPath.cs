using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Color4 = Vortice.Mathematics.Color4;
using D2D = Vortice.Direct2D1;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一覧の表示経路の共通部分です。物理画素の寸法の描画先へDirect2Dで描き、描き終えた画素をWPFの <see cref="ImageSource"/> へ渡します。
/// 描画の内容は経路によらず <see cref="Target"/> への描画呼出しで表すため、経路ごとに描画コードを持ちません。
/// 色から引く単色ブラシの対応表は、描画先のデバイスコンテキストごとに作る必要があるため経路が持ち、経路を解放すると失効します。
/// UIスレッドだけで使い、<see cref="Dispose"/> で解放します。
/// </summary>
internal abstract class CustomTableDisplayPath : IDisposable
{
    private const int MaxCachedBrushCount = 256;

    private readonly Dictionary<uint, D2D.ID2D1SolidColorBrush> brushes = [];

    /// <summary>描画先です。<see cref="Prepare"/> の後に使います。</summary>
    internal abstract D2D.ID2D1RenderTarget Target { get; }

    /// <summary>WPFへ渡している画像です。解放後と、まだ作っていない間はnullです。</summary>
    internal abstract ImageSource Image { get; }

    /// <summary>描画先の幅（物理画素）です。</summary>
    internal int PixelWidth { get; private protected set; }

    /// <summary>描画先の高さ（物理画素）です。</summary>
    internal int PixelHeight { get; private protected set; }

    /// <summary>表示倍率です。画像のWPF上の大きさを決めます。</summary>
    protected double PixelsPerDip { get; set; }

    /// <summary>画像のWPF上の大きさ（DIP）です。</summary>
    internal Size ImageSize => new(PixelWidth / PixelsPerDip, PixelHeight / PixelsPerDip);

    /// <summary>
    /// 指定の寸法で描けるようにします。寸法か倍率が変わらない限り、描画先を作り直しません。
    /// 生成に失敗したときは例外を出します。部分的に作った資源は <see cref="Dispose"/> が解放します。
    /// </summary>
    /// <param name="pixelWidth">幅（物理画素）。</param>
    /// <param name="pixelHeight">高さ（物理画素）。</param>
    /// <param name="pixelsPerDip">表示倍率。</param>
    internal abstract void Prepare(int pixelWidth, int pixelHeight, double pixelsPerDip);

    /// <summary>描画を終えます。<see cref="D2D.ID2D1RenderTarget.EndDraw()"/> の結果、または故障注入で置き換えた結果を返します。</summary>
    internal abstract SharpGen.Runtime.Result EndDraw();

    /// <summary>描き終えた画素をWPFが読める状態にします。<see cref="EndDraw"/> が成功した後に呼びます。</summary>
    internal abstract void Present();

    /// <summary>描画先、画像、ブラシなど、この経路が持つ面をすべて解放します。GPU経路の共有のデバイスは解放しません。</summary>
    public abstract void Dispose();

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

    /// <summary>色の単色のブラシを返します。同じ色のブラシは再利用し、経路の面を解放すると失効します。</summary>
    internal D2D.ID2D1SolidColorBrush GetBrush(Color color, double opacity = 1d)
    {
        double alpha = color.A / 255d * Math.Clamp(opacity, 0d, 1d);
        uint key = ((uint)Math.Round(alpha * 255d) << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (brushes.TryGetValue(key, out D2D.ID2D1SolidColorBrush brush))
        {
            return brush;
        }
        brush = Target.CreateSolidColorBrush(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, (float)alpha), null);
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

    /// <summary>色の対応表のブラシをすべて解放します。描画先より前に呼びます。</summary>
    protected void DisposeBrushes()
    {
        foreach (D2D.ID2D1SolidColorBrush brush in brushes.Values)
        {
            brush.Dispose();
        }
        brushes.Clear();
    }
}
