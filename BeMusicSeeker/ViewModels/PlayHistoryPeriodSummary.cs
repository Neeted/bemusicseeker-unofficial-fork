using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryPeriodSummary
{
    private PlayHistoryPeriodSummary()
    {
    }

    public string Label { get; private set; }

    public int RowCount { get; private set; }

    public int FinalizedCount { get; private set; }

    public int UnfinalizedCount => RowCount - FinalizedCount;

    public int SummaryEligibleCount { get; private set; }

    public int PlaytimeSeconds { get; private set; }

    public int JudgeCount { get; private set; }

    public int ScoreUpdateCount { get; private set; }

    public int BpUpdateCount { get; private set; }

    public int ClearUpdateCount { get; private set; }

    public int ComboUpdateCount { get; private set; }

    public int NewClearCount { get; private set; }

    public int NewFullComboCount { get; private set; }

    public int NewPerfectCount { get; private set; }

    internal static PlayHistoryPeriodSummary FromRows(string label, IEnumerable<PlayHistoryRow> rows)
    {
        var summary = new PlayHistoryPeriodSummary
        {
            Label = label ?? string.Empty
        };
        foreach (PlayHistoryRow row in rows ?? [])
        {
            if (row == null)
            {
                continue;
            }
            summary.RowCount++;
            if (!row.Finalized)
            {
                continue;
            }

            summary.FinalizedCount++;
            if (!row.IsSummaryEligible)
            {
                continue;
            }

            summary.SummaryEligibleCount++;
            summary.PlaytimeSeconds += row.PlaytimeSeconds ?? 0;
            summary.JudgeCount += row.JudgeTotal ?? 0;
            if (row.BestScoreUpdated)
            {
                summary.ScoreUpdateCount++;
            }
            if (row.BestBpUpdated)
            {
                summary.BpUpdateCount++;
            }
            if (row.BestClearUpdated)
            {
                summary.ClearUpdateCount++;
            }
            if (row.BestComboUpdated)
            {
                summary.ComboUpdateCount++;
            }
            if (IsNewClear(row))
            {
                summary.NewClearCount++;
            }
            if (row.OldBestClear != Models.LR2.ClearType.FC && row.NewBestClear == Models.LR2.ClearType.FC)
            {
                summary.NewFullComboCount++;
            }
            if (row.OldBestClear != Models.LR2.ClearType.PA && row.NewBestClear == Models.LR2.ClearType.PA)
            {
                summary.NewPerfectCount++;
            }
        }

        return summary;
    }

    private static bool IsNewClear(PlayHistoryRow row)
    {
        if (row?.NewBestClear == null || row.NewBestClear < Models.LR2.ClearType.EASY)
        {
            return false;
        }
        return row.OldBestClear == null || row.OldBestClear < Models.LR2.ClearType.EASY;
    }
}
