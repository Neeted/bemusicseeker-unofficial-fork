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

    public long FinalizedCount { get; private set; }

    public long UnfinalizedCount => RowCount - FinalizedCount;

    public int SummaryEligibleCount { get; private set; }

    public long PlaytimeSeconds { get; private set; }

    public bool PlayCountAvailable { get; private set; } = true;

    public bool JudgeCountAvailable { get; private set; } = true;

    public bool PlaytimeAvailable { get; private set; } = true;

    public long JudgeCount { get; private set; }

    public int ScoreUpdateCount { get; private set; }

    public int BpUpdateCount { get; private set; }

    public int ClearUpdateCount { get; private set; }

    public int ComboUpdateCount { get; private set; }

    public int AssistClearUpdateCount { get; private set; }

    public int EasyClearUpdateCount { get; private set; }

    public int NormalClearUpdateCount { get; private set; }

    public int HardClearUpdateCount { get; private set; }

    public int ExHardClearUpdateCount { get; private set; }

    public int FullComboClearUpdateCount { get; private set; }

    public int NewClearCount { get; private set; }

    public int NewFullComboCount { get; private set; }

    public int NewPerfectCount { get; private set; }

    internal static PlayHistoryPeriodSummary FromRows(
        string label,
        IEnumerable<PlayHistoryRow> rows,
        PlayHistoryPeriodSummaryOverride summaryOverride = null)
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
                summary.AddClearBreakdown(row.NewBestClear);
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
        if (summaryOverride != null)
        {
            summary.PlayCountAvailable = summaryOverride.PlayCount.HasValue;
            summary.FinalizedCount = summaryOverride.PlayCount.GetValueOrDefault();
            summary.JudgeCountAvailable = summaryOverride.JudgeCount.HasValue;
            summary.JudgeCount = summaryOverride.JudgeCount.GetValueOrDefault();
            summary.PlaytimeAvailable = summaryOverride.PlaytimeSeconds.HasValue;
            summary.PlaytimeSeconds = summaryOverride.PlaytimeSeconds.GetValueOrDefault();
        }

        return summary;
    }

    private void AddClearBreakdown(Models.LR2.ClearType? clear)
    {
        switch (clear)
        {
            case Models.LR2.ClearType.INVALID:
            case Models.LR2.ClearType.L_ASSIST:
                AssistClearUpdateCount++;
                break;
            case Models.LR2.ClearType.EASY:
                EasyClearUpdateCount++;
                break;
            case Models.LR2.ClearType.CLEAR:
                NormalClearUpdateCount++;
                break;
            case Models.LR2.ClearType.HARD:
                HardClearUpdateCount++;
                break;
            case Models.LR2.ClearType.EX_HARD:
                ExHardClearUpdateCount++;
                break;
            case Models.LR2.ClearType.FC:
            case Models.LR2.ClearType.PA:
                FullComboClearUpdateCount++;
                break;
        }
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

internal sealed class PlayHistoryPeriodSummaryOverride
{
    internal PlayHistoryPeriodSummaryOverride(long? playCount, long? judgeCount, long? playtimeSeconds)
    {
        PlayCount = playCount;
        JudgeCount = judgeCount;
        PlaytimeSeconds = playtimeSeconds;
    }

    internal long? PlayCount { get; }

    internal long? JudgeCount { get; }

    internal long? PlaytimeSeconds { get; }
}
