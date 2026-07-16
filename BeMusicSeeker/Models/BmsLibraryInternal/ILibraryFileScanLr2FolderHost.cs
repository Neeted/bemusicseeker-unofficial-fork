using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Bridges the LR2 folder-file diff owner to the application-owned database and settings operations.
/// </summary>
internal interface ILibraryFileScanLr2FolderHost
{
    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope();

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
}
