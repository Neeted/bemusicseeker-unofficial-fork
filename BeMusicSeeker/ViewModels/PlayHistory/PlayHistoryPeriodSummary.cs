using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryPeriodSummary
{
    private PlayHistoryPeriodSummary()
    {
    }

    public long FinalizedCount { get; private set; }

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

    /// <summary>FC・PERFECTとbeatorajaのMAXへのクリア更新数を取得します。</summary>
    public int FullComboClearUpdateCount { get; private set; }

    /// <summary>所持・確定状態によらず更新値を数え、実績は確定行または取得元の期間実績から作成します。</summary>
    internal static PlayHistoryPeriodSummary FromRows(
        IEnumerable<PlayHistoryRow> rows,
        PlayHistoryPeriodSummaryOverride summaryOverride = null)
    {
        var summary = new PlayHistoryPeriodSummary();
        foreach (PlayHistoryRow row in rows ?? [])
        {
            if (row == null)
            {
                continue;
            }
            if (row.Finalized)
            {
                summary.FinalizedCount++;
                summary.PlaytimeSeconds += row.PlaytimeSeconds ?? 0;
                summary.JudgeCount += row.JudgeTotal ?? 0;
            }
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
            case Models.LR2.ClearType.MAX:
                FullComboClearUpdateCount++;
                break;
        }
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
