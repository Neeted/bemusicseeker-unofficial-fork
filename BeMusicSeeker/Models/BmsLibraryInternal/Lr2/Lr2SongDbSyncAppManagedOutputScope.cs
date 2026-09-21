using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncAppManagedOutputScope(
    IReadOnlyList<string> directories,
    IReadOnlyList<string> filePaths,
    IReadOnlyList<string> pruneExcludedPaths,
    bool isComplete)
{
    public IReadOnlyList<string> Directories { get; } = directories ?? [];

    public IReadOnlyList<string> FilePaths { get; } = filePaths ?? [];

    public IReadOnlyList<string> PruneExcludedPaths { get; } = pruneExcludedPaths ?? [];

    public bool IsComplete { get; } = isComplete;
}
