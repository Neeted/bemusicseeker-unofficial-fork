using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Exposes the live chart rows and selection required by playback navigation.
/// </summary>
internal interface IPlaybackChartQueue
{
    int Count { get; }

    int SelectedIndex { get; set; }

    object GetRow(int index);
}

/// <summary>
/// Adapts the main chart table to the playback navigation boundary.
/// </summary>
internal sealed class MainChartListPlaybackQueue : IPlaybackChartQueue
{
    private readonly MainChartListViewModel chartList;

    internal MainChartListPlaybackQueue(MainChartListViewModel chartList)
    {
        this.chartList = chartList ?? throw new ArgumentNullException(nameof(chartList));
    }

    public int Count => chartList.Rows.Count;

    public int SelectedIndex
    {
        get => chartList.SelectedIndex;
        set => chartList.SelectedIndex = value;
    }

    public object GetRow(int index)
    {
        return chartList.Rows[index];
    }
}
