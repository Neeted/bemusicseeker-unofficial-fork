using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryFixInstallationResult
{
    public LibraryMutationDelta MutationDelta { get; } = new LibraryMutationDelta();

    public List<LibraryChartRef> ChartsToRemove { get; } = [];

    public List<BMSFile> MaintenanceTargets { get; } = [];

    public List<ChartFile> MaintenanceCharts { get; } = [];

    public int RequestedCount { get; set; }

    public int MovedCount { get; set; }

    public int DuplicateSkippedCount { get; set; }

    public long TotalMs { get; set; }
}
