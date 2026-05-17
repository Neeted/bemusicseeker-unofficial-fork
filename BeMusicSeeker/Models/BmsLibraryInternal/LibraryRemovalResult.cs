using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryRemovalResult
{
    public List<BMSFile> RemovedFiles { get; } = [];

    public List<LibraryChartRef> RemovedCharts { get; } = [];

    public List<LibraryDeleteFailure> Failures { get; } = [];

    public int InputChartCount { get; set; }

    public int CanonicalChartCount { get; set; }

    public int UnresolvedChartCount { get; set; }

    public int PathOnlyInputCount { get; set; }

    public int FolderDeleteCount { get; set; }

    public int FileDeleteCount { get; set; }

    public DirectoryResourceLookupCache.ReverseLookupMutationResult ResourceIndexMutation { get; set; } = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
}
