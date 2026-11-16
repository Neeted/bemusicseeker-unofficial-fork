using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Describes the active chart-list sort independently of the shell ViewModel.
/// </summary>
public class ChartListSortParameters
{
    public string ColumnsName { get; set; } = string.Empty;

    public ListSortDirection Direction { get; set; }
}

/// <summary>一覧で選択する鍵盤モードです。Allは未列挙のモードも含む全表示を表します。</summary>
[System.Flags]
public enum ChartModeFilter
{
    None = 0,
    _5KEYS = 1,
    _7KEYS = 2,
    _9KEYS = 4,
    _10KEYS = 8,
    _14KEYS = 0x10,
    _24KEYS = 0x20,
    _48KEYS = 0x40,
    All = _5KEYS | _7KEYS | _9KEYS | _10KEYS | _14KEYS | _24KEYS | _48KEYS
}

/// <summary>通常一覧とPlaylist詳細で共通のモード一致規則を提供します。</summary>
internal static class ChartModeFilterExtensions
{
    /// <summary>Allはすべて、部分選択は選択した一覧モードとnullを通します。詳細解析の25/50へ変換しません。</summary>
    internal static bool Matches(this ChartModeFilter filter, int? mode)
    {
        if (filter == ChartModeFilter.All || mode == null) { return true; }
        ChartModeFilter flag = mode switch
        {
            5 => ChartModeFilter._5KEYS,
            7 => ChartModeFilter._7KEYS,
            9 => ChartModeFilter._9KEYS,
            10 => ChartModeFilter._10KEYS,
            14 => ChartModeFilter._14KEYS,
            24 => ChartModeFilter._24KEYS,
            48 => ChartModeFilter._48KEYS,
            _ => ChartModeFilter.None
        };
        return (filter & flag) != ChartModeFilter.None;
    }
}

public enum PlaylistOwnedFilter
{
    All,
    OwnedComplete,
    OwnedIncomplete
}
