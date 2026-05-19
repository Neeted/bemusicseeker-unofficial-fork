using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum RenameInvalidExtensionAction
{
    Renamed,
    DeletedAsDuplicate,
    Skipped
}

internal sealed class RenameInvalidExtensionOutcome
{
    public RenameInvalidExtensionAction Action { get; set; }

    public string FinalPath { get; set; }

    public Exception FailureException { get; set; }

    public bool FailedDuringDelete { get; set; }
}

internal sealed class FileCollisionResolutionResult
{
    public string FinalPath { get; set; }

    public string SourceHash { get; set; }

    public string DuplicatePath { get; set; }

    public bool DuplicateMatched { get; set; }

    public bool EncounteredFileCollision { get; set; }

    public bool AnyHashUnavailable { get; set; }

    public bool AnyHashDifferent { get; set; }
}

/// <summary>
/// Builds and executes file-system mutations against snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryLibraryFileOperationsService
{
    public DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(
        string srcDir,
        string dstDir,
        DirectoryResourceLookupCache directoryLookupCache,
        IFileMutationService fileMutationService,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        fileMutationService.MoveDirectory(srcDir, dstDir, overwrite: false, recursiveDirectoryTreeFileMutationOptions);
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string item in (directoryLookupCache?.Keys ?? []).Where(f => (f + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            string newKey = item.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            mutationResult = mutationResult.Combine(directoryLookupCache.ReplaceDirWithResult(item, newKey));
        }
        return mutationResult;
    }

    public LibraryRemovalResult DeleteLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        IEnumerable<LibraryChartRef> libraryCharts,
        IEnumerable<ChartPackage> pendingPackages,
        DirectoryResourceLookupCache directoryLookupCache,
        bool sendToRecycleBin,
        Func<string, bool> confirmDeleteWholeFolder,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        var result = new LibraryRemovalResult();
        List<LibraryChartRef> currentLibraryCharts = [.. (libraryCharts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        List<LibraryChartRef> inputCharts = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        CanonicalChartResolveResult resolveResult = ResolveCanonicalCharts(inputCharts, currentLibraryCharts);
        result.InputChartCount = resolveResult.InputCount;
        result.CanonicalChartCount = resolveResult.CanonicalCharts.Count;
        result.UnresolvedChartCount = resolveResult.UnresolvedCharts.Count;
        result.PathOnlyInputCount = resolveResult.PathOnlyInputCount;
        foreach (LibraryChartRef unresolvedChart in resolveResult.UnresolvedCharts)
        {
            result.Failures.Add(new LibraryDeleteFailure
            {
                Path = unresolvedChart.Path,
                Exception = new InvalidOperationException("Library chart could not be resolved from the current catalog."),
                IsDirectory = false,
                Reason = "resolve_failed"
            });
        }
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        foreach (IGrouping<string, LibraryChartRef> folderGroup in from groupedFiles in resolveResult.CanonicalCharts.GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
                                                                   orderby groupedFiles.Key.Length descending
                                                                   select groupedFiles)
        {
            var removedChartPaths = new HashSet<string>(result.RemovedCharts.Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
            bool shouldDeleteWholeFolder = currentLibraryCharts.Where(chart => chart.Path.StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !removedChartPaths.Contains(chart.Path)).Count() == folderGroup.Count()
                && (confirmDeleteWholeFolder?.Invoke(folderGroup.Key) ?? false);
            if (shouldDeleteWholeFolder)
            {
                if (!Directory.Exists(folderGroup.Key))
                {
                    continue;
                }
                try
                {
                    fileMutationService.DeleteDirectoryShell(folderGroup.Key, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    result.FolderDeleteCount++;
                    AddRemovedCharts(result, folderGroup);
                }
                catch (Exception ex)
                {
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = folderGroup.Key,
                        Exception = ex,
                        IsDirectory = true
                    });
                }
                if (!result.Failures.Any(failure => failure.IsDirectory && string.Equals(failure.Path, folderGroup.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    result.ResourceIndexMutation = result.ResourceIndexMutation.Combine(CleanupDeletedFolderIndexes(folderGroup.Key, directoryLookupCache));
                    ClearInstallDestinationsUnderDeletedFolder(folderGroup.Key, pendingPackages, currentLibraryCharts);
                }
                continue;
            }
            foreach (LibraryChartRef selectedChart in folderGroup)
            {
                try
                {
                    if (File.Exists(selectedChart.Path))
                    {
                        fileMutationService.DeleteFileShell(selectedChart.Path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                        result.FileDeleteCount++;
                        AddRemovedChart(result, selectedChart);
                    }
                }
                catch (Exception ex2)
                {
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = selectedChart.Path,
                        Exception = ex2,
                        IsDirectory = false
                    });
                }
            }
        }
        return result;
    }

    public List<string> GetWholeFolderDeleteCandidatePaths(
        IEnumerable<LibraryChartRef> charts,
        IEnumerable<LibraryChartRef> libraryCharts)
    {
        List<LibraryChartRef> currentLibraryCharts = [.. (libraryCharts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        List<LibraryChartRef> inputCharts = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        CanonicalChartResolveResult resolveResult = ResolveCanonicalCharts(inputCharts, currentLibraryCharts);
        return GetWholeFolderDeleteCandidatePaths(resolveResult.CanonicalCharts, currentLibraryCharts);
    }

    private static List<string> GetWholeFolderDeleteCandidatePaths(
        IEnumerable<LibraryChartRef> canonicalCharts,
        List<LibraryChartRef> currentLibraryCharts)
    {
        List<string> result = [];
        var selectedChartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, LibraryChartRef> folderGroup in from groupedFiles in (canonicalCharts ?? []).GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
                                                                   orderby groupedFiles.Key.Length descending
                                                                   select groupedFiles)
        {
            bool shouldConfirmWholeFolder = currentLibraryCharts.Where(chart => chart.Path.StartsWith(folderGroup.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !selectedChartPaths.Contains(chart.Path)).Count() == folderGroup.Count();
            if (shouldConfirmWholeFolder)
            {
                result.Add(folderGroup.Key);
            }
            foreach (LibraryChartRef chart in folderGroup)
            {
                selectedChartPaths.Add(chart.Path);
            }
        }
        return result;
    }

    private static CanonicalChartResolveResult ResolveCanonicalCharts(
        IEnumerable<LibraryChartRef> inputCharts,
        IEnumerable<LibraryChartRef> currentLibraryCharts)
    {
        var result = new CanonicalChartResolveResult();
        List<LibraryChartRef> currentCharts = [.. (currentLibraryCharts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        var bmsByReference = currentCharts
            .Where(chart => chart.CompatibilityBmsFile != null)
            .GroupBy(chart => chart.CompatibilityBmsFile)
            .ToDictionary(group => group.Key, group => group.First());
        var bmsonByReference = currentCharts
            .Where(chart => chart.BmsonSong != null)
            .GroupBy(chart => chart.BmsonSong)
            .ToDictionary(group => group.Key, group => group.First());
        var byPath = currentCharts
            .GroupBy(chart => CreatePathKey(chart.Path), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryChartRef inputChart in inputCharts ?? [])
        {
            result.InputCount++;
            if (inputChart.CompatibilityBmsFile == null && inputChart.BmsonSong == null)
            {
                result.PathOnlyInputCount++;
            }
            LibraryChartRef canonicalChart = ResolveCanonicalChart(inputChart, bmsByReference, bmsonByReference, byPath);
            if (canonicalChart == null)
            {
                result.UnresolvedCharts.Add(inputChart);
                continue;
            }
            string canonicalKey = canonicalChart.Kind + "|" + CreatePathKey(canonicalChart.Path);
            if (addedKeys.Add(canonicalKey))
            {
                result.CanonicalCharts.Add(canonicalChart);
            }
        }
        return result;
    }

    private static LibraryChartRef ResolveCanonicalChart(
        LibraryChartRef inputChart,
        Dictionary<BMSFile, LibraryChartRef> bmsByReference,
        Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRef> bmsonByReference,
        Dictionary<string, LibraryChartRef> byPath)
    {
        if (inputChart == null)
        {
            return null;
        }
        if (inputChart.CompatibilityBmsFile != null && bmsByReference.TryGetValue(inputChart.CompatibilityBmsFile, out LibraryChartRef bmsChart))
        {
            return bmsChart;
        }
        if (inputChart.BmsonSong != null && bmsonByReference.TryGetValue(inputChart.BmsonSong, out LibraryChartRef bmsonChart))
        {
            return bmsonChart;
        }
        string pathKey = CreatePathKey(inputChart.Path);
        if (!string.IsNullOrWhiteSpace(pathKey) && byPath.TryGetValue(pathKey, out LibraryChartRef pathChart) && pathChart.Kind == inputChart.Kind)
        {
            return pathChart;
        }
        return null;
    }

    private static string CreatePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static DirectoryResourceLookupCache.ReverseLookupMutationResult CleanupDeletedFolderIndexes(string folderPath, DirectoryResourceLookupCache directoryLookupCache)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
        try
        {
            DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            foreach (string indexedDirectoryPath in (directoryLookupCache?.Keys ?? []).Where(directoryPath => (directoryPath + Path.DirectorySeparatorChar).StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                mutationResult = mutationResult.Combine(directoryLookupCache.RemoveDirWithResult(indexedDirectoryPath));
            }
            return mutationResult;
        }
        catch
        {
            return DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        }
    }

    private static void ClearInstallDestinationsUnderDeletedFolder(string folderPath, IEnumerable<ChartPackage> pendingPackages, IEnumerable<LibraryChartRef> currentLibraryCharts)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }
        try
        {
            IEnumerable<BMSFile> libraryFiles = (currentLibraryCharts ?? [])
                .Select(chart => chart.CompatibilityBmsFile)
                .Where(bmsInfo => bmsInfo != null);
            foreach (BMSFile installLinkedBmsFile in EnumerateInstallLinkedAdaptersUnderFolder(pendingPackages, libraryFiles, folderPath))
            {
                installLinkedBmsFile.instl_dst = null;
            }
        }
        catch
        {
        }
    }

    private static void AddRemovedCharts(LibraryRemovalResult result, IEnumerable<LibraryChartRef> charts)
    {
        foreach (LibraryChartRef chart in charts ?? [])
        {
            AddRemovedChart(result, chart);
        }
    }

    private static void AddRemovedChart(LibraryRemovalResult result, LibraryChartRef chart)
    {
        if (chart == null)
        {
            return;
        }
        result.RemovedCharts.Add(chart);
    }

    public LibraryMutationDelta BuildFolderMoveDelta(
        string srcDir,
        string dstDir,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<ChartPackage> installedPackages,
        bool unregister,
        bool raiseBmsFilesChanged = true)
    {
        var delta = new LibraryMutationDelta();
        List<BMSFile> targetFiles = [.. (libraryFiles ?? []).Where(file => file != null && !string.IsNullOrWhiteSpace(file.path) && file.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
        List<LR2SongDBExtended.bmson_song> targetBmsonSongs = [.. (bmsonSongs ?? []).Where(song => song != null && !string.IsNullOrWhiteSpace(song.path) && song.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
        if (unregister)
        {
            delta.FilesToUnregister.AddRange(targetFiles);
            delta.BmsonSongsToUnregister.AddRange(targetBmsonSongs);
            delta.InvalidateInstalledDirectoryIndex = targetFiles.Count > 0 || targetBmsonSongs.Count > 0;
            delta.InvalidateParentFolderCache = targetFiles.Count > 0 || targetBmsonSongs.Count > 0;
            delta.ClearDuplicatedCache = targetFiles.Count > 0 || targetBmsonSongs.Count > 0;
            return delta;
        }
        foreach (BMSFile installLinkedFile in EnumerateInstallLinkedAdaptersUnderFolder(pendingPackages, libraryFiles, srcDir))
        {
            if (!string.IsNullOrWhiteSpace(installLinkedFile?.instl_dst))
            {
                delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    File = installLinkedFile,
                    NewInstallDestination = installLinkedFile.instl_dst.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (ChartPackage installedPackage in installedPackages ?? [])
        {
            if (!string.IsNullOrWhiteSpace(installedPackage?.path)
                && (installedPackage.path + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                delta.UpdatedInstalledPackagePaths.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = installedPackage.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (IGrouping<string, BMSFile> group in targetFiles.GroupBy(target => Path.GetDirectoryName(target.path)))
        {
            string newFolderPath = group.Key.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            delta.FolderPathChanges.Add(new LibraryFolderPathChange
            {
                NewFolderPath = newFolderPath,
                OldFolderPath = group.Key
            });
            foreach (BMSFile file in group)
            {
                delta.FilePathChanges.Add(new LibraryFilePathChange
                {
                    File = file,
                    NewPath = file.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (LR2SongDBExtended.bmson_song bmsonSong in targetBmsonSongs)
        {
            delta.BmsonSongPathChanges.Add(new LibraryBmsonSongPathChange
            {
                Song = bmsonSong,
                OldPath = bmsonSong.path,
                NewPath = bmsonSong.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
            });
        }
        delta.RaiseBmsFilesChanged = raiseBmsFilesChanged && (delta.FilePathChanges.Count > 0 || delta.BmsonSongPathChanges.Count > 0);
        delta.RaiseInstalledPackagesChanged = delta.UpdatedInstalledPackagePaths.Count > 0;
        delta.InvalidateInstalledDirectoryIndex = delta.FilePathChanges.Count > 0 || delta.BmsonSongPathChanges.Count > 0 || delta.UpdatedInstallDestinations.Count > 0 || delta.UpdatedInstalledPackagePaths.Count > 0;
        delta.InvalidateParentFolderCache = delta.FilePathChanges.Count > 0 || delta.BmsonSongPathChanges.Count > 0;
        delta.ClearDuplicatedCache = delta.FilePathChanges.Count > 0 || delta.BmsonSongPathChanges.Count > 0 || delta.UpdatedInstallDestinations.Count > 0 || delta.UpdatedInstalledPackagePaths.Count > 0;
        return delta;
    }

    public List<FolderAutoRenamePlan> BuildAutoRenamePlans(
        IEnumerable<BMSFile> selectedFiles,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<string> rootFolders,
        bool renameRootFolder,
        Func<IEnumerable<BMSFile>, string, string, string> createFolderPath)
    {
        List<string> sourceFolders = [.. (from d in (selectedFiles ?? []).Where(f => f != null).Select(f => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                      orderby d.Length
                                      select d)];
        List<string> effectiveRootFolders = [.. (rootFolders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder))];
        List<string> targetFolders = [];
        foreach (string folder in sourceFolders.Where(folder => renameRootFolder || !effectiveRootFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
        {
            if (!targetFolders.Any(existingFolder => folder.StartsWith(existingFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                targetFolders.Add(folder);
            }
        }
        List<FolderAutoRenamePlan> plans = [];
        foreach (string folder in targetFolders)
        {
            var plan = new FolderAutoRenamePlan
            {
                SourceDirectory = folder
            };
            try
            {
                if (!Directory.Exists(folder) || Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase))
                {
                    plans.Add(plan);
                    continue;
                }
                List<BMSFile> directChildren = [.. (from f in libraryFiles ?? []
                                                    where f != null
                                                    && f.path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                                                    && DirectoryExt.GetDirectoryNameSimple(f.path).Equals(folder, StringComparison.OrdinalIgnoreCase)
                                                select f)];
                string longestFileName = (from f in FastDirectoryEnumerator.GetFileNames(folder)
                                          orderby f.Length descending
                                          select f).FirstOrDefault() ?? string.Empty;
                string requestedPath = createFolderPath?.Invoke(directChildren, DirectoryExt.GetDirectoryNameSimple(folder), longestFileName);
                if (!string.IsNullOrWhiteSpace(requestedPath) && !folder.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    int suffix = 1;
                    string candidate = requestedPath;
                    while (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        suffix++;
                        candidate = requestedPath + " (" + suffix + ")";
                    }
                    plan.DestinationDirectory = candidate;
                }
            }
            catch (Exception ex)
            {
                plan.FailureException = ex;
            }
            plans.Add(plan);
        }
        return plans;
    }

    public List<FolderAutoRenamePlan> BuildRootFolderMovePlans(IEnumerable<LibraryChartRef> selectedCharts, string destinationRootDirectory)
    {
        List<string> sourceFolders = [.. (from d in (selectedCharts ?? []).Where(chart => chart != null).Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                      orderby d.Length
                                      select d)];
        List<string> targetFolders = [];
        foreach (string folder in sourceFolders)
        {
            if (!targetFolders.Any(existingFolder => folder.StartsWith(existingFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                targetFolders.Add(folder);
            }
        }
        return [.. targetFolders
            .Where(folder => !Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
            .Select(folder => new FolderAutoRenamePlan
            {
                SourceDirectory = folder,
                DestinationDirectory = Path.Combine(destinationRootDirectory, Path.GetFileName(folder))
            })];
    }

    public LibraryMergeResult PrepareMergeDirectory(
        string srcDir,
        string dstDir,
        IEnumerable<BMSFile> libraryFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> libraryBmsonSongs,
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<ChartPackage> installedPackages,
        Func<IEnumerable<BMSFile>, HashSet<string>> createHashSnapshotExcluding)
    {
        var result = new LibraryMergeResult();
        if (!Directory.Exists(srcDir) || !Directory.Exists(dstDir) || srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        result.SourceFiles.AddRange((libraryFiles ?? [])
            .Where(file => file != null && file.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        result.SourceFiles.AddRange((libraryBmsonSongs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path) && song.path.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(PendingChartEntry.CreateFromBmsonSong)
            .Where(file => file != null));
        result.Repackage = ChartPackage.FromChartEntries(result.SourceFiles.Select(PackageChartEntry.FromCompatibilityAdapter));
        result.Repackage.path = srcDir;
        result.Repackage.delete_parent = false;
        result.ExistingHashes = createHashSnapshotExcluding?.Invoke(result.SourceFiles) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile installLinkedFile in EnumerateInstallLinkedAdaptersUnderFolder(pendingPackages, libraryFiles, srcDir))
        {
            if (!string.IsNullOrWhiteSpace(installLinkedFile?.instl_dst))
            {
                result.ReferenceMutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    File = installLinkedFile,
                    NewInstallDestination = installLinkedFile.instl_dst.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (ChartPackage installedPackage in installedPackages ?? [])
        {
            if (!string.IsNullOrWhiteSpace(installedPackage?.path)
                && (installedPackage.path + Path.DirectorySeparatorChar).StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = installedPackage.path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        result.ReferenceMutationDelta.RaiseInstalledPackagesChanged = result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count > 0;
        result.ReferenceMutationDelta.InvalidateInstalledDirectoryIndex = result.ReferenceMutationDelta.UpdatedInstallDestinations.Count > 0 || result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count > 0;
        result.ReferenceMutationDelta.ClearDuplicatedCache = result.ReferenceMutationDelta.UpdatedInstallDestinations.Count > 0 || result.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count > 0;
        result.Success = result.SourceFiles.Count > 0;
        return result;
    }

    private static IEnumerable<BMSFile> EnumerateInstallLinkedAdaptersUnderFolder(
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<BMSFile> libraryFiles,
        string folderPath)
    {
        IEnumerable<BMSFile> pendingFiles = (pendingPackages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(entry => IsInstallDestinationUnderFolder(entry?.Chart?.InstallDestination, folderPath))
            .Select(entry => entry.GetOrCreateCompatibilityAdapter())
            .Where(file => file != null);
        IEnumerable<BMSFile> existingLibraryFiles = (libraryFiles ?? [])
            .Where(file => file != null && IsInstallDestinationUnderFolder(file.instl_dst, folderPath));
        return pendingFiles.Concat(existingLibraryFiles);
    }

    private static bool IsInstallDestinationUnderFolder(string installDestination, string folderPath)
    {
        return !string.IsNullOrWhiteSpace(installDestination)
            && !string.IsNullOrWhiteSpace(folderPath)
            && (installDestination + Path.DirectorySeparatorChar).StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public LibraryFixInstallationResult FixInstallationDirectory(
        IEnumerable<BMSFile> chartFiles,
        HashSet<string> existingHashes,
        Func<ChartPackage, string, bool> movePackageFiles,
        Func<BMSFile, bool> confirmDuplicateRemoval)
    {
        var result = new LibraryFixInstallationResult();
        var stopwatch = Stopwatch.StartNew();
        List<BMSFile> files = [.. (chartFiles ?? []).Where(file => file != null && !string.IsNullOrWhiteSpace(file.instl_dst))];
        result.RequestedCount = files.Count;
        foreach (BMSFile file in files)
        {
            var bmsonEntry = file as PendingChartEntry;
            bool isBmson = bmsonEntry?.IsBmsonChart == true && bmsonEntry.BmsonSong != null;
            var installPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromCompatibilityAdapter(file)]);
            installPackage.path = file.path;
            installPackage.delete_parent = false;
            string oldPath = isBmson ? bmsonEntry.BmsonSong.path : file.path;
            if (!(movePackageFiles?.Invoke(installPackage, file.instl_dst) ?? false))
            {
                continue;
            }
            if (installPackage.ChartEntries.Count == 0)
            {
                result.DuplicateSkippedCount++;
                if (confirmDuplicateRemoval != null && confirmDuplicateRemoval(file))
                {
                    result.FilesToRemove.Add(file);
                }
                continue;
            }
            file.instl_dst = null;
            if (isBmson)
            {
                result.MutationDelta.BmsonSongPathChanges.Add(new LibraryBmsonSongPathChange
                {
                    Song = bmsonEntry.BmsonSong,
                    NewPath = file.path,
                    OldPath = oldPath
                });
            }
            else
            {
                result.MutationDelta.FilePathChanges.Add(new LibraryFilePathChange
                {
                    File = file,
                    NewPath = file.path,
                    OldPath = oldPath
                });
            }
            result.MutationDelta.RaiseBmsFilesChanged = true;
            result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
            result.MutationDelta.InvalidateParentFolderCache = true;
            result.MutationDelta.ClearDuplicatedCache = true;
            result.MaintenanceTargets.Add(file);
            result.MovedCount++;
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        result.MutationDelta.TotalMs = result.TotalMs;
        return result;
    }

    public LibraryMutationDelta RenameLibraryFileExtensions(
        IEnumerable<BMSFile> bmsFiles,
        string newExt,
        bool unregister,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        var delta = new LibraryMutationDelta();
        var stopwatch = Stopwatch.StartNew();
        foreach (BMSFile file in (bmsFiles ?? []).Where(file => file != null && File.Exists(file.path)))
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    delta.RenamedCount++;
                    if (unregister)
                    {
                        delta.FilesToUnregister.Add(file);
                    }
                    else
                    {
                        delta.FilePathChanges.Add(new LibraryFilePathChange
                        {
                            File = file,
                            NewPath = renameResult.FinalPath
                        });
                        delta.RaiseBmsFilesChanged = true;
                    }
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    delta.DuplicateDeletedCount++;
                    delta.FilesToUnregister.Add(file);
                    break;
                default:
                    delta.SkippedCount++;
                    if (renameResult.FailureException != null)
                    {
                        delta.Failures.Add(new LibraryDeleteFailure
                        {
                            Path = file.path,
                            Exception = renameResult.FailureException,
                            IsDirectory = false
                        });
                    }
                    break;
            }
        }
        delta.InvalidateInstalledDirectoryIndex = delta.FilePathChanges.Count > 0 || delta.FilesToUnregister.Count > 0;
        delta.InvalidateParentFolderCache = delta.FilePathChanges.Count > 0 || delta.FilesToUnregister.Count > 0;
        stopwatch.Stop();
        delta.TotalMs = stopwatch.ElapsedMilliseconds;
        return delta;
    }

    public RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(BMSFile sourceFile, string requestedPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions, Action<string> logInfo = null, Action<Exception, string> logWarn = null)
    {
        var outcome = new RenameInvalidExtensionOutcome
        {
            Action = RenameInvalidExtensionAction.Skipped,
            FinalPath = requestedPath
        };
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.path) || string.IsNullOrWhiteSpace(requestedPath) || !File.Exists(sourceFile.path))
        {
            return outcome;
        }
        FileCollisionResolutionResult resolution = ResolveFileCollisionWithSuffix(
            sourceFile.path,
            requestedPath,
            TryGetSourceHashForInvalidExtensionRename(sourceFile),
            "invalid_ext_rename",
            logInfo);
        string finalPath = resolution.FinalPath;
        if (resolution.DuplicateMatched)
        {
            try
            {
                fileMutationService.DeleteFileDirect(sourceFile.path, targetOnlyFileMutationOptions);
                logInfo?.Invoke("invalid_ext_rename duplicate_deleted source=" + sourceFile.path + " existing=" + resolution.DuplicatePath + " hash=" + (resolution.SourceHash ?? "(null)"));
                outcome.Action = RenameInvalidExtensionAction.DeletedAsDuplicate;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.FailureException = ex;
                outcome.FailedDuringDelete = true;
                logWarn?.Invoke(ex, "invalid_ext_rename delete_failed source=" + sourceFile.path + " existing=" + resolution.DuplicatePath);
                return outcome;
            }
        }
        if (!string.Equals(finalPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            logInfo?.Invoke("invalid_ext_rename renamed_with_suffix source=" + sourceFile.path + " requested=" + requestedPath + " resolved=" + finalPath);
        }
        try
        {
            fileMutationService.MoveFile(sourceFile.path, finalPath, overwrite: false, targetOnlyFileMutationOptions);
            outcome.Action = RenameInvalidExtensionAction.Renamed;
            outcome.FinalPath = finalPath;
            return outcome;
        }
        catch (Exception ex2)
        {
            outcome.FailureException = ex2;
            outcome.FinalPath = finalPath;
            outcome.FailedDuringDelete = false;
            logWarn?.Invoke(ex2, "invalid_ext_rename move_failed source=" + sourceFile.path + " target=" + finalPath);
            return outcome;
        }
    }

    public FileCollisionResolutionResult ResolveFileCollisionWithSuffix(string sourcePath, string requestedPath, string sourceHashHint = null, string logCategory = null, Action<string> logInfo = null)
    {
        var result = new FileCollisionResolutionResult
        {
            FinalPath = requestedPath,
            SourceHash = sourceHashHint
        };
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return result;
        }

        string candidatePath = requestedPath;
        int suffix = 1;
        while (File.Exists(candidatePath) || Directory.Exists(candidatePath))
        {
            result.FinalPath = candidatePath;
            if (Directory.Exists(candidatePath))
            {
                if (!string.IsNullOrWhiteSpace(logCategory))
                {
                    logInfo?.Invoke(logCategory + " collision_detected source=" + sourcePath + " candidate=" + candidatePath + " existsType=directory");
                }
            }
            else
            {
                result.EncounteredFileCollision = true;
                if (!string.IsNullOrWhiteSpace(logCategory))
                {
                    logInfo?.Invoke(logCategory + " collision_detected source=" + sourcePath + " candidate=" + candidatePath + " existsType=file");
                }
                if (string.IsNullOrWhiteSpace(result.SourceHash))
                {
                    result.SourceHash = TryComputeFileMd5ForPath(sourcePath, logCategory, logInfo);
                }
                string destinationHash = TryComputeFileMd5ForPath(candidatePath, logCategory, logInfo);
                if (!string.IsNullOrWhiteSpace(result.SourceHash) && !string.IsNullOrWhiteSpace(destinationHash))
                {
                    if (result.SourceHash.Equals(destinationHash, StringComparison.OrdinalIgnoreCase))
                    {
                        result.DuplicateMatched = true;
                        result.DuplicatePath = candidatePath;
                        return result;
                    }
                    result.AnyHashDifferent = true;
                }
                else
                {
                    result.AnyHashUnavailable = true;
                    if (!string.IsNullOrWhiteSpace(logCategory))
                    {
                        logInfo?.Invoke(logCategory + " hash_compare_unavailable source=" + sourcePath + " candidate=" + candidatePath + " reason=" + (string.IsNullOrWhiteSpace(result.SourceHash) ? "source_hash_unavailable" : "dest_hash_unavailable"));
                    }
                }
            }
            candidatePath = BuildPathWithSuffix(requestedPath, suffix);
            suffix++;
        }
        result.FinalPath = candidatePath;
        return result;
    }

    public string GetNonConflictingPathWithSuffix(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return requestedPath;
        }
        string directoryName = Path.GetDirectoryName(requestedPath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);
        int suffix = 1;
        string candidate = requestedPath;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = BuildPathWithSuffix(requestedPath, suffix);
            suffix++;
        }
        return candidate;
    }

    public string TryComputeFileMd5ForPath(string filePath, string logCategory = null, Action<string> logInfo = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }
        try
        {
            using var md5 = MD5.Create();
            byte[] hashBytes;
            using (var inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hashBytes = md5.ComputeHash(inputStream);
            }
            var stringBuilder = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                stringBuilder.Append(b.ToString("x2"));
            }
            return stringBuilder.ToString();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is IOException || ex is PathTooLongException || ex is SecurityException || ex is UnauthorizedAccessException)
        {
            if (!string.IsNullOrWhiteSpace(logCategory))
            {
                logInfo?.Invoke(logCategory + " hash_unavailable path=" + filePath + " error=" + ex.Message);
            }
            return null;
        }
    }

    public List<ChartPackage> GetPendingPackagesFullyCoveredBySelection(IEnumerable<ChartPackage> pendingPackages, HashSet<string> selectedPaths, HashSet<BMSFile> selectedFileRefs)
    {
        List<ChartPackage> result = [];
        foreach (ChartPackage package in (pendingPackages ?? []).Where(pkg => pkg != null))
        {
            List<PackageChartEntry> packageEntries = package.ChartEntries;
            if (packageEntries.Count > 0 && packageEntries.All(entry => IsMatchedRemovedEntry(entry, selectedPaths, selectedFileRefs)))
            {
                result.Add(package);
            }
        }
        return result;
    }

    public bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        if (file == null)
        {
            return false;
        }
        if (removedFiles != null && removedFiles.Contains(file))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(file.path) && removedPaths.Contains(file.path);
    }

    private static bool IsMatchedRemovedEntry(PackageChartEntry entry, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        if (entry?.Chart == null)
        {
            return false;
        }
        if (entry.CompatibilityAdapter != null && removedFiles != null && removedFiles.Contains(entry.CompatibilityAdapter))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart.Path) && removedPaths != null && removedPaths.Contains(entry.Chart.Path);
    }

    private string TryGetSourceHashForInvalidExtensionRename(BMSFile sourceFile)
    {
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.hash))
        {
            return TryComputeFileMd5ForPath(sourceFile?.path);
        }
        return sourceFile.hash;
    }

    private static string BuildPathWithSuffix(string requestedPath, int suffix)
    {
        if (suffix < 1 || string.IsNullOrWhiteSpace(requestedPath))
        {
            return requestedPath;
        }
        string directoryName = Path.GetDirectoryName(requestedPath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);
        string renamedFileName = fileNameWithoutExtension + "(" + suffix + ")" + extension;
        return string.IsNullOrWhiteSpace(directoryName) ? renamedFileName : Path.Combine(directoryName, renamedFileName);
    }

    private sealed class CanonicalChartResolveResult
    {
        public int InputCount { get; set; }

        public int PathOnlyInputCount { get; set; }

        public List<LibraryChartRef> CanonicalCharts { get; } = [];

        public List<LibraryChartRef> UnresolvedCharts { get; } = [];
    }
}
