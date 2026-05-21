using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

internal sealed class ChartInfoDisplaySnapshot
{
    internal static readonly ChartInfoDisplaySnapshot Empty = new(null);

    private ChartInfoDisplaySnapshot(LR2SongDBExtended.chart_info chartInfo)
    {
        ChartLevelText = ChartInfoDisplayFormatter.FormatOptionalInt(chartInfo?.level);
        ChartLevelSortKey = chartInfo?.level ?? 0;
        ChartLevelUndefined = chartInfo == null || !chartInfo.level.HasValue;
        ChartDifficultyText = ChartInfoDisplayFormatter.FormatDifficulty(chartInfo?.difficulty);
        ChartDifficultySortKey = chartInfo?.difficulty;
        ChartDifficultyColorKey = ChartInfoDisplayFormatter.GetDifficultyColorKey(chartInfo?.difficulty);
        ChartDifficultyUndefined = chartInfo == null || !chartInfo.difficulty_defined;
        ChartMainBpmText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.mainbpm);
        ChartMainBpmSortKey = chartInfo?.mainbpm;
        ChartMaxBpmText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.maxbpm);
        ChartMaxBpmSortKey = chartInfo?.maxbpm;
        ChartMinBpmText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.minbpm);
        ChartMinBpmSortKey = chartInfo?.minbpm;
        ChartDurationText = ChartInfoDisplayFormatter.FormatDuration(chartInfo?.length);
        ChartDurationSortKey = chartInfo?.length;
        ChartJudgeText = ChartInfoDisplayFormatter.FormatJudge(chartInfo?.judge);
        ChartJudgeSortKey = chartInfo?.judge;
        ChartJudgeColorKey = ChartInfoDisplayFormatter.GetJudgeColorKey(chartInfo?.judge);
        ChartJudgePercentText = ChartInfoDisplayFormatter.FormatOptionalInt(chartInfo?.judge);
        ChartFeatureText = chartInfo == null ? string.Empty : ChartInfoDisplayFormatter.FormatFeature(chartInfo.feature);
        ChartFeatureSortKey = chartInfo?.feature;
        ChartNotes = chartInfo?.notes;
        ChartLongNotes = chartInfo?.ln;
        ChartScratchNotes = ChartInfoDisplayFormatter.GetScratchNotes(chartInfo);
        ChartTotalText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.total);
        ChartTotalSortKey = chartInfo?.total;
        ChartTotalUndefined = chartInfo == null || !chartInfo.total_defined;
        ChartTotalPerNoteText = ChartInfoDisplayFormatter.FormatFixedTwo(ChartInfoDisplayFormatter.GetTotalPerNote(chartInfo));
        ChartTotalPerNoteSortKey = ChartInfoDisplayFormatter.GetTotalPerNote(chartInfo);
        ChartDensityText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.density);
        ChartDensitySortKey = chartInfo?.density;
        ChartPeakDensityText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.peakdensity);
        ChartPeakDensitySortKey = chartInfo?.peakdensity;
        ChartEndDensityText = ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.enddensity);
        ChartEndDensitySortKey = chartInfo?.enddensity;
        ChartSoflanCount = chartInfo?.speedchange_count;
    }

    internal string ChartLevelText { get; }

    internal double? ChartLevelSortKey { get; }

    internal bool ChartLevelUndefined { get; }

    internal string ChartDifficultyText { get; }

    internal int? ChartDifficultySortKey { get; }

    internal string ChartDifficultyColorKey { get; }

    internal bool ChartDifficultyUndefined { get; }

    internal string ChartMainBpmText { get; }

    internal double? ChartMainBpmSortKey { get; }

    internal string ChartMaxBpmText { get; }

    internal double? ChartMaxBpmSortKey { get; }

    internal string ChartMinBpmText { get; }

    internal double? ChartMinBpmSortKey { get; }

    internal string ChartDurationText { get; }

    internal int? ChartDurationSortKey { get; }

    internal string ChartJudgeText { get; }

    internal int? ChartJudgeSortKey { get; }

    internal string ChartJudgeColorKey { get; }

    internal string ChartJudgePercentText { get; }

    internal string ChartFeatureText { get; }

    internal int? ChartFeatureSortKey { get; }

    internal int? ChartNotes { get; }

    internal int? ChartLongNotes { get; }

    internal int? ChartScratchNotes { get; }

    internal string ChartTotalText { get; }

    internal double? ChartTotalSortKey { get; }

    internal bool ChartTotalUndefined { get; }

    internal string ChartTotalPerNoteText { get; }

    internal double? ChartTotalPerNoteSortKey { get; }

    internal string ChartDensityText { get; }

    internal double? ChartDensitySortKey { get; }

    internal string ChartPeakDensityText { get; }

    internal double? ChartPeakDensitySortKey { get; }

    internal string ChartEndDensityText { get; }

    internal double? ChartEndDensitySortKey { get; }

    internal int? ChartSoflanCount { get; }

    internal static ChartInfoDisplaySnapshot FromChartInfo(LR2SongDBExtended.chart_info chartInfo)
    {
        return chartInfo == null ? Empty : new ChartInfoDisplaySnapshot(chartInfo);
    }
}
