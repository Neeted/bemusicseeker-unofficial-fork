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

[System.Flags]
public enum ChartModeFilter
{
    None = 0,
    _5KEYS = 1,
    _7KEYS = 2,
    _9KEYS = 4,
    _10KEYS = 8,
    _14KEYS = 0x10,
    All = 0x1F
}

public enum PlaylistOwnedFilter
{
    All,
    OwnedComplete,
    OwnedIncomplete
}
