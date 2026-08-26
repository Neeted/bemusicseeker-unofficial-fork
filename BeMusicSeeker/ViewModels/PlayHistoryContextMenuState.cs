using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryContextMenuState
{
    internal const string CopyMd5Kind = "Md5";
    internal const string CopySha256Kind = "Sha256";

    private PlayHistoryContextMenuState(
        string md5,
        string sha256,
        string chartPath,
        string chartTitle,
        ChartFile resolvedChart,
        bool canOpenScoreViewer,
        bool hasResolvedChart,
        ExternalChartKind chartKind)
    {
        Md5 = md5 ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
        ChartPath = chartPath ?? string.Empty;
        ChartTitle = chartTitle ?? string.Empty;
        this.resolvedChart = resolvedChart;
        CanOpenScoreViewer = canOpenScoreViewer;
        HasResolvedChart = hasResolvedChart;
        ChartKind = chartKind;
    }

    private readonly ChartFile resolvedChart;

    internal string Md5 { get; }

    internal string Sha256 { get; }

    internal string ChartPath { get; }

    internal string ChartTitle { get; }

    internal bool CanOpenBmsIr => IsValidBmsIrHash(Md5);

    internal bool CanOpenRepository => !string.IsNullOrWhiteSpace(Sha256);

    internal bool CanOpenExplorer => !string.IsNullOrWhiteSpace(ChartPath);

    /// <summary>Gets whether the resolved play-history row can use the associated-file terminal.</summary>
    internal bool CanOpenAssociated => HasResolvedChart
        && !string.IsNullOrWhiteSpace(ChartPath)
        && System.IO.Path.IsPathFullyQualified(ChartPath);

    internal bool CanOpenScoreViewer { get; }

    internal bool HasResolvedChart { get; }

    internal ExternalChartKind ChartKind { get; }

    internal bool CanCopyMd5 => !string.IsNullOrWhiteSpace(Md5);

    internal bool CanCopySha256 => !string.IsNullOrWhiteSpace(Sha256);

    internal bool HasExternalLinkItem => CanOpenBmsIr || CanOpenRepository;

    internal bool HasLocalChartItem => CanOpenAssociated || CanOpenScoreViewer;

    internal bool HasHashCopyItem => CanCopyMd5 || CanCopySha256;

    internal bool HasVisibleItem => HasExternalLinkItem || HasLocalChartItem || HasHashCopyItem;

    internal static bool TryCreate(object row, out PlayHistoryContextMenuState state)
    {
        state = null;
        if (row is not PlayHistoryRow playHistoryRow)
        {
            return false;
        }

        ChartFile chart = playHistoryRow.ResolvedChart;
        string md5 = chart?.Kind == ChartFileKind.Bmson
            ? null
            : FirstNonEmpty(GridRowResolver.GetHash(playHistoryRow), playHistoryRow.Md5, playHistoryRow.RawHash);
        string sha256 = FirstNonEmpty(
            GridRowResolver.GetRepositorySha256(playHistoryRow),
            playHistoryRow.Sha256,
            chart?.Sha256,
            chart?.ChartInfo?.sha256);
        bool canOpenScoreViewer = chart?.Kind == ChartFileKind.Bms
            && !string.IsNullOrWhiteSpace(chart.Md5)
            && !string.IsNullOrWhiteSpace(chart.Path);
        state = new PlayHistoryContextMenuState(
            md5,
            sha256,
            chart?.Path,
            chart?.Title,
            chart,
            canOpenScoreViewer,
            chart != null,
            chart?.Kind == ChartFileKind.Bmson ? ExternalChartKind.BmsonOnly : ExternalChartKind.BmsOnly);
        if (state.HasVisibleItem)
        {
            return true;
        }

        state = null;
        return false;
    }

    internal string GetCopyValue(string kind)
    {
        return kind switch
        {
            CopyMd5Kind => CanCopyMd5 ? Md5 : null,
            CopySha256Kind => CanCopySha256 ? Sha256 : null,
            _ => null
        };
    }

    internal static bool IsValidBmsIrHash(string md5)
    {
        return !string.IsNullOrWhiteSpace(md5)
            && System.Text.RegularExpressions.Regex.IsMatch(md5.Trim(), "^[A-F0-9]{32}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

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

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }
}

internal enum PlayHistoryContextMenuActionKind
{
    OpenBmsIr,
    OpenMocha,
    OpenMinIr,
    OpenAssociated,
    OpenExplorer,
    RegisterScoreViewer,
    CopyMd5,
    CopySha256
}

internal sealed class PlayHistoryContextMenuAction
{
    private PlayHistoryContextMenuAction(
        string url,
        string value,
        string path,
        ScoreViewerTarget scoreViewerTarget)
    {
        Url = url;
        Value = value;
        Path = path;
        ScoreViewerTarget = scoreViewerTarget;
    }

    internal string Url { get; }

    internal string Value { get; }

    internal string Path { get; }

    internal ScoreViewerTarget ScoreViewerTarget { get; }

    internal static PlayHistoryContextMenuAction ForUrl(string url)
    {
        return new PlayHistoryContextMenuAction(url, null, null, null);
    }

    internal static PlayHistoryContextMenuAction ForValue(string value)
    {
        return new PlayHistoryContextMenuAction(null, value, null, null);
    }

    internal static PlayHistoryContextMenuAction ForPath(string path)
    {
        return new PlayHistoryContextMenuAction(null, null, path, null);
    }

    internal static PlayHistoryContextMenuAction ForScoreViewer(ScoreViewerTarget scoreViewerTarget)
    {
        return new PlayHistoryContextMenuAction(null, null, null, scoreViewerTarget);
    }
}
