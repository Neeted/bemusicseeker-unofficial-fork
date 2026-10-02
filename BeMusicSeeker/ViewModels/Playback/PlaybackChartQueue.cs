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

    /// <summary>候補判定に必要なfileを行の寿命内に解決し、workerへproviderを渡しません。フォルダ判定だけなら譜面projectionを省略できます。</summary>
    (BeMusicSeeker.Models.BMSFile PlayerFile, BeMusicSeeker.Models.ChartFile Chart) GetPlaybackFiles(int index, bool includeChart = true)
    {
        object row = GetRow(index);
        GridRowResolver.TryGetBmsPlayerFile(row, out BeMusicSeeker.Models.BMSFile playerFile);
        BeMusicSeeker.Models.ChartFile chart = null;
        if (includeChart) { GridRowResolver.TryGetChartFile(row, out chart); }
        return (playerFile, chart);
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
    /// <summary>差替え開始との短い排他内でprojectionを確定し、破棄後のprovider参照を防ぎます。</summary>
    public (BeMusicSeeker.Models.BMSFile PlayerFile, BeMusicSeeker.Models.ChartFile Chart) GetPlaybackFiles(int index, bool includeChart = true)
    {
        lock (rowGate)
        {
            if (replacing || index < 0 || index >= chartList.Rows.Count) { return (null, null); }
            object row = chartList.Rows[index];
            GridRowResolver.TryGetBmsPlayerFile(row, out BeMusicSeeker.Models.BMSFile playerFile);
            BeMusicSeeker.Models.ChartFile chart = null;
            if (includeChart) { GridRowResolver.TryGetChartFile(row, out chart); }
            return (playerFile, chart);
        }
    }

    private void CompleteRowsReplacement()
    {
        lock (rowGate) { replacing = false; }
    }
}
