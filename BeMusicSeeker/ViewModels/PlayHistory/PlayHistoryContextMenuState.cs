using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryContextMenuState
{
    private PlayHistoryContextMenuState(
        string md5,
        string sha256,
        string chartPath,
        string chartTitle,
        ChartFile resolvedChart,
        ScoreViewerCandidate scoreViewerCandidate,
        bool hasResolvedChart,
        ExternalChartKind? chartKind)
    {
        Md5 = md5 ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
        ChartPath = chartPath ?? string.Empty;
        ChartTitle = chartTitle ?? string.Empty;
        this.resolvedChart = resolvedChart;
        ScoreViewerCandidate = scoreViewerCandidate;
        HasResolvedChart = hasResolvedChart;
        ChartKind = chartKind;
    }

    private readonly ChartFile resolvedChart;

    /// <summary>履歴行に取得済みのMD5です。SHA256や取得元の生hashで補いません。</summary>
    internal string Md5 { get; }

    /// <summary>履歴行に取得済みのSHA256です。追加の譜面読込みを行いません。</summary>
    internal string Sha256 { get; }

    internal string ChartPath { get; }

    internal string ChartTitle { get; }

    internal bool CanOpenExplorer => !string.IsNullOrWhiteSpace(ChartPath);

    /// <summary>Gets whether the resolved play-history row can use the associated-file terminal.</summary>
    internal bool CanOpenAssociated => HasResolvedChart
        && !string.IsNullOrWhiteSpace(ChartPath)
        && System.IO.Path.IsPathFullyQualified(ChartPath);

    /// <summary>
    /// 右クリックした行1件の譜面ビューアの候補です。解決済みの行は譜面の種類・MD5・パス、
    /// 未解決のLR2行はBMS・履歴のMD5・パスなし、未解決のbeatoraja行は種類を確定できないため対象外です。
    /// </summary>
    internal ScoreViewerCandidate ScoreViewerCandidate { get; }

    /// <summary>譜面ビューアの共通判定で「開く」を有効に表示するかを返します。ファイルの実在は要件にしません。</summary>
    internal bool CanOpenScoreViewer => ResolveScoreViewerDecision().Mode == ScoreViewerMenuMode.Open;

    /// <summary>右クリックした行1件の候補を、複数選択中でも他の行を含めず、共通判定へ渡します。</summary>
    internal ScoreViewerMenuDecision ResolveScoreViewerDecision()
        => ScoreViewerMenuDecision.Resolve([ScoreViewerCandidate]);

    internal bool HasResolvedChart { get; }

    /// <summary>譜面種別を取得します。履歴の取得元だけでは BMS/bmson を区別できない場合は null です。</summary>
    internal ExternalChartKind? ChartKind { get; }

    internal bool HasLocalChartItem => CanOpenAssociated || CanOpenScoreViewer;

    /// <summary>履歴行の取得済みhashとローカル能力を捕捉します。表示する項目の有無は設定Web操作・ローカル操作・日付選択の各判断へ委ねます。</summary>
    internal static bool TryCreate(object row, out PlayHistoryContextMenuState state)
    {
        state = null;
        if (row is not PlayHistoryRow playHistoryRow)
        {
            return false;
        }

        ChartFile chart = playHistoryRow.ResolvedChart;
        string md5 = playHistoryRow.Md5;
        string sha256 = playHistoryRow.Sha256;
        ScoreViewerCandidate scoreViewerCandidate = chart != null
            ? ScoreViewerCandidate.FromChart(chart)
            : playHistoryRow.Provider == PlayHistoryProvider.Lr2
                ? new ScoreViewerCandidate(ChartFileKind.Bms, md5, Path: null, Title: null)
                : ScoreViewerCandidate.NotTarget;
        ExternalChartKind? chartKind = chart != null
            ? chart.Kind == ChartFileKind.Bmson ? ExternalChartKind.BmsonOnly : ExternalChartKind.BmsOnly
            : playHistoryRow.Provider == PlayHistoryProvider.Lr2 ? ExternalChartKind.BmsOnly : null;
        state = new PlayHistoryContextMenuState(
            md5,
            sha256,
            chart?.Path,
            chart?.Title,
            chart,
            scoreViewerCandidate,
            chart != null,
            chartKind);
        return true;
    }

    /// <summary>履歴行の本来のMD5/SHA256と、実在時だけのローカルpathを設定操作の解決へ渡します。</summary>
    internal RightClickActionResolutionInput CreateResolutionInput(Func<string, bool> fileExists)
    {
        string localPath = HasResolvedChart
            && !string.IsNullOrWhiteSpace(ChartPath)
            && System.IO.Path.IsPathFullyQualified(ChartPath)
            && fileExists != null
            && fileExists(ChartPath)
                ? ChartPath
                : null;
        return new RightClickActionResolutionInput(Md5, Sha256, localPath, ChartKind);
    }

    /// <summary>Builds the exact chart operation target used by the associated-open terminal.</summary>
    internal ChartOperationTarget CreateAssociatedOpenTarget()
    {
        if (!CanOpenAssociated || resolvedChart == null)
        {
            return null;
        }

        return new ChartOperationTarget(
            resolvedChart,
            playlistEntry: null,
            ChartOperationSourceScope.PlayHistory,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.OpenFile | ChartOperationCapabilities.OpenFolder);
    }

}

internal enum PlayHistoryContextMenuActionKind
{
    OpenAssociated,
    OpenExplorer,
    RegisterScoreViewer
}

internal sealed class PlayHistoryContextMenuAction
{
    private PlayHistoryContextMenuAction(
        string path,
        ScoreViewerMenuDecision scoreViewerDecision)
    {
        Path = path;
        ScoreViewerDecision = scoreViewerDecision;
    }

    internal string Path { get; }

    /// <summary>譜面ビューアの共通判定の結果です。パスだけの操作では null です。</summary>
    internal ScoreViewerMenuDecision ScoreViewerDecision { get; }

    internal static PlayHistoryContextMenuAction ForPath(string path)
    {
        return new PlayHistoryContextMenuAction(path, null);
    }

    internal static PlayHistoryContextMenuAction ForScoreViewer(ScoreViewerMenuDecision scoreViewerDecision)
    {
        return new PlayHistoryContextMenuAction(null, scoreViewerDecision);
    }
}
