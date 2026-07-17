using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable install-destination facts captured before a file scan mutates the catalog.
/// </summary>
internal sealed class InstallDestinationCleanupSnapshot
{
    private static readonly InstallDestinationCleanupSnapshot empty = new([]);

    private InstallDestinationCleanupSnapshot(IEnumerable<ChartFile> charts)
    {
        Charts = Array.AsReadOnly((charts ?? [])
            .Where(chart => chart != null)
            .Select(ChartFileProjection.ToImmutableSnapshot)
            .Where(chart => chart != null)
            .ToArray());
    }

    internal static InstallDestinationCleanupSnapshot Empty => empty;

    internal IReadOnlyList<ChartFile> Charts { get; }

    internal static InstallDestinationCleanupSnapshot FromCharts(IEnumerable<ChartFile> charts)
    {
        return (charts ?? []).Any(chart => chart != null)
            ? new InstallDestinationCleanupSnapshot(charts)
            : Empty;
    }
}
