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
        LibraryChartRefIndexSnapshot libraryChartLookup,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        IEnumerable<ChartPackage> pendingPackages,
        DirectoryResourceLookupCache directoryLookupCache,
        bool sendToRecycleBin,
        Func<string, bool> confirmDeleteWholeFolder,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        var result = new LibraryRemovalResult();
        List<LibraryChartRef> inputCharts = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        libraryChartLookup ??= LibraryChartRefIndexSnapshot.Empty;
        CanonicalChartResolveResult resolveResult = libraryChartLookup.ResolveCanonicalCharts(inputCharts);
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
            bool shouldDeleteWholeFolder = libraryChartLookup.CountChartRefsUnderRealPath(folderGroup.Key, removedChartPaths) == folderGroup.Count()
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
                    CollectInstallDestinationClearsUnderDeletedFolder(result, folderGroup.Key, pendingPackages, installDestinationOverlayCharts);
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
        LibraryChartRefIndexSnapshot libraryChartLookup)
    {
        List<LibraryChartRef> inputCharts = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        libraryChartLookup ??= LibraryChartRefIndexSnapshot.Empty;
        CanonicalChartResolveResult resolveResult = libraryChartLookup.ResolveCanonicalCharts(inputCharts);
        return GetWholeFolderDeleteCandidatePathsForCanonicalCharts(resolveResult.CanonicalCharts, libraryChartLookup);
    }

    private static List<string> GetWholeFolderDeleteCandidatePathsForCanonicalCharts(
        IEnumerable<LibraryChartRef> canonicalCharts,
        LibraryChartRefIndexSnapshot libraryChartLookup)
    {
        List<string> result = [];
        var selectedChartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, LibraryChartRef> folderGroup in from groupedFiles in (canonicalCharts ?? []).GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
                                                                   orderby groupedFiles.Key.Length descending
                                                                   select groupedFiles)
        {
            bool shouldConfirmWholeFolder = libraryChartLookup.CountChartRefsUnderRealPath(folderGroup.Key, selectedChartPaths) == folderGroup.Count();
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

    private static void CollectInstallDestinationClearsUnderDeletedFolder(
        LibraryRemovalResult result,
        string folderPath,
        IEnumerable<ChartPackage> pendingPackages,
        InstallDestinationOverlayChartRefSnapshot currentLibraryCharts)
    {
        if (result == null || string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }
        try
        {
            int installDestinationChangeCountBefore = result.MutationDelta.UpdatedInstallDestinations.Count;
            foreach (LibraryInstallDestinationChange target in EnumerateInstallDestinationTargetsUnderFolder(pendingPackages, currentLibraryCharts, folderPath))
            {
                if (target.Entry != null)
                {
                    target.Entry.ClearInstallDestination();
                }
                else
                {
                    result.MutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                    {
                        Chart = target.Chart,
                        NewInstallDestination = null,
                        ClearInstallDestinationState = true
                    });
                }
            }
            if (result.MutationDelta.UpdatedInstallDestinations.Count > installDestinationChangeCountBefore)
            {
                result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
                result.MutationDelta.ClearDuplicatedCache = true;
                result.MutationDelta.RaiseLibraryChartsChanged = true;
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
        IEnumerable<LibraryChartRef> sourceCharts,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<ChartPackage> installedPackages,
        bool unregister,
        bool raiseLibraryChartsChanged = true)
    {
        var delta = new LibraryMutationDelta();
        List<LibraryChartRef> targetCharts = [.. (sourceCharts ?? [])
            .Where(chart => IsChartUnderFolder(chart, srcDir))];
        if (unregister)
        {
            delta.ChartsToUnregister.AddRange(targetCharts.Select(ToChartFile).Where(chart => chart != null));
            delta.InvalidateInstalledDirectoryIndex = targetCharts.Count > 0;
            delta.InvalidateParentFolderCache = targetCharts.Count > 0;
            delta.ClearDuplicatedCache = targetCharts.Count > 0;
            return delta;
        }
        delta.UpdatedInstallDestinations.AddRange(EnumerateInstallDestinationChangesUnderFolder(pendingPackages, installDestinationOverlayCharts, srcDir, dstDir));
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
        foreach (IGrouping<string, LibraryChartRef> group in targetCharts
            .Where(chart => chart.GetBmsStorageOwner() != null)
            .GroupBy(target => Path.GetDirectoryName(target.Path)))
        {
            string newFolderPath = group.Key.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true);
            delta.FolderPathChanges.Add(new LibraryFolderPathChange
            {
                NewFolderPath = newFolderPath,
                OldFolderPath = group.Key
            });
            foreach (LibraryChartRef chart in group)
            {
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ToChartFile(chart),
                    NewPath = chart.Path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
                });
            }
        }
        foreach (LibraryChartRef chart in targetCharts.Where(chart => chart.GetBmsonStorageOwner() != null))
        {
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ToChartFile(chart),
                OldPath = chart.Path,
                NewPath = chart.Path.ReplaceFromStart(srcDir, dstDir, isIgnoreCase: true)
            });
        }
        delta.RaiseLibraryChartsChanged = raiseLibraryChartsChanged && delta.ChartPathChanges.Count > 0;
        delta.RaiseInstalledPackagesChanged = delta.UpdatedInstalledPackagePaths.Count > 0;
        delta.InvalidateInstalledDirectoryIndex = delta.ChartPathChanges.Count > 0 || delta.UpdatedInstallDestinations.Count > 0 || delta.UpdatedInstalledPackagePaths.Count > 0;
        delta.InvalidateParentFolderCache = delta.ChartPathChanges.Count > 0;
        delta.ClearDuplicatedCache = delta.ChartPathChanges.Count > 0 || delta.UpdatedInstallDestinations.Count > 0 || delta.UpdatedInstalledPackagePaths.Count > 0;
        return delta;
    }

    public List<FolderAutoRenamePlan> BuildAutoRenamePlans(
        IEnumerable<ChartFile> selectedCharts,
        IEnumerable<string> rootFolders,
        bool renameRootFolder,
        Func<IReadOnlyCollection<string>, IReadOnlyList<ChartFile>> createDirectChildSnapshot,
        Func<IEnumerable<ChartFile>, string, string, string> createFolderPath)
    {
        List<string> sourceFolders = [.. (from d in (selectedCharts ?? []).Where(chart => chart != null).Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                      orderby d.Length
                                      select d)];
        List<string> effectiveRootFolders = [.. (rootFolders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder))];
        List<string> targetFolders = [];
        var targetFolderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in sourceFolders.Where(folder => renameRootFolder || !effectiveRootFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
        {
            if (!HasTargetAncestor(folder, targetFolderSet))
            {
                targetFolders.Add(folder);
                targetFolderSet.Add(folder);
            }
        }
        IReadOnlyList<ChartFile> directChildSnapshot = targetFolders.Count == 0
            ? []
            : createDirectChildSnapshot?.Invoke(targetFolders) ?? [];
        Dictionary<string, List<ChartFile>> directChildrenByDirectory = CreateDirectChildrenByDirectory(directChildSnapshot);
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
                List<ChartFile> directChildren = directChildrenByDirectory.TryGetValue(folder, out List<ChartFile> children)
                    ? children
                    : [];
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

    private static Dictionary<string, List<ChartFile>> CreateDirectChildrenByDirectory(IEnumerable<ChartFile> charts)
    {
        var directChildrenByDirectory = new Dictionary<string, List<ChartFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                continue;
            }

            string directory = DirectoryExt.GetDirectoryNameSimple(chart.Path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (!directChildrenByDirectory.TryGetValue(directory, out List<ChartFile> children))
            {
                children = [];
                directChildrenByDirectory[directory] = children;
            }
            children.Add(chart);
        }

        return directChildrenByDirectory;
    }

    private static bool HasTargetAncestor(string folder, ISet<string> targetFolders)
    {
        if (string.IsNullOrWhiteSpace(folder) || targetFolders?.Count > 0 != true)
        {
            return false;
        }

        string parent = GetParentDirectory(folder);
        while (!string.IsNullOrWhiteSpace(parent))
        {
            if (targetFolders.Contains(parent))
            {
                return true;
            }

            string nextParent = GetParentDirectory(parent);
            if (string.Equals(nextParent, parent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            parent = nextParent;
        }

        return false;
    }

    private static string GetParentDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(trimmedPath) ? null : Path.GetDirectoryName(trimmedPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
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
        IEnumerable<LibraryChartRef> sourceCharts,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<ChartPackage> installedPackages,
        Func<IEnumerable<ChartFile>, IPrimaryHashLookup> createHashSnapshotExcluding)
    {
        var result = new LibraryMergeResult();
        if (!Directory.Exists(srcDir) || !Directory.Exists(dstDir) || srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        result.SourceCharts.AddRange((sourceCharts ?? [])
            .Where(chart => IsChartUnderFolder(chart, srcDir))
            .Select(CreateStableLibraryChartRefSnapshot)
            .Where(chart => chart != null));
        List<PackageChartEntry> sourceEntries = [.. result.SourceCharts.Select(ToPackageChartEntry).Where(entry => entry != null)];
        result.Repackage = ChartPackage.FromChartEntries(sourceEntries);
        result.Repackage.path = srcDir;
        result.Repackage.delete_parent = false;
        result.ExistingHashes = createHashSnapshotExcluding?.Invoke(sourceEntries.Select(entry => entry.Chart).Where(chart => chart != null)) ?? EmptyPrimaryHashLookup.Instance;
        result.ReferenceMutationDelta.UpdatedInstallDestinations.AddRange(EnumerateInstallDestinationChangesUnderFolder(pendingPackages, installDestinationOverlayCharts, srcDir, dstDir));
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
        result.Success = result.SourceCharts.Count > 0;
        return result;
    }

    private static IEnumerable<LibraryInstallDestinationChange> EnumerateInstallDestinationChangesUnderFolder(
        IEnumerable<ChartPackage> pendingPackages,
        InstallDestinationOverlayChartRefSnapshot libraryCharts,
        string sourceFolderPath,
        string destinationFolderPath)
    {
        foreach (LibraryInstallDestinationChange target in EnumerateInstallDestinationTargetsUnderFolder(pendingPackages, libraryCharts, sourceFolderPath))
        {
            string currentInstallDestination = target.GetCurrentInstallDestination();
            yield return new LibraryInstallDestinationChange
            {
                Entry = target.Entry,
                Chart = target.Chart,
                NewInstallDestination = RewriteInstallDestinationUnderFolder(currentInstallDestination, sourceFolderPath, destinationFolderPath)
            };
        }
    }

    private static IEnumerable<LibraryInstallDestinationChange> EnumerateInstallDestinationTargetsUnderFolder(
        IEnumerable<ChartPackage> pendingPackages,
        InstallDestinationOverlayChartRefSnapshot libraryCharts,
        string folderPath)
    {
        foreach (PackageChartEntry entry in (pendingPackages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(entry => IsInstallDestinationUnderFolder(entry?.Chart?.InstallDestination, folderPath)))
        {
            yield return new LibraryInstallDestinationChange
            {
                Entry = entry,
                Chart = entry.Chart
            };
        }
        foreach (ChartFile chart in (libraryCharts ?? InstallDestinationOverlayChartRefSnapshot.Empty)
            .GetChartRefsUnderInstallDestination(folderPath)
            .Select(chart => chart?.GetChartSnapshot())
            .Where(chart => chart != null))
        {
            yield return new LibraryInstallDestinationChange
            {
                Chart = chart
            };
        }
    }

    private static ChartFile ToChartFile(LibraryChartRef chart)
    {
        return chart?.ToChartFile();
    }

    private static LibraryChartRef CreateStableLibraryChartRefSnapshot(LibraryChartRef chart)
    {
        ChartFile chartSnapshot = chart?.ToChartFile();
        return chartSnapshot == null ? null : LibraryChartRef.FromChartFile(chartSnapshot);
    }

    private static PackageChartEntry ToPackageChartEntry(LibraryChartRef chart)
    {
        ChartFile chartFile = ToChartFile(chart);
        if (chartFile == null)
        {
            return null;
        }
        return PackageChartEntry.FromChart(chartFile);
    }

    private static bool IsInstallDestinationUnderFolder(string installDestination, string folderPath)
    {
        string installDestinationKey = CreateDirectoryComparisonKey(installDestination);
        string folderKey = CreateDirectoryComparisonKey(folderPath);
        return !string.IsNullOrWhiteSpace(installDestinationKey)
            && !string.IsNullOrWhiteSpace(folderKey)
            && (string.Equals(installDestinationKey, folderKey, StringComparison.OrdinalIgnoreCase)
                || installDestinationKey.StartsWith(AppendDirectorySeparator(folderKey), StringComparison.OrdinalIgnoreCase));
    }

    private static string RewriteInstallDestinationUnderFolder(string installDestination, string sourceFolderPath, string destinationFolderPath)
    {
        string installDestinationKey = CreateDirectoryComparisonKey(installDestination);
        string sourceFolderKey = CreateDirectoryComparisonKey(sourceFolderPath);
        if (string.IsNullOrWhiteSpace(installDestinationKey) || string.IsNullOrWhiteSpace(sourceFolderKey))
        {
            return installDestination?.ReplaceFromStart(sourceFolderPath, destinationFolderPath, isIgnoreCase: true);
        }

        string destinationBase = TrimDirectoryPathEnd(destinationFolderPath);
        if (string.Equals(installDestinationKey, sourceFolderKey, StringComparison.OrdinalIgnoreCase))
        {
            return destinationBase;
        }

        string sourcePrefix = AppendDirectorySeparator(sourceFolderKey);
        if (!installDestinationKey.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return installDestination;
        }

        string relativePath = installDestinationKey.Substring(sourcePrefix.Length);
        return string.IsNullOrWhiteSpace(relativePath)
            ? destinationBase
            : Path.Combine(destinationBase, relativePath);
    }

    private static string AppendDirectorySeparator(string path)
    {
        return string.IsNullOrWhiteSpace(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string TrimDirectoryPathEnd(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            string root = Path.GetPathRoot(path);
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return path;
            }

            string rootTrimmed = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.IsNullOrWhiteSpace(rootTrimmed) && string.Equals(trimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase)
                ? root
                : trimmed;
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string CreateDirectoryComparisonKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(path.Trim());
            return TrimDirectoryPathEnd(fullPath);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool IsChartUnderFolder(LibraryChartRef chart, string folderPath)
    {
        return !string.IsNullOrWhiteSpace(chart?.Path)
            && !string.IsNullOrWhiteSpace(folderPath)
            && chart.Path.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public LibraryFixInstallationResult FixInstallationDirectory(
        IEnumerable<ChartFile> charts,
        Func<ChartPackage, string, bool> movePackageFiles,
        Func<ChartFile, bool> confirmDuplicateRemoval)
    {
        var result = new LibraryFixInstallationResult();
        var stopwatch = Stopwatch.StartNew();
        List<ChartFile> targets = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
        result.RequestedCount = targets.Count;
        foreach (ChartFile chart in targets)
        {
            PackageChartEntry entry = PackageChartEntry.FromChart(chart);
            if (entry == null)
            {
                continue;
            }
            var installPackage = ChartPackage.FromChartEntries([entry]);
            installPackage.path = chart.Path;
            installPackage.delete_parent = false;
            string oldPath = chart.Path;
            if (!(movePackageFiles?.Invoke(installPackage, chart.InstallDestination) ?? false))
            {
                continue;
            }
            if (installPackage.ChartEntries.Count == 0)
            {
                result.DuplicateSkippedCount++;
                if (confirmDuplicateRemoval != null && confirmDuplicateRemoval(chart))
                {
                    LibraryChartRef removableChart = LibraryChartRef.FromChartFile(chart);
                    if (removableChart != null)
                    {
                        result.ChartsToRemove.Add(removableChart);
                    }
                }
                continue;
            }

            ChartFile movedChart = entry.Chart;
            BMSFile movedBmsFile = movedChart?.GetBmsStorageOwner();
            if (movedBmsFile != null)
            {
                result.MutationDelta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = movedChart,
                    NewPath = movedBmsFile.path,
                    OldPath = oldPath
                });
                result.MutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Chart = movedChart,
                    NewInstallDestination = null
                });
                result.MaintenanceCharts.Add(movedChart);
            }
            else
            {
                LR2SongDBExtended.bmson_song movedBmsonSong = movedChart?.GetBmsonStorageOwner();
                if (movedBmsonSong == null)
                {
                    continue;
                }
                result.MutationDelta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = movedChart,
                    NewPath = movedBmsonSong.path,
                    OldPath = oldPath
                });
                result.MaintenanceCharts.Add(movedChart);
            }
            result.MutationDelta.RaiseLibraryChartsChanged = true;
            result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
            result.MutationDelta.InvalidateParentFolderCache = true;
            result.MutationDelta.ClearDuplicatedCache = true;
            result.MovedCount++;
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        result.MutationDelta.TotalMs = result.TotalMs;
        return result;
    }

    public LibraryMutationDelta RenameLibraryFileExtensions(
        IEnumerable<ChartFile> charts,
        string newExt,
        bool unregister,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        var delta = new LibraryMutationDelta();
        var stopwatch = Stopwatch.StartNew();
        foreach (BMSFile file in (charts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(file => file != null && File.Exists(file.path)))
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    delta.RenamedCount++;
                    if (unregister)
                    {
                        delta.ChartsToUnregister.Add(ChartFileProjection.FromBmsFile(
                            file,
                            includeWarningSnapshot: true,
                            includeResourceReferences: false));
                    }
                    else
                    {
                        delta.ChartPathChanges.Add(new LibraryChartPathChange
                        {
                            Chart = ChartFileProjection.FromBmsFile(
                                file,
                                includeWarningSnapshot: true,
                                includeResourceReferences: false),
                            NewPath = renameResult.FinalPath
                        });
                        delta.RaiseLibraryChartsChanged = true;
                    }
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    delta.DuplicateDeletedCount++;
                    delta.ChartsToUnregister.Add(ChartFileProjection.FromBmsFile(
                        file,
                        includeWarningSnapshot: true,
                        includeResourceReferences: false));
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
        delta.InvalidateInstalledDirectoryIndex = delta.ChartPathChanges.Count > 0 || delta.ChartsToUnregister.Count > 0;
        delta.InvalidateParentFolderCache = delta.ChartPathChanges.Count > 0 || delta.ChartsToUnregister.Count > 0;
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

    public List<ChartPackage> GetPendingPackagesFullyCoveredBySelection(IEnumerable<ChartPackage> pendingPackages, HashSet<string> selectedPaths)
    {
        List<ChartPackage> result = [];
        foreach (ChartPackage package in (pendingPackages ?? []).Where(pkg => pkg != null))
        {
            List<PackageChartEntry> packageEntries = package.ChartEntries;
            if (packageEntries.Count > 0 && packageEntries.All(entry => IsMatchedRemovedEntry(entry, selectedPaths)))
            {
                result.Add(package);
            }
        }
        return result;
    }

    private static bool IsMatchedRemovedEntry(PackageChartEntry entry, HashSet<string> removedPaths)
    {
        if (entry?.Chart == null)
        {
            return false;
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

}
