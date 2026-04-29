using System;
using System.Globalization;

namespace BeMusicSeeker.Diagnostics;

internal readonly struct TableFirstVisibleTiming
{
    public TableFirstVisibleTiming(int requestVersion, long requestToBuildStartMs, long requestToBuildCompleteMs, long requestToVisibleRenderMs, long buildToVisibleRenderMs, int viewCount)
    {
        RequestVersion = requestVersion;
        RequestToBuildStartMs = requestToBuildStartMs;
        RequestToBuildCompleteMs = requestToBuildCompleteMs;
        RequestToVisibleRenderMs = requestToVisibleRenderMs;
        BuildToVisibleRenderMs = buildToVisibleRenderMs;
        ViewCount = viewCount;
    }

    public int RequestVersion { get; }

    public long RequestToBuildStartMs { get; }

    public long RequestToBuildCompleteMs { get; }

    public long RequestToVisibleRenderMs { get; }

    public long BuildToVisibleRenderMs { get; }

    public int ViewCount { get; }
}

internal readonly struct TableFirstVisibleMetrics
{
    public TableFirstVisibleMetrics(
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
        ControlType = controlType ?? string.Empty;
        Trigger = trigger ?? string.Empty;
        SourceGenerationId = sourceGenerationId;
        ViewGenerationId = viewGenerationId;
        RowCount = rowCount;
        VisibleRowCount = visibleRowCount;
        VisibleColumnCount = visibleColumnCount;
        VisibleCellCount = visibleCellCount;
        FirstRenderMs = firstRenderMs;
        RenderWorkMs = renderWorkMs;
        TextCacheHitRate = textCacheHitRate;
        StateLogMs = stateLogMs;
        Timing = timing;
        IsPreparationRender = isPreparationRender;
    }

    public string ControlType { get; }

    public string Trigger { get; }

    public long SourceGenerationId { get; }

    public long ViewGenerationId { get; }

    public int RowCount { get; }

    public int VisibleRowCount { get; }

    public int VisibleColumnCount { get; }

    public int VisibleCellCount { get; }

    public long FirstRenderMs { get; }

    public long RenderWorkMs { get; }

    public double TextCacheHitRate { get; }

    public long StateLogMs { get; }

    public TableFirstVisibleTiming Timing { get; }

    public bool IsPreparationRender { get; }

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
