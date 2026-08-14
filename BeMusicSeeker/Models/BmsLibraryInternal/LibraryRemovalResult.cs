using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryRemovalResult
{
    public List<LibraryChartRef> RemovedCharts { get; } = [];

    public List<LibraryDeleteFailure> Failures { get; } = [];

    /// <summary>
    /// Gets directories whose filesystem deletion completed successfully.
    /// The operation owner uses these facts to update the current resource index
    /// after the filesystem service returns.
    /// </summary>
    public List<string> DeletedFolderPaths { get; } = [];

    public LibraryMutationDelta MutationDelta { get; } = new();

    public int InputChartCount { get; set; }

    public int CanonicalChartCount { get; set; }

    public int UnresolvedChartCount { get; set; }

    public int PathOnlyInputCount { get; set; }

    public int FolderDeleteCount { get; set; }

    public int FileDeleteCount { get; set; }

}
