using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Describes the sort indicator presented by the main chart table.
/// </summary>
public sealed class MainChartListSortPresentation
{
    internal MainChartListSortPresentation(string columnsName, ListSortDirection direction)
    {
        ColumnsName = columnsName ?? string.Empty;
        Direction = direction;
    }

    /// <summary>
    /// Gets the member name displayed as the active sort column.
    /// </summary>
    public string ColumnsName { get; }

    /// <summary>
    /// Gets the direction displayed by the active sort column.
    /// </summary>
    public ListSortDirection Direction { get; }
}
