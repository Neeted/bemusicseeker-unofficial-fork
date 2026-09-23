using System;
using System.Globalization;

namespace BeMusicSeeker.Diagnostics;

internal readonly struct TableFirstVisibleTiming(int requestVersion, long requestToBuildStartMs, long requestToBuildCompleteMs, long requestToVisibleRenderMs, long buildToVisibleRenderMs, int viewCount)
{
    public int RequestVersion { get; } = requestVersion;

    public long RequestToBuildStartMs { get; } = requestToBuildStartMs;

    public long RequestToBuildCompleteMs { get; } = requestToBuildCompleteMs;

    public long RequestToVisibleRenderMs { get; } = requestToVisibleRenderMs;

    public long BuildToVisibleRenderMs { get; } = buildToVisibleRenderMs;

    public int ViewCount { get; } = viewCount;
}

internal readonly struct TableFirstVisibleMetrics(
    string controlType,
    string trigger,
    long sourceGenerationId,
    long viewGenerationId,
    int rowCount,
    int visibleRowCount,
    int visibleColumnCount,
    int visibleCellCount,
    long firstRenderMs,
    long renderWorkMs,
    double textCacheHitRate,
    long stateLogMs,
    TableFirstVisibleTiming timing,
    bool isPreparationRender = false)
{
    public string ControlType { get; } = controlType ?? string.Empty;

    public string Trigger { get; } = trigger ?? string.Empty;

    public long SourceGenerationId { get; } = sourceGenerationId;

    public long ViewGenerationId { get; } = viewGenerationId;

    public int RowCount { get; } = rowCount;

    public int VisibleRowCount { get; } = visibleRowCount;

    public int VisibleColumnCount { get; } = visibleColumnCount;

    public int VisibleCellCount { get; } = visibleCellCount;

    public long FirstRenderMs { get; } = firstRenderMs;

    public long RenderWorkMs { get; } = renderWorkMs;

    public double TextCacheHitRate { get; } = textCacheHitRate;

    public long StateLogMs { get; } = stateLogMs;

    public TableFirstVisibleTiming Timing { get; } = timing;

    public bool IsPreparationRender { get; } = isPreparationRender;

    public static int CalculateVisibleCellCount(int visibleRowCount, int visibleColumnCount)
    {
        if (visibleRowCount < 0 || visibleColumnCount < 0)
        {
            return -1;
        }
        try
        {
            return checked(visibleRowCount * visibleColumnCount);
        }
        catch (OverflowException)
        {
            return int.MaxValue;
        }
    }
}

internal static class TableFirstVisibleLogFormatter
{
    public static string Format(TableFirstVisibleMetrics metrics)
    {
        TableFirstVisibleTiming timing = metrics.Timing;
        return "table_first_visible"
            + " controlType=" + metrics.ControlType
            + " trigger=" + metrics.Trigger
            + " requestVersion=" + timing.RequestVersion
            + " sourceGenerationId=" + metrics.SourceGenerationId
            + " viewGenerationId=" + metrics.ViewGenerationId
            + " rowCount=" + metrics.RowCount
            + " visibleRowCount=" + metrics.VisibleRowCount
            + " visibleColumnCount=" + metrics.VisibleColumnCount
            + " visibleCellCount=" + metrics.VisibleCellCount
            + " isPreparationRender=" + metrics.IsPreparationRender
            + " requestToBuildStartMs=" + timing.RequestToBuildStartMs
            + " requestToBuildCompleteMs=" + timing.RequestToBuildCompleteMs
            + " requestToVisibleRenderMs=" + timing.RequestToVisibleRenderMs
            + " buildToVisibleRenderMs=" + timing.BuildToVisibleRenderMs
            + " firstRenderMs=" + metrics.FirstRenderMs
            + " renderWorkMs=" + metrics.RenderWorkMs
            + " textCacheHitRate=" + metrics.TextCacheHitRate.ToString(CultureInfo.InvariantCulture)
            + " viewCount=" + timing.ViewCount
            + " stateLogMs=" + metrics.StateLogMs;
    }
}
