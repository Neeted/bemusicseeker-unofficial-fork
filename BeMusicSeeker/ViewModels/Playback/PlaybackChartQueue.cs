using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 再生候補の捕捉と、現在一覧での位置照合を提供します。
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

    /// <summary>同じ行から譜面と生存識別を捕捉します。明示行が交換済みなら空候補を返します。</summary>
    PlaybackQueueItem CapturePlaybackItem(int index, object expectedRow = null)
    {
        object row = GetRow(index);
        return expectedRow != null && !ReferenceEquals(row, expectedRow) ? default : PlaybackQueueItem.FromRow(index, row);
    }

    /// <summary>現在一覧で生存識別と種類・実pathが一致する位置を返し、不在なら-1を返します。</summary>
    int IndexOfPlaybackItem(object locator, ChartFile chart);
}

/// <summary>行の寿命内に捕捉した再生譜面と、旧viewを保持しない生存識別です。</summary>
/// <param name="Index">候補を捕捉した表示位置です。</param>
/// <param name="Chart">行から捕捉した現在の譜面です。未所持・対象外はnullです。</param>
/// <param name="Locator">現在一覧での位置照合に使う生存項目の識別です。</param>
internal readonly record struct PlaybackQueueItem(int Index, ChartFile Chart, object Locator)
{
    /// <summary>行を解決し、通常token・package項目・playlist entryを優先して捕捉します。</summary>
    internal static PlaybackQueueItem FromRow(int index, object row)
    {
        GridRowResolver.TryGetPlaybackChart(row, out ChartFile chart);
        if (row is PlaylistDetailRow { IsOwned: false } or PlaylistDetailSourceRow { IsOwned: false }) { chart = null; }
        object locator = GridRowResolver.GetPlaylistEntry(row) ?? (row switch
        {
            ChartListSourceRow source => source.SelectionIdentity,
            LibraryChartRow library => (object)library.PackageEntry ?? (object)chart?.Token ?? chart,
            _ => (object)chart?.Token ?? chart
        });
        return new(index, chart, locator);
    }

    /// <summary>同hash別配置を混同せず、形式と実pathを照合します。</summary>
    internal static bool SameChart(ChartFile left, ChartFile right) => left != null && right != null
        && left.Kind == right.Kind && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 現一覧の行寿命と既存identity索引を再生候補の境界へ接続します。
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
        => CapturePlaybackItem(index).Chart;

    /// <inheritdoc/>
    public PlaybackQueueItem CapturePlaybackItem(int index, object expectedRow = null)
    {
        lock (rowGate)
        {
            if (replacing || index < 0 || index >= chartList.Rows.Count)
            {
                return default;
            }
            object row = chartList.Rows[index];
            if (expectedRow != null && !ReferenceEquals(expectedRow, row)) { return default; }
            var item = PlaybackQueueItem.FromRow(index, row);
            // tokenのないsubsetも、捕捉済みの同じ行から既存索引と同じ識別を構成します。
            // 添字で識別を取り直すと、in-placeの並替え後の別entryと譜面を混ぜるためです。
            return chartList.Rows is ICustomTableSelectionIdentitySource && row is LibraryChartRow && item.Locator is ChartFile chart
                ? item with { Locator = (chart.Kind, chart.Path) } : item;
        }
    }

    /// <inheritdoc/>
    public int IndexOfPlaybackItem(object locator, ChartFile chart)
    {
        lock (rowGate)
        {
            if (replacing || locator == null || chart == null) { return -1; }
            if (chartList.Rows is ICustomTableSelectionIdentitySource source)
            {
                int index = source.IndexOfSelectionIdentity(locator);
                PlaybackQueueItem item = index >= 0 ? CapturePlaybackItem(index) : default;
                return Equals(locator, item.Locator) && PlaybackQueueItem.SameChart(item.Chart, chart) ? index : -1;
            }
            // 非仮想の既存IListは既に実体化済みです。仮想viewの索引不在を別の検索で補いません。
            for (int index = 0; index < chartList.Rows.Count; index++)
            {
                PlaybackQueueItem item = CapturePlaybackItem(index);
                bool sameLocator = locator is ChartFile && item.Locator is ChartFile
                    ? PlaybackQueueItem.SameChart(item.Chart, chart) : Equals(locator, item.Locator);
                if (sameLocator && PlaybackQueueItem.SameChart(item.Chart, chart)) { return index; }
            }
            return -1;
        }
    }

    private void CompleteRowsReplacement()
    {
        lock (rowGate) { replacing = false; }
    }
}
