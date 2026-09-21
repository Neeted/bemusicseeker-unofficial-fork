using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartResourceHealthRequest
{
    private ChartResourceHealthRequest(IReadOnlyList<ChartFile> charts)
    {
        Charts = charts ?? throw new ArgumentNullException(nameof(charts));
    }

    internal IReadOnlyList<ChartFile> Charts { get; }

    internal bool HasTargets => Charts.Count > 0;

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, out ChartResourceHealthRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        List<ChartFile> charts = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck))
            .Select(target => target.Chart)];
        if (charts.Count == 0)
        {
            return false;
        }

        request = new ChartResourceHealthRequest(charts);
        return true;
    }
}
