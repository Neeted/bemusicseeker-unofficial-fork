using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryMergeResult
{
    public bool Success { get; set; }

    public List<LibraryChartRef> SourceCharts { get; } = [];

    public ChartPackage Repackage { get; set; }

    public IPrimaryHashLookup ExistingHashes { get; set; } = EmptyPrimaryHashLookup.Instance;

    public LibraryMutationDelta ReferenceMutationDelta { get; } = new LibraryMutationDelta();
}
