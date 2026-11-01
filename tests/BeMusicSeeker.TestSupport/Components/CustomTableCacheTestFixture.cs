using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>分割した同機能caseが実際に共用する入力・観測のhelperだけをまとめます。</summary>
internal static class CustomTableCacheTestFixture
{

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static CustomTableColumn CreateColumn(string id, int width)
    {
        var layout = new CustomTableColumnSettings.ColumnLayout
        {
            Width = width,
            Visibility = Visibility.Visible
        };
        return new CustomTableColumn(id, id, layout, 0, null, TextAlignment.Left, row => id);
    }

}
