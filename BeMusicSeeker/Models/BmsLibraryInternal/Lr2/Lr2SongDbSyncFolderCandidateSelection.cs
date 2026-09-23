namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncFolderCandidateSelection(
    Lr2FolderFileCandidateSnapshot candidates,
    string source,
    int enumeratedAppManagedCandidateCount,
    int enumeratedAppManagedExactFileCount)
{
    public Lr2FolderFileCandidateSnapshot Candidates { get; } = candidates;

    public string Source { get; } = source;

    public int EnumeratedAppManagedCandidateCount { get; } = enumeratedAppManagedCandidateCount;

    public int EnumeratedAppManagedExactFileCount { get; } = enumeratedAppManagedExactFileCount;
}
