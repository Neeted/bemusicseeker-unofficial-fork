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
        bool canOpenScoreViewer)
    {
        Md5 = md5 ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
        ChartPath = chartPath ?? string.Empty;
        ChartTitle = chartTitle ?? string.Empty;
        CanOpenScoreViewer = canOpenScoreViewer;
    }

    internal string Md5 { get; }

    internal string Sha256 { get; }

    internal string ChartPath { get; }

    internal string ChartTitle { get; }

    internal bool CanOpenBmsIr => !string.IsNullOrWhiteSpace(Md5);

    internal bool CanOpenRepository => !string.IsNullOrWhiteSpace(Sha256);

    internal bool CanOpenExplorer => !string.IsNullOrWhiteSpace(ChartPath);

    internal bool CanOpenScoreViewer { get; }

    internal bool CanCopyMd5 => !string.IsNullOrWhiteSpace(Md5);

    internal bool CanCopySha256 => !string.IsNullOrWhiteSpace(Sha256);

    internal bool HasExternalLinkItem => CanOpenBmsIr || CanOpenRepository;

    internal bool HasLocalChartItem => CanOpenExplorer || CanOpenScoreViewer;

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
        string md5 = chart?.Kind == ChartFileKind.Bms ? GridRowResolver.GetHash(playHistoryRow) : null;
        string sha256 = GridRowResolver.GetRepositorySha256(playHistoryRow);
        bool canOpenScoreViewer = chart?.Kind == ChartFileKind.Bms
            && !string.IsNullOrWhiteSpace(chart.Md5)
            && !string.IsNullOrWhiteSpace(chart.Path);
        state = new PlayHistoryContextMenuState(md5, sha256, chart?.Path, chart?.Title, canOpenScoreViewer);
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
}
