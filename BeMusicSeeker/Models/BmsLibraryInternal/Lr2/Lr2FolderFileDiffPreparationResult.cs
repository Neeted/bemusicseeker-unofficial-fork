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
    long totalElapsedMs,
    SongTableFileCheckResult fileCheckResult = null)
{
    public Lr2SongDbSyncRequest Request { get; } = request;

    /// <summary>
    /// Gets the file-scan result that was completed before the outer LR2 bridge
    /// was invoked.
    /// </summary>
    public SongTableFileCheckResult FileCheckResult { get; } = fileCheckResult;

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

    /// <summary>
    /// Creates a copy that carries the completed file-scan result alongside
    /// the prepared LR2 request.
    /// </summary>
    internal Lr2FolderFileDiffPreparationResult WithFileCheckResult(
        SongTableFileCheckResult result)
    {
        return new Lr2FolderFileDiffPreparationResult(
            Request,
            AppManagedOutputDirectories,
            AppManagedOutputFilePaths,
            AppManagedPruneExcludedPaths,
            AppManagedPhysicalSurface,
            AppManagedCandidateCount,
            RootsMs,
            BuiltinSourceMs,
            AppManagedScopeMs,
            FilterMs,
            ParentSurfaceMs,
            ExtraTextRootsMs,
            TextMetadataMs,
            TotalElapsedMs,
            result);
    }

    /// <summary>
    /// Creates a scan result when LR2 preparation was not requested or could
    /// not produce a request.
    /// </summary>
    internal static Lr2FolderFileDiffPreparationResult FromFileCheckResult(
        SongTableFileCheckResult result)
    {
        return new Lr2FolderFileDiffPreparationResult(
            request: null,
            appManagedOutputDirectories: [],
            appManagedOutputFilePaths: [],
            appManagedPruneExcludedPaths: [],
            appManagedPhysicalSurface: CustomFolderOutputPhysicalSurface.Empty,
            appManagedCandidateCount: 0,
            rootsMs: 0L,
            builtinSourceMs: 0L,
            appManagedScopeMs: 0L,
            filterMs: 0L,
            parentSurfaceMs: 0L,
            extraTextRootsMs: 0L,
            textMetadataMs: 0L,
            totalElapsedMs: 0L,
            fileCheckResult: result);
    }
}
