#nullable enable

using System;
using System.Windows;

namespace BeMusicSeeker.Views;

/// <summary>
/// 共通ScrollBarテンプレートへ渡す背景の描画指定を保持します。
/// 派生ScrollBarや保存状態を増やさず、一覧用Styleだけが不透明な背景を選べるようにします。
/// </summary>
public static class ScrollBarChrome
{
    /// <summary>
    /// 背景を通常・拡大・無効のすべてで不透明に表示するかを示します。
    /// 既存用は通常時に背景を表示せず、拡大時だけ半透明で表示するため、既定値はfalseです。
    /// </summary>
    public static readonly DependencyProperty IsOpaqueBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "IsOpaqueBackground",
            typeof(bool),
            typeof(ScrollBarChrome),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>対象ScrollBarの背景が不透明表示かを取得します。</summary>
    public static bool GetIsOpaqueBackground(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsOpaqueBackgroundProperty);
    }

    /// <summary>対象ScrollBarの背景を不透明表示にするかを設定します。</summary>
    public static void SetIsOpaqueBackground(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsOpaqueBackgroundProperty, value);
    }
}
