using System;

namespace BeMusicSeeker.Models;

internal static class ChartFileIdentity
{
    /// <summary>同じ形式の項目を、所持トークンを優先して照合し、一致しなければ大文字小文字を区別しない物理パスで照合します。確認後の固定対象や所持項目削除の厳密な識別には使用しません。</summary>
    internal static bool IsSameChartTarget(ChartFile chart, ChartFile targetChart)
    {
        if (chart == null || targetChart == null || chart.Kind != targetChart.Kind)
        {
            return false;
        }
        if (ReferenceEquals(chart, targetChart))
        {
            return true;
        }
        if (chart.Token != null && ReferenceEquals(chart.Token, targetChart.Token))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(chart.Path)
            && !string.IsNullOrWhiteSpace(targetChart.Path)
            && chart.Path.Equals(targetChart.Path, StringComparison.OrdinalIgnoreCase);
    }
}
