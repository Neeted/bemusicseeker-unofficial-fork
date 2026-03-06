using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DuplicateAnalysisResult
{
    public HashSet<BMSFile> DuplicateFiles { get; } = new HashSet<BMSFile>();

    public List<DuplicateGroup> DuplicateGroups { get; } = new List<DuplicateGroup>();
}
