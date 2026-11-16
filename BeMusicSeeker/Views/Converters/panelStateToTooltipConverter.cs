using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

/// <summary>
/// プレイヤーパネルの有効な表示モードから、表示切替ボタンの tooltip 文字列を作る converter です。
/// 第2入力に <see cref="ViewModels.ResourceService.Resources"/> を受け取り、言語変更の通知で表示モードを変えずに再評価します。
/// </summary>
internal class panelStateToTooltipConverter : IMultiValueConverter
{
    /// <summary>
    /// 表示モードと現在の資源から tooltip を生成します。
    /// 区切り <c>": "</c> は翻訳対象ではなく、言語ごとに変わるのは両側の資源値だけです。
    /// </summary>
    /// <param name="values">0番目は <see cref="PlayerPanelState"/>、1番目は再評価の契機となる資源（値は使用しません）。</param>
    /// <param name="targetType">未使用。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">未使用。</param>
    /// <returns>tooltip 文字列。表示モードが未確定の場合は <see cref="Binding.DoNothing"/>。</returns>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 0 && values[0] is PlayerPanelState state)
        {
            if (state.HasFlag(PlayerPanelState.BMS_PLAYER))
            {
                return Resources.Tooltip_view_mode + ": " + Resources.Tooltip_bms_player;
            }
            return Resources.Tooltip_view_mode + ": " + Resources.Tooltip_image_file;
        }
        return Binding.DoNothing;
    }

    /// <summary>
    /// 未実装です。
    /// </summary>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
