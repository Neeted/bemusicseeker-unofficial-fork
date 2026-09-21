using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Resolves chart operation targets from selected row objects without depending on WPF controls.
/// </summary>
internal static class ChartOperationTargetSelectionResolver
{
    /// <summary>
    /// Resolves selected chart operation targets for the requested source scope and capability.
    /// </summary>
    internal static List<ChartOperationTarget> Resolve(ChartOperationTargetSelectionRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return [.. request.Rows
            .Select(row => ResolveRow(row, request.SourceScope))
            .Where(target => HasRequiredCapability(target, request.RequiredCapability))];
    }

    private static ChartOperationTarget ResolveRow(object row, ChartOperationSourceScope sourceScope)
    {
        if (row == null)
        {
            throw new ArgumentException("Selected row collection must not contain null rows.", nameof(row));
        }

        return GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target) ? target : null;
    }

    private static bool HasRequiredCapability(ChartOperationTarget target, ChartOperationCapabilities capability)
    {
        return target != null
            && (capability == ChartOperationCapabilities.None || target.HasCapability(capability));
    }
}
