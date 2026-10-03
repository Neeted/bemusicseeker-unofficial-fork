using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Exposes the live chart rows and selection required by playback navigation.
/// </summary>
internal interface IPlaybackChartQueue
{
    int Count { get; }

    int SelectedIndex { get; set; }

    object GetRow(int index);

    /// <summary>選曲候補の共通譜面を行の寿命内に取得します。</summary>
    ChartFile GetPlaybackChart(int index)
    {
        GridRowResolver.TryGetPlaybackChart(GetRow(index), out ChartFile chart);
        return chart;
    }
}

/// <summary>
/// Adapts the main chart table to the playback navigation boundary.
/// </summary>
internal sealed class MainChartListPlaybackQueue : IPlaybackChartQueue
{
    private readonly MainChartListViewModel chartList;
    private readonly object rowGate = new();
    private bool replacing;

    internal MainChartListPlaybackQueue(MainChartListViewModel chartList)
    {
        this.chartList = chartList ?? throw new ArgumentNullException(nameof(chartList));
        chartList.RowsReplacing += (_, _) =>
        {
            lock (rowGate) { replacing = true; }
        };
        chartList.RowsReplacementCanceled += (_, _) => CompleteRowsReplacement();
        chartList.RowsReplacementPublishFailed += (_, _) => CompleteRowsReplacement();
        chartList.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { CompleteRowsReplacement(); }
        };
    }

    public int Count { get { lock (rowGate) { return replacing ? 0 : chartList.Rows.Count; } } }

    public int SelectedIndex
    {
        get => chartList.SelectedIndex;
        set => chartList.SelectedIndex = value;
    }

    /// <summary>差替え中は旧providerを再参照せず、それ以外は現行行を取得します。</summary>
    public object GetRow(int index)
    {
        lock (rowGate) { return replacing || index < 0 || index >= chartList.Rows.Count ? null : chartList.Rows[index]; }
    }
    /// <summary>交換中のproviderへ再アクセスせず、形式共通の選曲候補を取得します。</summary>
    public ChartFile GetPlaybackChart(int index)
    {
        lock (rowGate)
        {
            if (replacing || index < 0 || index >= chartList.Rows.Count) return null;
            GridRowResolver.TryGetPlaybackChart(chartList.Rows[index], out ChartFile chart);
            return chart;
        }
    }

    private void CompleteRowsReplacement()
    {
        lock (rowGate) { replacing = false; }
    }
}
