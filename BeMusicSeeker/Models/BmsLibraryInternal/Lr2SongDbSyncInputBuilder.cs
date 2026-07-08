using System;
using System.Collections.Generic;
using System.Diagnostics;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputBuilder(
    Action<string> logLr2FolderScan,
    Action<string> logInstallPerformance)
{
    public Lr2SongDbSyncInput Create(
        Lr2SongDbSyncInputRowSnapshot rowSnapshot,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope,
        Stopwatch inputStopwatch,
        Stopwatch rowSnapshotStopwatch,
        Stopwatch rootsStopwatch,
        Stopwatch builtinSettingsStopwatch,
        Stopwatch scanSurfaceStopwatch,
        Stopwatch lr2FolderCandidatesStopwatch)
    {
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = scanSurfaceSelection.Surface;
        lr2FolderCandidatesStopwatch.Stop();
        var directoryTargetsStopwatch = Stopwatch.StartNew();
        IReadOnlyCollection<string> directoryMetadataTargets =
            CreateDirectoryMetadataTargets(scanSurface, rootSnapshot, rowSnapshot);
        directoryTargetsStopwatch.Stop();
        lr2FolderCandidatesStopwatch.Start();
        Lr2SongDbSyncFolderCandidateSelection lr2FolderCandidateSelection =
            CreateFolderCandidateSelection(
                scanSurface,
                rootSnapshot,
                settingsSnapshot,
                preparedSurfaceSelection,
                appManagedOutputScope);
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates = lr2FolderCandidateSelection.Candidates;
        lr2FolderCandidatesStopwatch.Stop();
        Lr2SongDbSyncDirectoryTargetSelection directoryTargetSelection =
            CreateDirectoryTargetSelection(
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

    private static IReadOnlyCollection<string> CreateDirectoryMetadataTargets(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputRowSnapshot rowSnapshot)
    {
        return scanSurface?.NormalFolderDirectoryPaths?.Count > 0
            ? scanSurface.NormalFolderDirectoryPaths
            : Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(rootSnapshot.RootDirectories, rowSnapshot.ChartPaths);
    }

    private Lr2SongDbSyncFolderCandidateSelection CreateFolderCandidateSelection(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope)
    {
        Lr2SongDbSyncPreparedDataSurface preparedSurface = preparedSurfaceSelection.ActiveSurface;
        bool hasPreparedLr2FolderSurface = preparedSurfaceSelection.HasActiveLr2FolderSurface;
        string source;
        Lr2FolderFileCandidateSnapshot candidates;
        int enumeratedAppManagedCandidateCount = 0;
        int enumeratedAppManagedExactFileCount = appManagedOutputScope.FilePaths.Count;
        if (scanSurface != null)
        {
            if (!appManagedOutputScope.IsComplete)
            {
                source = hasPreparedLr2FolderSurface
                    ? "scan_surface_prepared_only_app_managed_incomplete"
                    : "scan_surface_app_managed_incomplete";
                candidates = CreateIncompleteLr2FolderCandidateSnapshot();
                if (hasPreparedLr2FolderSurface)
                {
                    candidates = MergeLr2FolderFileCandidateSurface(candidates, preparedSurface);
                }
            }
            else
            {
                source = hasPreparedLr2FolderSurface
                    ? "scan_surface_prepared_merge"
                    : "scan_surface_direct";
                candidates = new Lr2FolderFileCandidateSnapshot(
                    scanSurface.Lr2FolderFilePaths,
                    scanSurface.Lr2FolderFileEntries,
                    scanSurface.Lr2FolderFileDiscoveryComplete);
                if (hasPreparedLr2FolderSurface)
                {
                    candidates = MergeLr2FolderFileCandidateSurface(candidates, preparedSurface);
                }
                if (preparedSurface?.Lr2FolderFileDiscoveryComplete == false)
                {
                    candidates = new Lr2FolderFileCandidateSnapshot(
                        candidates.Paths,
                        candidates.EntriesByPath,
                        discoveryComplete: false);
                }
            }
        }
        else
        {
            if (!appManagedOutputScope.IsComplete)
            {
                source = hasPreparedLr2FolderSurface
                    ? "enumeration_prepared_only_app_managed_incomplete"
                    : "enumeration_app_managed_incomplete";
                candidates = CreateIncompleteLr2FolderCandidateSnapshot();
            }
            else
            {
                source = hasPreparedLr2FolderSurface
                    ? "enumeration_prepared_merge"
                    : "enumeration";
                candidates = Lr2FolderFileDiscoveryService.CreateFileCandidates(
                    Lr2FolderFileDiscoveryService.CreateDiscoveryDirectoriesForEnumeration(
                        rootSnapshot.Lr2FolderDiscoveryDirectories,
                        preparedSurface),
                    rootSnapshot.Lr2RootPath,
                    settingsSnapshot.Lr2BuiltinCustomFolderSettings,
                    logLr2FolderScan,
                    appManagedOutputScope.Directories);
                candidates =
                    Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                        candidates.Paths,
                        candidates.EntriesByPath,
                        appManagedOutputScope.FilePaths,
                        candidates.DiscoveryComplete,
                        out enumeratedAppManagedCandidateCount,
                        appManagedOutputScope.Directories);
                if (enumeratedAppManagedCandidateCount > 0)
                {
                    source += "_app_managed_filtered";
                }
            }
            if (hasPreparedLr2FolderSurface)
            {
                candidates = MergeLr2FolderFileCandidateSurface(candidates, preparedSurface);
            }
        }
        if (!appManagedOutputScope.IsComplete)
        {
            candidates = CreateIncompleteLr2FolderCandidateSnapshot();
        }
        else
        {
            candidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                candidates.Paths,
                candidates.EntriesByPath,
                appManagedOutputScope.FilePaths,
                candidates.DiscoveryComplete,
                out int postMergeAppManagedCandidateCount,
                appManagedOutputScope.Directories);
            enumeratedAppManagedCandidateCount += postMergeAppManagedCandidateCount;
            if (postMergeAppManagedCandidateCount > 0
                && !source.EndsWith("_app_managed_filtered", StringComparison.OrdinalIgnoreCase))
            {
                source += "_app_managed_filtered";
            }
        }

        return new Lr2SongDbSyncFolderCandidateSelection(
            candidates,
            source,
            enumeratedAppManagedCandidateCount,
            enumeratedAppManagedExactFileCount);
    }

    private static Lr2SongDbSyncDirectoryTargetSelection CreateDirectoryTargetSelection(
        IReadOnlyCollection<string> directoryMetadataTargets,
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot)
    {
        IReadOnlyCollection<string> lr2FolderParentDirectoryTargets = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateTargets(
            lr2FolderFileCandidates.Paths,
            rootSnapshot.RootDirectories,
            settingsSnapshot.Lr2NormalCustomFolderOutputBaseDir,
            settingsSnapshot.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
            settingsSnapshot.Lr2RootCustomFolderOutputBaseDir,
            settingsSnapshot.Lr2BuiltinFolderSourceDirectories);
        IReadOnlyCollection<string> directoryEntryTargets = MergeLr2DirectoryMetadataTargets(
            directoryMetadataTargets,
            lr2FolderParentDirectoryTargets);

        return new Lr2SongDbSyncDirectoryTargetSelection(
            directoryMetadataTargets,
            lr2FolderParentDirectoryTargets,
            directoryEntryTargets);
    }

    private static Lr2FolderFileCandidateSnapshot MergeLr2FolderFileCandidateSurface(
        Lr2FolderFileCandidateSnapshot baseCandidates,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        return Lr2FolderFileDiscoveryService.MergeCandidateSurface(baseCandidates, preparedSurface);
    }

    private static Lr2FolderFileCandidateSnapshot CreateIncompleteLr2FolderCandidateSnapshot()
    {
        return new Lr2FolderFileCandidateSnapshot(
            [],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);
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
