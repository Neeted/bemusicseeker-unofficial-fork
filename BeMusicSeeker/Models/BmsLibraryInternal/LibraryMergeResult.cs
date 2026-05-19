using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryMergeResult
{
    public bool Success { get; set; }

    public List<BMSFile> SourceBmsFiles { get; } = [];

    public List<LR2.LR2SongDBExtended.bmson_song> SourceBmsonSongs { get; } = [];

    public ChartPackage Repackage { get; set; }

    public HashSet<string> ExistingHashes { get; set; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    public LibraryMutationDelta ReferenceMutationDelta { get; } = new LibraryMutationDelta();
}
