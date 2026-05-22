using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal static class ChartWarningProjectionFormatter
{
    internal static string BuildDigestText(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection,
        bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        return ChartWarningCollection.BuildDigestText(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection),
            chart?.InstallDestination,
            hideResourceHealthDigestWhenInstallDestinationSet);
    }

    internal static string BuildDisplayText(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return ChartWarningCollection.BuildDisplayText(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection));
    }

    internal static string BuildTooltipText(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return ChartWarningCollection.BuildTooltipText(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection));
    }

    internal static bool HasHighlightedWarning(
        ChartFile chart,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return ChartWarningCollection.HasAnyHighlightedWarning(
            EnumerateProjectedWarnings(chart?.Warnings, resourceHealthProjection, hasResourceHealthProjection));
    }

    private static IEnumerable<ChartWarning> EnumerateProjectedWarnings(
        IEnumerable<ChartWarning> sourceWarnings,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        foreach (ChartWarning warning in (sourceWarnings ?? Enumerable.Empty<ChartWarning>()).Where(warning => warning != null && (!hasResourceHealthProjection || warning.Category != ChartWarningCategory.ResourceHealth)))
        {
            yield return warning;
        }

        if (!hasResourceHealthProjection)
        {
            yield break;
        }

        foreach (ChartWarning warning in resourceHealthProjection?.Warnings ?? Enumerable.Empty<ChartWarning>())
        {
            if (warning != null)
            {
                yield return warning;
            }
        }
    }
}
