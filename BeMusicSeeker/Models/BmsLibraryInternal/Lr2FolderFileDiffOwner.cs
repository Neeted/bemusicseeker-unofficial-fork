using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns LR2 folder-file diff preparation and its terminal database synchronization.
/// </summary>
internal sealed class Lr2FolderFileDiffOwner
{
    private readonly Action<string> logInstallPerformance;

    private readonly Action<string> logInstallPerformanceWarn;

    private readonly Func<Exception, string> getDisplayedExceptionMessage;

    private readonly Action<string> logEverythingScan;

    private readonly BMSLibrary.Lr2SynchronizationOwner lr2Synchronization;

    internal Lr2FolderFileDiffOwner(
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Func<Exception, string> getDisplayedExceptionMessage,
        Action<string> logEverythingScan,
        BMSLibrary.Lr2SynchronizationOwner lr2Synchronization)
    {
        this.logInstallPerformance = logInstallPerformance ?? throw new ArgumentNullException(nameof(logInstallPerformance));
        this.logInstallPerformanceWarn = logInstallPerformanceWarn ?? throw new ArgumentNullException(nameof(logInstallPerformanceWarn));
        this.getDisplayedExceptionMessage = getDisplayedExceptionMessage ?? throw new ArgumentNullException(nameof(getDisplayedExceptionMessage));
        this.logEverythingScan = logEverythingScan ?? throw new ArgumentNullException(nameof(logEverythingScan));
        this.lr2Synchronization = lr2Synchronization ?? throw new ArgumentNullException(nameof(lr2Synchronization));
    }

    internal bool CanPrepare(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult)
    {
        return options?.OperationModeLR2DB == true
            && fileCheckResult?.Lr2ScanSurfaceAvailable == true
            && fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories?.Count > 0;
    }

    internal void Apply(
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult,
        string reason,
        Task<Lr2FolderFileDiffPreparationResult> preparationTask = null)
    {
        if (!CanPrepare(options, fileCheckResult))
        {
            return;
        }

        Lr2FolderFileDiffPreparationResult preparation = WaitForPreparation(
            preparationTask,
            options,
            rootDirectories,
            fileCheckResult,
            reason);
        if (preparation?.Request == null)
        {
            return;
        }

        int scanCandidateCount = fileCheckResult.Lr2ScanLr2FolderFilePaths?.Count ?? 0;
        var stopwatchSurfaceApply = Stopwatch.StartNew();
        ApplyLr2SyncRequestSurfaceToFileCheckResult(fileCheckResult, preparation.Request);
        ApplyLr2FilteredFolderCandidateSurfaceToFileCheckResult(fileCheckResult, preparation);
        stopwatchSurfaceApply.Stop();
        long surfaceApplyMs = stopwatchSurfaceApply.ElapsedMilliseconds;
        bool allowPrune = ShouldPruneLr2FolderFileRowsDuringFileDiff(reason);
        logInstallPerformance("lr2folder_file_diff_prepare"
            + " reason=" + (reason ?? "unknown")
            + " rootsMs=" + preparation.RootsMs
            + " builtinSourceMs=" + preparation.BuiltinSourceMs
            + " appManagedScopeMs=" + preparation.AppManagedScopeMs
            + " filterMs=" + preparation.FilterMs
            + " parentSurfaceMs=" + preparation.ParentSurfaceMs
            + " extraTextRootsMs=" + preparation.ExtraTextRootsMs
            + " textMetadataMs=" + preparation.TextMetadataMs
            + " surfaceApplyMs=" + surfaceApplyMs
            + " totalMs=" + (preparation.TotalElapsedMs + surfaceApplyMs));
        logInstallPerformance("lr2folder_file_diff_filter"
            + " reason=" + (reason ?? "unknown")
            + " candidates=" + scanCandidateCount
            + " externalCandidates=" + preparation.Request.Lr2FolderFilePaths.Count
            + " appManagedFiltered=" + preparation.AppManagedCandidateCount
            + " appManagedScopeDirs=" + preparation.AppManagedOutputDirectories.Count
            + " appManagedExactFiles=" + preparation.AppManagedOutputFilePaths.Count
            + " appManagedPruneExcludedPaths=" + preparation.AppManagedPruneExcludedPaths.Count
            + " allowPruneRequested=" + allowPrune.ToString().ToLowerInvariant());
        lr2Synchronization.SyncLr2FolderFileRows(
            options,
            preparation.Request,
            reason,
            "lr2folder_file_diff_sync",
            allowPrune,
            pruneExcludedDirectories: preparation.AppManagedOutputDirectories,
            pruneExcludedPaths: preparation.AppManagedPruneExcludedPaths,
            scopeReadLr2FolderRowsOnly: true);
    }

    private Lr2FolderFileDiffPreparationResult WaitForPreparation(
        Task<Lr2FolderFileDiffPreparationResult> preparationTask,
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        if (preparationTask == null)
        {
            return Prepare(options, rootDirectories, fileCheckResult, reason);
        }

        var stopwatchWait = Stopwatch.StartNew();
        try
        {
            Lr2FolderFileDiffPreparationResult result = preparationTask.GetAwaiter().GetResult();
            stopwatchWait.Stop();
            logInstallPerformance("lr2folder_file_diff_prepare_wait"
                + " reason=" + (reason ?? "unknown")
                + " status=completed"
                + " waitMs=" + stopwatchWait.ElapsedMilliseconds
                + " preparedMs=" + (result?.TotalElapsedMs ?? 0L));
            return result;
        }
        catch (Exception ex)
        {
            stopwatchWait.Stop();
            logInstallPerformanceWarn("lr2folder_file_diff_prepare_wait failed"
                + " reason=" + (reason ?? "unknown")
                + " waitMs=" + stopwatchWait.ElapsedMilliseconds
                + " message=" + getDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            return Prepare(options, rootDirectories, fileCheckResult, reason);
        }
    }

    internal Lr2FolderFileDiffPreparationResult Prepare(
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        if (!CanPrepare(options, fileCheckResult))
        {
            return null;
        }

        BmsLibraryOptionsSnapshot currentSettings = lr2Synchronization.CurrentOptionsSnapshot;
        var stopwatchPrepare = Stopwatch.StartNew();
        var stopwatchStage = Stopwatch.StartNew();
        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(SafeFullPathOrOriginal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        long rootsMs = RestartElapsed(stopwatchStage);
        List<string> builtinSourceDirectories = lr2Synchronization.CreateLr2SongDbSyncBuiltinFolderSourceDirectories(currentSettings);
        long builtinSourceMs = RestartElapsed(stopwatchStage);
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope();
        long appManagedScopeMs = RestartElapsed(stopwatchStage);
        Lr2FolderFileCandidateSnapshot fileDiffCandidates;
        int appManagedCandidateCount;
        bool reusedFilteredCandidates = fileCheckResult.Lr2ScanLr2FolderCandidatesAlreadyFiltered;
        if (reusedFilteredCandidates)
        {
            Lr2FolderFileCandidateSnapshot filteredScanCandidates = new(
                fileCheckResult.Lr2ScanLr2FolderFilePaths,
                fileCheckResult.Lr2ScanLr2FolderFileEntries,
                fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete);
            appManagedCandidateCount = fileCheckResult.Lr2ScanLr2FolderAppManagedFilteredCount;
            if (!appManagedOutputScope.IsComplete)
            {
                fileDiffCandidates = CreateIncompleteLr2FolderCandidateSnapshot();
            }
            else if (!ArePathSetsEqual(fileCheckResult.Lr2ScanLr2FolderAppManagedScopeDirectories, appManagedOutputScope.Directories))
            {
                appManagedCandidateCount = 0;
                fileDiffCandidates = Lr2FolderFileDiscoveryService.CreateFileCandidates(
                    fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories,
                    currentSettings.LR2RootPath,
                    lr2Synchronization.CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
                    logEverythingScan,
                    appManagedOutputScope.Directories);
            }
            else
            {
                fileDiffCandidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                    filteredScanCandidates.Paths,
                    filteredScanCandidates.EntriesByPath,
                    appManagedOutputScope.FilePaths,
                    filteredScanCandidates.DiscoveryComplete,
                    out int currentScopeAppManagedCandidateCount,
                    appManagedOutputScope.Directories);
                appManagedCandidateCount += currentScopeAppManagedCandidateCount;
            }
        }
        else if (!appManagedOutputScope.IsComplete)
        {
            fileDiffCandidates = CreateIncompleteLr2FolderCandidateSnapshot();
            appManagedCandidateCount = 0;
        }
        else
        {
            fileDiffCandidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                fileCheckResult.Lr2ScanLr2FolderFilePaths,
                fileCheckResult.Lr2ScanLr2FolderFileEntries,
                appManagedOutputScope.FilePaths,
                fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete,
                out appManagedCandidateCount,
                appManagedOutputScope.Directories);
        }
        long filterMs = RestartElapsed(stopwatchStage);
        var request = new Lr2SongDbSyncRequest
        {
            RootDirectories = roots,
            Lr2FolderDiscoveryDirectories = fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories,
            Lr2FolderPruneDirectories = lr2Synchronization.CreateLr2SongDbSyncLr2FolderPruneDirectories(
                roots,
                builtinSourceDirectories,
                options: currentSettings),
            Lr2FolderFilePaths = fileDiffCandidates.Paths,
            Lr2FolderFileEntries = fileDiffCandidates.EntriesByPath,
            FolderInfoFilePaths = fileCheckResult.Lr2ScanFolderInfoFilePaths,
            FolderInfoFileEntries = fileCheckResult.Lr2ScanFolderInfoFileEntries,
            TextFileDirectories = fileCheckResult.Lr2ScanTextFileDirectories,
            DirectoryEntries = MergeMissingLr2DirectoryEntrySurface(
                fileCheckResult.Lr2ScanDirectoryEntries,
                fileCheckResult.Lr2ScanNormalFolderDirectoryEntries),
            Lr2FolderFileDiscoveryComplete = fileDiffCandidates.DiscoveryComplete,
            Lr2RootPath = currentSettings.LR2RootPath,
            Lr2NormalCustomFolderOutputBaseDir = currentSettings.LR2CustomFolderOutputBaseDir,
            Lr2AdditionalNormalCustomFolderOutputBaseDirs = currentSettings.LR2CustomFolderAdditionalOutputBaseDirs,
            Lr2RootCustomFolderOutputBaseDir = currentSettings.LR2CustomFolderOutputBaseDirRootType,
            Lr2BuiltinFolderSourceDirectories = builtinSourceDirectories
        };
        PrepareLr2FolderParentDirectoryEntrySurface(request);
        long parentSurfaceMs = RestartElapsed(stopwatchStage);
        IReadOnlyList<string> extraTextMetadataSourceDirectories = CreateLr2TextMetadataSourceDirectoriesOutsideRoots(
            request.Lr2FolderDiscoveryDirectories,
            roots);
        long extraTextRootsMs = RestartElapsed(stopwatchStage);
        Lr2TextMetadataCandidateSnapshot textMetadataSnapshot = CreateLr2PreparedTextMetadataCandidates(
            extraTextMetadataSourceDirectories,
            request.DirectoryEntries.Keys);
        long textMetadataMs = RestartElapsed(stopwatchStage);
        ApplyLr2TextMetadataCandidatesToRequest(request, textMetadataSnapshot, extraTextMetadataSourceDirectories);
        stopwatchPrepare.Stop();
        return new Lr2FolderFileDiffPreparationResult(
            request,
            appManagedOutputScope.Directories,
            appManagedOutputScope.FilePaths,
            appManagedOutputScope.PruneExcludedPaths,
            new CustomFolderOutputPhysicalSurface(
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: false),
            appManagedCandidateCount,
            rootsMs,
            builtinSourceMs,
            appManagedScopeMs,
            filterMs,
            parentSurfaceMs,
            extraTextRootsMs,
            textMetadataMs,
            stopwatchPrepare.ElapsedMilliseconds);
    }

    private void PrepareLr2FolderParentDirectoryEntrySurface(Lr2SongDbSyncRequest request)
    {
        if (request == null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var stopwatchStage = Stopwatch.StartNew();
        IReadOnlyCollection<string> parentDirectoryTargets = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateTargets(
            (request.Lr2FolderFilePaths ?? []).Concat(request.Lr2FolderFileEntries?.Keys ?? []),
            request.RootDirectories,
            request.Lr2NormalCustomFolderOutputBaseDir,
            request.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
            request.Lr2RootCustomFolderOutputBaseDir,
            request.Lr2BuiltinFolderSourceDirectories);
        long targetMs = RestartElapsed(stopwatchStage);
        if (parentDirectoryTargets.Count == 0)
        {
            logInstallPerformance("lr2folder_parent_directory_surface targets=0 targetMs=" + targetMs + " totalMs=" + stopwatch.ElapsedMilliseconds);
            return;
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> parentDirectoryEntries = CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
            request.DirectoryEntries,
            request.Lr2FolderDiscoveryDirectories,
            parentDirectoryTargets);
        long entryMs = RestartElapsed(stopwatchStage);
        request.DirectoryEntries = MergeMissingLr2DirectoryEntrySurface(
            request.DirectoryEntries,
            parentDirectoryEntries);
        long overlayMs = RestartElapsed(stopwatchStage);
        logInstallPerformance("lr2folder_parent_directory_surface"
            + " targets=" + parentDirectoryTargets.Count
            + " entries=" + (parentDirectoryEntries?.Count ?? 0)
            + " targetMs=" + targetMs
            + " entryMs=" + entryMs
            + " overlayMs=" + overlayMs
            + " totalMs=" + stopwatch.ElapsedMilliseconds);
    }

    private static Lr2TextMetadataCandidateSnapshot CreateLr2PreparedTextMetadataCandidates(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> targetDirectories)
    {
        if (!(rootDirectories ?? []).Any(path => !string.IsNullOrWhiteSpace(path))
            || !(targetDirectories ?? []).Any(path => !string.IsNullOrWhiteSpace(path)))
        {
            return new Lr2TextMetadataCandidateSnapshot(
                new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true),
                []);
        }

        return CreateLr2SongDbSyncTextMetadataCandidates(rootDirectories, targetDirectories);
    }

    private static void ApplyLr2SyncRequestSurfaceToFileCheckResult(
        SongTableFileCheckResult result,
        Lr2SongDbSyncRequest request)
    {
        if (result == null || request == null)
        {
            return;
        }

        if (request.DirectoryEntries != null)
        {
            result.Lr2ScanDirectoryEntries = request.DirectoryEntries;
        }
        if (request.FolderInfoFilePaths != null)
        {
            result.Lr2ScanFolderInfoFilePaths = ToReadOnlyList(request.FolderInfoFilePaths);
        }
        if (request.FolderInfoFileEntries != null)
        {
            result.Lr2ScanFolderInfoFileEntries = request.FolderInfoFileEntries;
        }
        if (request.TextFileDirectories != null)
        {
            result.Lr2ScanTextFileDirectories = ToReadOnlyList(request.TextFileDirectories);
        }
    }

    private static void ApplyLr2FilteredFolderCandidateSurfaceToFileCheckResult(
        SongTableFileCheckResult result,
        Lr2FolderFileDiffPreparationResult preparation)
    {
        if (result == null || preparation?.Request == null)
        {
            return;
        }

        result.Lr2ScanLr2FolderFilePaths = ToReadOnlyList(preparation.Request.Lr2FolderFilePaths);
        result.Lr2ScanLr2FolderFileEntries = preparation.Request.Lr2FolderFileEntries
            ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        result.Lr2ScanLr2FolderFileDiscoveryComplete = preparation.Request.Lr2FolderFileDiscoveryComplete;
        result.Lr2ScanLr2FolderCandidatesAlreadyFiltered = true;
        result.Lr2ScanLr2FolderAppManagedFilteredCount = preparation.AppManagedCandidateCount;
        result.Lr2ScanLr2FolderAppManagedScopeDirectoryCount = preparation.AppManagedOutputDirectories.Count;
        result.Lr2ScanLr2FolderAppManagedScopeDirectories = preparation.AppManagedOutputDirectories;
        result.Lr2ScanLr2FolderAppManagedExactFileCount = preparation.AppManagedOutputFilePaths.Count;
        result.Lr2ScanAppManagedCustomFolderOutputFilePaths = [.. preparation.AppManagedPhysicalSurface.FileEntries.Keys
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        result.Lr2ScanAppManagedCustomFolderOutputFileEntries = preparation.AppManagedPhysicalSurface.FileEntries;
        result.Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete = preparation.AppManagedPhysicalSurface.DiscoveryComplete;
    }

    private static void ApplyLr2TextMetadataCandidatesToRequest(
        Lr2SongDbSyncRequest request,
        Lr2TextMetadataCandidateSnapshot textMetadataSnapshot,
        IEnumerable<string> metadataScopeDirectories)
    {
        if (request == null || textMetadataSnapshot == null)
        {
            return;
        }

        IReadOnlyList<string> folderInfoPaths = MergePreparedFileSurface(
            request.FolderInfoFilePaths,
            request.FolderInfoFileEntries,
            textMetadataSnapshot.FolderInfoCandidates.Paths,
            textMetadataSnapshot.FolderInfoCandidates.EntriesByPath,
            metadataScopeDirectories,
            out IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoEntries);
        request.FolderInfoFilePaths = folderInfoPaths;
        request.FolderInfoFileEntries = folderInfoEntries;
        request.TextFileDirectories = MergePreparedDirectoryList(
            request.TextFileDirectories,
            textMetadataSnapshot.TextFileDirectories,
            metadataScopeDirectories);
    }

    private static IReadOnlyList<T> ToReadOnlyList<T>(IReadOnlyCollection<T> values)
    {
        if (values == null)
        {
            return [];
        }

        return values as IReadOnlyList<T> ?? [.. values];
    }

    private static Lr2FolderFileCandidateSnapshot CreateIncompleteLr2FolderCandidateSnapshot()
    {
        return new Lr2FolderFileCandidateSnapshot(
            [],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);
    }

    private static bool ArePathSetsEqual(IEnumerable<string> first, IEnumerable<string> second)
    {
        return new HashSet<string>(
            (first ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(SafeFullPathOrOriginal),
            StringComparer.OrdinalIgnoreCase)
            .SetEquals((second ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(SafeFullPathOrOriginal));
    }

    private static string SafeFullPathOrOriginal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        try
        {
            return LongPathFileSystem.NormalizePathForStorage(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path;
        }
    }

    private static bool ShouldPruneLr2FolderFileRowsDuringFileDiff(string reason)
    {
        return true;
    }

    private static long RestartElapsed(Stopwatch stopwatch)
    {
        long elapsedMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        return elapsedMs;
    }
}
