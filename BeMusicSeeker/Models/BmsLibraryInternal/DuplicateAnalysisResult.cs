using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DuplicateAnalysisResult
{
    public HashSet<BMSFile> DuplicateFiles { get; } = [];

    public List<DuplicateGroup> DuplicateGroups { get; } = [];
}
