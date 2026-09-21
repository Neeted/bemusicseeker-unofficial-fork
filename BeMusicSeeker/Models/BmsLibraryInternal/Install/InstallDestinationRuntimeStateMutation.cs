using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Describes the install-destination runtime state changes produced by a catalog mutation.
/// </summary>
internal sealed class InstallDestinationRuntimeStateMutation
{
    internal List<LibraryChartPathChange> PathChanges { get; } = [];

    internal List<ChartFile> AppliedCharts { get; } = [];

    internal bool PruneToCurrentOwnedCharts { get; set; }

    internal bool HasStateChanges => PathChanges.Count > 0 || AppliedCharts.Count > 0;

    internal bool HasChanges => HasStateChanges || PruneToCurrentOwnedCharts;
}
