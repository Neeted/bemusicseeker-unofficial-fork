using System;
using System.Collections.Generic;
using System.Diagnostics;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputBuilder(
    Func<
        Lr2SongDbSyncScanSurfaceSnapshot,
        Lr2SongDbSyncInputRootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot,
        Lr2SongDbSyncPreparedSurfaceSelection,
        Lr2SongDbSyncAppManagedOutputScope,
        Lr2SongDbSyncFolderCandidateSelection> createFolderCandidateSelection,
    Func<
        IReadOnlyCollection<string>,
        Lr2FolderFileCandidateSnapshot,
        Lr2SongDbSyncInputRootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot,
        Lr2SongDbSyncDirectoryTargetSelection> createDirectoryTargetSelection,
    Action<string> logInstallPerformance)
{
    public Lr2SongDbSyncInput Create(
        Lr2SongDbSyncInputRowSnapshot rowSnapshot,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection,
        IReadOnlyCollection<string> directoryMetadataTargets,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope,
        Stopwatch inputStopwatch,
        Stopwatch rowSnapshotStopwatch,
        Stopwatch rootsStopwatch,
        Stopwatch builtinSettingsStopwatch,
        Stopwatch scanSurfaceStopwatch,
        Stopwatch directoryTargetsStopwatch,
        Stopwatch lr2FolderCandidatesStopwatch)
    {
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = scanSurfaceSelection.Surface;
        Lr2SongDbSyncFolderCandidateSelection lr2FolderCandidateSelection =
            createFolderCandidateSelection(
                scanSurface,
                rootSnapshot,
                settingsSnapshot,
                preparedSurfaceSelection,
                appManagedOutputScope);
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates = lr2FolderCandidateSelection.Candidates;
        lr2FolderCandidatesStopwatch.Stop();
        Lr2SongDbSyncDirectoryTargetSelection directoryTargetSelection =
            createDirectoryTargetSelection(
                directoryMetadataTargets,
                lr2FolderFileCandidates,
                rootSnapshot,
                settingsSnapshot);
        IReadOnlyCollection<string> directoryEntryTargets = directoryTargetSelection.DirectoryEntryTargets;
        var folderInfoCandidatesStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncFolderInfoCandidateSelection folderInfoCandidateSelection =
            CreateFolderInfoCandidateSelection(
                scanSurface,
                rootSnapshot.Lr2FolderDiscoveryDirectories,
                directoryEntryTargets,
                preparedSurfaceSelection);
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates = folderInfoCandidateSelection.Candidates;
        Lr2TextMetadataCandidateSnapshot textMetadataCandidates = folderInfoCandidateSelection.TextMetadataCandidates;
        folderInfoCandidatesStopwatch.Stop();
        var directoryEntriesStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncDirectoryEntrySelection directoryEntrySelection =
            CreateDirectoryEntrySelection(
                scanSurface,
                rootSnapshot.Lr2FolderDiscoveryDirectories,
                directoryEntryTargets,
                preparedSurfaceSelection);
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = directoryEntrySelection.Entries;
        directoryEntriesStopwatch.Stop();
        var textFileDirsStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncTextFileDirectorySelection textFileDirectorySelection =
            CreateTextFileDirectorySelection(
                scanSurface,
                textMetadataCandidates,
                preparedSurfaceSelection);
        IReadOnlyList<string> textFileDirectories = textFileDirectorySelection.Directories;
        textFileDirsStopwatch.Stop();
        inputStopwatch.Stop();
        var inputSurfaceTimings = new Lr2SongDbSyncInputSurfaceTimings(
            rowSnapshotStopwatch,
            rootsStopwatch,
            builtinSettingsStopwatch,
            scanSurfaceStopwatch,
            directoryTargetsStopwatch,
            directoryEntriesStopwatch,
            lr2FolderCandidatesStopwatch,
            folderInfoCandidatesStopwatch,
            textFileDirsStopwatch,
            inputStopwatch);
        logInstallPerformance(Lr2SongDbSyncInputSurfaceLogFormatter.Format(
            scanSurfaceSelection,
            rootSnapshot,
            settingsSnapshot,
            directoryTargetSelection,
            directoryEntries,
            directoryEntrySelection,
            folderInfoCandidates,
            lr2FolderFileCandidates,
            appManagedOutputScope,
            lr2FolderCandidateSelection,
            preparedSurfaceSelection,
            textFileDirectories,
            textFileDirectorySelection,
            inputSurfaceTimings));

        return Lr2SongDbSyncInputFactory.Create(
            rootSnapshot,
            rowSnapshot,
            directoryTargetSelection,
            folderInfoCandidates,
            directoryEntries,
            settingsSnapshot,
            appManagedOutputScope,
            lr2FolderFileCandidates,
            textFileDirectories,
            scanSurfaceSelection);
    }

    private static Lr2SongDbSyncFolderInfoCandidateSelection CreateFolderInfoCandidateSelection(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface,
        IEnumerable<string> lr2FolderDiscoveryDirectories,
        IReadOnlyCollection<string> directoryEntryTargets,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection)
    {
        Lr2SongDbSyncPreparedDataSurface preparedSurface = preparedSurfaceSelection.ActiveSurface;
        bool hasPreparedSurface = preparedSurfaceSelection.HasActivePreparedSurface;
        Lr2TextMetadataCandidateSnapshot textMetadataCandidates = null;
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates;
        if (scanSurface != null)
        {
            folderInfoCandidates = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
                scanSurface.FolderInfoFilePaths,
                scanSurface.FolderInfoFileEntries.Values,
                directoryEntryTargets);
        }
        else
        {
            textMetadataCandidates = CreateLr2SongDbSyncTextMetadataCandidates(lr2FolderDiscoveryDirectories, directoryEntryTargets);
            folderInfoCandidates = textMetadataCandidates.FolderInfoCandidates;
        }
        if (hasPreparedSurface && preparedSurface.FolderInfoFilePaths.Count > 0)
        {
            IReadOnlyList<string> folderInfoPaths = MergePreparedFileSurface(
                folderInfoCandidates.Paths,
                folderInfoCandidates.EntriesByPath,
                preparedSurface.FolderInfoFilePaths,
                preparedSurface.FolderInfoFileEntries,
                preparedSurface.Lr2FolderScopeDirectories,
                out IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoEntries);
            folderInfoCandidates = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
                folderInfoPaths,
                folderInfoEntries.Values,
                directoryEntryTargets);
        }

        return new Lr2SongDbSyncFolderInfoCandidateSelection(
            folderInfoCandidates,
            textMetadataCandidates);
    }

    private static Lr2SongDbSyncDirectoryEntrySelection CreateDirectoryEntrySelection(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface,
        IEnumerable<string> lr2FolderDiscoveryDirectories,
        IReadOnlyCollection<string> directoryEntryTargets,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection)
    {
        Lr2SongDbSyncPreparedDataSurface preparedSurface = preparedSurfaceSelection.ActiveSurface;
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = scanSurface != null
            ? CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
                OverlayLr2DirectoryEntrySurface(
                    MergeMissingLr2DirectoryEntrySurface(scanSurface.DirectoryEntries, scanSurface.NormalFolderDirectoryEntries),
                    preparedSurface.DirectoryEntries),
                lr2FolderDiscoveryDirectories,
                directoryEntryTargets)
            : OverlayLr2DirectoryEntrySurface(
                CreateLr2SongDbSyncDirectoryEntriesFromGroupedScan(
                    lr2FolderDiscoveryDirectories,
                    directoryEntryTargets),
                preparedSurface.DirectoryEntries);

        return new Lr2SongDbSyncDirectoryEntrySelection(
            directoryEntries,
            Math.Max(0, directoryEntryTargets.Count - directoryEntries.Count));
    }

    private static Lr2SongDbSyncTextFileDirectorySelection CreateTextFileDirectorySelection(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface,
        Lr2TextMetadataCandidateSnapshot textMetadataCandidates,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection)
    {
        Lr2SongDbSyncPreparedDataSurface preparedSurface = preparedSurfaceSelection.ActiveSurface;
        bool hasPreparedSurface = preparedSurfaceSelection.HasActivePreparedSurface;
        if (scanSurface?.TextFileDirectories != null)
        {
            return new Lr2SongDbSyncTextFileDirectorySelection(
                hasPreparedSurface
                    ? MergePreparedDirectoryList(
                        scanSurface.TextFileDirectories,
                        preparedSurface.TextFileDirectories,
                        preparedSurface.Lr2FolderScopeDirectories)
                    : scanSurface.TextFileDirectories,
                hasPreparedSurface
                    ? "scan_surface_prepared_merge"
                    : "scan_surface_direct");
        }
        if (textMetadataCandidates?.TextFileDirectories != null)
        {
            return new Lr2SongDbSyncTextFileDirectorySelection(
                hasPreparedSurface
                    ? MergePreparedDirectoryList(
                        textMetadataCandidates.TextFileDirectories,
                        preparedSurface.TextFileDirectories,
                        preparedSurface.Lr2FolderScopeDirectories)
                    : textMetadataCandidates.TextFileDirectories,
                hasPreparedSurface
                    ? "enumeration_prepared_merge"
                    : "enumeration");
        }

        return new Lr2SongDbSyncTextFileDirectorySelection(
            hasPreparedSurface
                ? MergePreparedDirectoryList(
                    [],
                    preparedSurface.TextFileDirectories,
                    preparedSurface.Lr2FolderScopeDirectories)
                : [],
            hasPreparedSurface
                ? "prepared_only"
                : "empty");
    }
}
