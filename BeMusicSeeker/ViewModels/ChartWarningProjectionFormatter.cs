using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal static class ChartWarningProjectionFormatter
{
    internal static string BuildDigestText(
        BMSFile source,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection,
        string installDestination)
    {
        return ChartWarningCollection.BuildDigestText(
            EnumerateProjectedWarnings(source, resourceHealthProjection, hasResourceHealthProjection),
            installDestination);
    }

    internal static string BuildDisplayText(
        BMSFile source,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return ChartWarningCollection.BuildDisplayText(
            EnumerateProjectedWarnings(source, resourceHealthProjection, hasResourceHealthProjection));
    }

    internal static string BuildTooltipText(
        BMSFile source,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return ChartWarningCollection.BuildTooltipText(
            EnumerateProjectedWarnings(source, resourceHealthProjection, hasResourceHealthProjection));
    }

    internal static bool HasHighlightedWarning(
        BMSFile source,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        return ChartWarningCollection.HasAnyHighlightedWarning(
            EnumerateProjectedWarnings(source, resourceHealthProjection, hasResourceHealthProjection));
    }

    private static IEnumerable<ChartWarning> EnumerateProjectedWarnings(
        BMSFile source,
        ResourceHealthWarningProjection resourceHealthProjection,
        bool hasResourceHealthProjection)
    {
        IEnumerable<ChartWarning> sourceWarnings = source?.Warnings.ToStructuredList() ?? Enumerable.Empty<ChartWarning>();
        foreach (ChartWarning warning in sourceWarnings.Where(warning => warning != null && (!hasResourceHealthProjection || warning.Category != ChartWarningCategory.ResourceHealth)))
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
