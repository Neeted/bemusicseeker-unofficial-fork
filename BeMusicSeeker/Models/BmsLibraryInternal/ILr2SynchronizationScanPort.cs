using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Supplies the LR2 synchronization owner operations required by the file-scan route.
/// </summary>
internal interface ILr2SynchronizationScanPort
{
    void ThrowIfLr2SongDbSyncMutationBlocked(string operation);

    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope();

    CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface();

    Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc);

    List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options);

    List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> builtinSourceDirectories,
        BmsLibraryOptionsSnapshot options);

    Lr2FolderFileDbSyncResult SyncLr2FolderFileRows(
        BmsLibraryOptionsSnapshot options,
        Lr2SongDbSyncRequest request,
        string reason,
        string logName,
        bool allowPrune = true,
        IReadOnlyCollection<string> pruneExcludedDirectories = null,
        IReadOnlyCollection<string> pruneExcludedPaths = null,
        bool scopeReadLr2FolderRowsOnly = false,
        bool updateParentDirectoryRowsForPreservedItems = true);

    bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options);

    void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason);

    void CaptureLr2SongDbSyncScanSurface(
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult);

    void CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason);

    void MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult result);
}
