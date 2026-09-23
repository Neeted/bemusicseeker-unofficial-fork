using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartLibraryMoveRequest
{
    private ChartLibraryMoveRequest(IReadOnlyList<LibraryChartRef> charts, string newParentDirectory)
    {
        Charts = charts ?? throw new ArgumentNullException(nameof(charts));
        NewParentDirectory = newParentDirectory;
    }

    internal IReadOnlyList<LibraryChartRef> Charts { get; }

    internal string NewParentDirectory { get; }

    internal bool HasTargets => Charts.Count > 0;

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, string newParentDirectory, out ChartLibraryMoveRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        List<LibraryChartRef> charts = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.MoveInLibrary)
                && !string.IsNullOrWhiteSpace(target.Chart.Path))
            .Select(target => target.ToLibraryChartRef())
            .Where(chart => !string.IsNullOrWhiteSpace(chart?.Path))];
        if (charts.Count == 0)
        {
            return false;
        }

        request = new ChartLibraryMoveRequest(charts, newParentDirectory);
        return true;
    }
}
