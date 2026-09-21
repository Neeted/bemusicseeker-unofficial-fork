using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongDbSyncInputFactory
{
    public static Lr2SongDbSyncInput Create(
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputRowSnapshot rowSnapshot,
        Lr2SongDbSyncDirectoryTargetSelection directoryTargetSelection,
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope,
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates,
        IReadOnlyList<string> textFileDirectories,
        Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection)
    {
        return new Lr2SongDbSyncInput(
            rootSnapshot.RootDirectories,
            rowSnapshot.ChartPaths,
            [.. directoryTargetSelection.DirectoryMetadataTargets],
            folderInfoCandidates.Paths,
            folderInfoCandidates.EntriesByPath,
            directoryEntries,
            rootSnapshot.Lr2FolderDiscoveryDirectories,
            settingsSnapshot.Lr2FolderPruneDirectories,
            rootSnapshot.Lr2RootPath,
            settingsSnapshot.Lr2NormalCustomFolderOutputBaseDir,
            settingsSnapshot.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
            settingsSnapshot.Lr2RootCustomFolderOutputBaseDir,
            settingsSnapshot.Lr2BuiltinFolderSourceDirectories,
            settingsSnapshot.Lr2BuiltinCustomFolderSettings,
            lr2FolderFileCandidates.Paths,
            lr2FolderFileCandidates.EntriesByPath,
            lr2FolderFileCandidates.DiscoveryComplete,
            rowSnapshot.SongRows,
            textFileDirectories,
            scanSurfaceSelection.Generation,
            rowSnapshot.OwnedCollectionVersion,
            rowSnapshot.BmsRowsVersion,
            rowSnapshot.BmsonRowsVersion);
    }
}
