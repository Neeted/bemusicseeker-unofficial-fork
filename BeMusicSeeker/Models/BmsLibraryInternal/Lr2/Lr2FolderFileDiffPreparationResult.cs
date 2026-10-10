using System.Collections.Generic;

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
    SongTableFileCheckResult fileCheckResult = null,
    LibraryFileInitializationResult synchronizationInput = null)
{
    /// <summary>呼出元へ直接渡す今回の確定入力です。</summary>
    internal LibraryFileInitializationResult SynchronizationInput { get; } = synchronizationInput ?? new();

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
    /// 確定したファイル走査結果と直接引渡しの入力を、今回のLR2差分準備と共に返します。
    /// </summary>
    internal Lr2FolderFileDiffPreparationResult WithFileCheckResult(
        SongTableFileCheckResult result, LibraryFileInitializationResult synchronizationInput = null)
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
            result, synchronizationInput ?? SynchronizationInput);
    }

    /// <summary>
    /// LR2差分準備が不要でも、今回のファイル確定と後段入力を失わず返します。
    /// </summary>
    internal static Lr2FolderFileDiffPreparationResult FromFileCheckResult(
        SongTableFileCheckResult result, LibraryFileInitializationResult synchronizationInput = null)
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
            fileCheckResult: result,
            synchronizationInput: synchronizationInput);
    }
}
