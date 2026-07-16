using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileDiffPreparationResult(
    Lr2SongDbSyncRequest request,
    IReadOnlyList<string> appManagedOutputDirectories,
    IReadOnlyList<string> appManagedOutputFilePaths,
    IReadOnlyList<string> appManagedPruneExcludedPaths,
    CustomFolderOutputPhysicalSurface appManagedPhysicalSurface,
    int appManagedCandidateCount,
    long rootsMs,
    long builtinSourceMs,
    long appManagedScopeMs,
    long filterMs,
    long parentSurfaceMs,
    long extraTextRootsMs,
    long textMetadataMs,
    long totalElapsedMs)
{
    public Lr2SongDbSyncRequest Request { get; } = request;

    public IReadOnlyList<string> AppManagedOutputDirectories { get; } = appManagedOutputDirectories ?? [];

    public IReadOnlyList<string> AppManagedOutputFilePaths { get; } = appManagedOutputFilePaths ?? [];

    public IReadOnlyList<string> AppManagedPruneExcludedPaths { get; } = appManagedPruneExcludedPaths ?? [];

    public CustomFolderOutputPhysicalSurface AppManagedPhysicalSurface { get; } =
        appManagedPhysicalSurface ?? CustomFolderOutputPhysicalSurface.Empty;

    public int AppManagedCandidateCount { get; } = appManagedCandidateCount;

    public long RootsMs { get; } = rootsMs;

    public long BuiltinSourceMs { get; } = builtinSourceMs;

    public long AppManagedScopeMs { get; } = appManagedScopeMs;

    public long FilterMs { get; } = filterMs;

    public long ParentSurfaceMs { get; } = parentSurfaceMs;

    public long ExtraTextRootsMs { get; } = extraTextRootsMs;

    public long TextMetadataMs { get; } = textMetadataMs;

    public long TotalElapsedMs { get; } = totalElapsedMs;
}
