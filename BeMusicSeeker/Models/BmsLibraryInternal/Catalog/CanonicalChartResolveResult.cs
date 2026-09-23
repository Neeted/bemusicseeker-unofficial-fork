using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class CanonicalChartResolveResult
{
    public int InputCount { get; set; }

    public int PathOnlyInputCount { get; set; }

    public List<LibraryChartRef> CanonicalCharts { get; } = [];

    public List<LibraryChartRef> UnresolvedCharts { get; } = [];
}
