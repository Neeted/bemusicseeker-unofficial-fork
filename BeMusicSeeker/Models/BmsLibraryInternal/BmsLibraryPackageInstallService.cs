using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum ComponentMoveDecision
{
    Move,
    Overwrite,
    SkipSame,
    SkipOlderOrEqual
}

internal enum SmartOverwriteHashCompareResult
{
    Same,
    Different,
    Unavailable
}

internal sealed class ComponentMovePlanItem
{
    public string SourcePath { get; set; }

    public string DestinationPath { get; set; }
}

internal sealed class ComponentMovePlanBuildResult
{
    public List<ComponentMovePlanItem> PlanItems { get; } = new List<ComponentMovePlanItem>();

    public int SkippedByExclusion { get; set; }
}

internal sealed class ComponentMoveSummary
{
    public int Total { get; set; }

    public int Moved { get; set; }

    public int Overwritten { get; set; }

    public int SkippedSame { get; set; }

    public int SkippedOlder { get; set; }

    public int DeletedAfterSkip { get; set; }

    public int SkippedByExclusion { get; set; }

    public int SkippedSamePath { get; set; }

    public int Failed { get; set; }

    public int RenamedKeep { get; set; }

    public int RenamedFromOverwrite { get; set; }

    public int RenamedFromSkipOlder { get; set; }

    public int HashChecked { get; set; }

    public int HashSameSkip { get; set; }

    public int HashDiffRenamed { get; set; }

    public int HashUnavailableRenamed { get; set; }
}

internal sealed class BmsLibraryPackageInstallService
{
    public static readonly TimeSpan SmartComponentOverwriteTimeTolerance = TimeSpan.FromSeconds(2.0);

    private static readonly HashSet<string> smartOverwriteProtectedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".bmx",
        ".pmx",
        ".bmson"
    };

    public bool IsSmartOverwriteProtectedExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }
        string extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) && smartOverwriteProtectedExtensions.Contains(extension);
    }

    public ComponentMoveDecision DecideComponentMove(string srcFilePath, string dstFilePath)
    {
        if (!File.Exists(dstFilePath))
        {
            return ComponentMoveDecision.Move;
        }
        FileInfo sourceInfo = new FileInfo(srcFilePath);
        FileInfo destinationInfo = new FileInfo(dstFilePath);
        DateTime sourceWriteTime = sourceInfo.LastWriteTimeUtc;
        DateTime destinationWriteTime = destinationInfo.LastWriteTimeUtc;
        if (sourceInfo.Length == destinationInfo.Length && Math.Abs((sourceWriteTime - destinationWriteTime).TotalSeconds) <= SmartComponentOverwriteTimeTolerance.TotalSeconds)
        {
            return ComponentMoveDecision.SkipSame;
        }
        return sourceWriteTime > destinationWriteTime ? ComponentMoveDecision.Overwrite : ComponentMoveDecision.SkipOlderOrEqual;
    }

    public bool IsSamePath(string path1, string path2)
    {
        try
        {
            return string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string GetRelativePathFromDirectory(string rootDirectory, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return Path.GetFileName(targetPath);
        }
        string normalizedRootDirectory = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(normalizedRootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(targetPath);
        }
        return targetPath.Substring(normalizedRootDirectory.Length);
    }

    public ComponentMovePlanBuildResult BuildComponentMovePlan(IEnumerable<string> installComponentFiles, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        ComponentMovePlanBuildResult result = new ComponentMovePlanBuildResult();
        HashSet<string> excludedPathSet = ((excludedComponentPaths != null && excludedComponentPaths.Count > 0) ? new HashSet<string>(excludedComponentPaths, StringComparer.OrdinalIgnoreCase) : null);
        foreach (string installComponentFile in installComponentFiles ?? Enumerable.Empty<string>())
        {
            if (File.Exists(installComponentFile))
            {
                if (excludedPathSet != null && excludedPathSet.Contains(installComponentFile))
                {
                    result.SkippedByExclusion++;
                    continue;
                }
                result.PlanItems.Add(new ComponentMovePlanItem
                {
                    SourcePath = installComponentFile,
                    DestinationPath = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile))
                });
                continue;
            }
            if (!Directory.Exists(installComponentFile))
            {
                throw new FileNotFoundException("Component path was not found.", installComponentFile);
            }
            string destinationRoot = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile));
            foreach (string path in Directory.EnumerateFiles(installComponentFile, "*", SearchOption.AllDirectories))
            {
                if (excludedPathSet != null && excludedPathSet.Contains(path))
                {
                    result.SkippedByExclusion++;
                    continue;
                }
                result.PlanItems.Add(new ComponentMovePlanItem
                {
                    SourcePath = path,
                    DestinationPath = Path.Combine(destinationRoot, GetRelativePathFromDirectory(installComponentFile, path))
                });
            }
        }
        return result;
    }

    public List<BMSPackage> DeduplicatePackagesByPathOrReference(IEnumerable<BMSPackage> packages)
    {
        List<BMSPackage> deduplicated = new List<BMSPackage>();
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<BMSPackage> references = new HashSet<BMSPackage>();
        foreach (BMSPackage package in packages ?? Enumerable.Empty<BMSPackage>())
        {
            if (package == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(package.path))
            {
                if (!paths.Add(package.path))
                {
                    continue;
                }
            }
            else if (!references.Add(package))
            {
                continue;
            }
            deduplicated.Add(package);
        }
        return deduplicated;
    }

    public List<BMSFile> DeduplicateFilesByPathOrReference(IEnumerable<BMSFile> files)
    {
        List<BMSFile> deduplicated = new List<BMSFile>();
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> references = new HashSet<BMSFile>();
        foreach (BMSFile file in files ?? Enumerable.Empty<BMSFile>())
        {
            if (file == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(file.path))
            {
                if (!paths.Add(file.path))
                {
                    continue;
                }
            }
            else if (!references.Add(file))
            {
                continue;
            }
            deduplicated.Add(file);
        }
        return deduplicated;
    }

    public List<BMSPackage> GetPendingPackagesContainingOnlyInstalledCharts(IEnumerable<BMSPackage> pendingPackages, Func<string, bool> isInstalledHash)
    {
        List<BMSPackage> result = new List<BMSPackage>();
        foreach (BMSPackage package in pendingPackages ?? Enumerable.Empty<BMSPackage>())
        {
            List<BMSFile> files = (package?.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            if (files.Count == 0)
            {
                continue;
            }
            if (files.All((BMSFile file) => IsBmsHashAvailable(file.hash) && isInstalledHash != null && isInstalledHash(file.hash)))
            {
                result.Add(package);
            }
        }
        return result;
    }

    public List<BMSFile> GetPendingBmsFilesSnapshot(IEnumerable<BMSPackage> pendingPackages)
    {
        return DeduplicateFilesByPathOrReference((pendingPackages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage package) => package != null)
            .SelectMany((BMSPackage package) => package.BMSFiles)
            .Where((BMSFile file) => file != null));
    }

    public PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<BMSPackage> pendingPackages, IEnumerable<BMSPackage> packagesToRemove = null, IEnumerable<BMSFile> filesToRemove = null, bool clearAll = false)
    {
        List<BMSPackage> currentPending = (pendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        PendingPackageMutationDelta delta = new PendingPackageMutationDelta();
        if (clearAll)
        {
            delta.HasChanges = currentPending.Count > 0;
            delta.InstallPathsToDelete = currentPending.Where((BMSPackage pkg) => !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return delta;
        }
        HashSet<BMSPackage> removedPackages = new HashSet<BMSPackage>((packagesToRemove ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null));
        HashSet<string> removedPackagePaths = new HashSet<string>((packagesToRemove ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null && !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path), StringComparer.OrdinalIgnoreCase);
        List<BMSFile> removedFileList = (filesToRemove ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        HashSet<string> removedPaths = new HashSet<string>(removedFileList.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path)).Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> removedFileRefs = new HashSet<BMSFile>(removedFileList);
        bool removeFiles = removedFileList.Count > 0;
        HashSet<string> installPathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSPackage package in currentPending)
        {
            if (removedPackages.Contains(package) || (!string.IsNullOrWhiteSpace(package.path) && removedPackagePaths.Contains(package.path)))
            {
                delta.HasChanges = true;
                if (!string.IsNullOrWhiteSpace(package.path))
                {
                    installPathsToDelete.Add(package.path);
                }
                continue;
            }
            if (!removeFiles)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            List<BMSFile> packageFiles = package.BMSFiles.Where((BMSFile file) => file != null).ToList();
            if (packageFiles.Count == 0)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            List<BMSFile> remainingFiles = packageFiles.Where((BMSFile file) => !IsMatchedRemovedFile(file, removedPaths, removedFileRefs)).ToList();
            if (remainingFiles.Count == packageFiles.Count)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            delta.HasChanges = true;
            if (remainingFiles.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(package.path))
                {
                    installPathsToDelete.Add(package.path);
                }
                continue;
            }
            package.BMSFiles.Clear();
            package.BMSFiles.AddRange(remainingFiles);
            delta.RemainingPackages.Add(package);
        }
        delta.InstallPathsToDelete = installPathsToDelete.ToList();
        return delta;
    }

    public PendingInstallBatchPlan BuildEstimatedInstallBatchPlan(IEnumerable<BMSPackage> requestedPackages, IEnumerable<BMSPackage> currentPendingPackages, IEnumerable<BMSFile> installedFiles, bool deletePendingPackageSourceAfterInstall, Func<BMSPackage, string, ISet<string>, int> countComponentMoveTargets)
    {
        Stopwatch planStopwatch = Stopwatch.StartNew();
        PendingInstallBatchPlan plan = new PendingInstallBatchPlan();
        Stopwatch filterStopwatch = Stopwatch.StartNew();
        List<BMSPackage> pendingSnapshot = (currentPendingPackages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage pkg) => pkg != null).ToList();
        HashSet<BMSPackage> pendingPackageSet = new HashSet<BMSPackage>(pendingSnapshot);
        plan.SelectedPendingPackages.AddRange(DeduplicatePackagesByPathOrReference(requestedPackages).Where((BMSPackage pkg) => pendingPackageSet.Contains(pkg)));
        filterStopwatch.Stop();
        plan.FilterMs = filterStopwatch.ElapsedMilliseconds;
        plan.SelectedPendingCount = plan.SelectedPendingPackages.Count;

        Stopwatch groupBuildStopwatch = Stopwatch.StartNew();
        HashSet<string> installedHashes = new HashSet<string>((installedFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && IsBmsHashAvailable(file.hash)).Select((BMSFile file) => file.hash), StringComparer.OrdinalIgnoreCase);
        HashSet<string> moveGuardHashes = new HashSet<string>(installedHashes, StringComparer.OrdinalIgnoreCase);
        HashSet<string> reservedHashes = new HashSet<string>(moveGuardHashes, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PendingInstallBatchGroup> groupsByDestination = new Dictionary<string, PendingInstallBatchGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSPackage originalPackage in plan.SelectedPendingPackages)
        {
            List<BMSFile> packageFiles = (originalPackage.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null).ToList();
            if (packageFiles.Count == 0)
            {
                continue;
            }
            List<BMSFile> installedInLibraryFiles = new List<BMSFile>();
            List<BMSFile> installTargetPackageFiles = new List<BMSFile>();
            List<BMSFile> duplicateInBatchFiles = new List<BMSFile>();
            foreach (BMSFile packageFile in packageFiles)
            {
                if (!IsBmsHashAvailable(packageFile.hash))
                {
                    installTargetPackageFiles.Add(packageFile);
                }
                else if (installedHashes.Contains(packageFile.hash))
                {
                    installedInLibraryFiles.Add(packageFile);
                }
                else if (!reservedHashes.Add(packageFile.hash))
                {
                    duplicateInBatchFiles.Add(packageFile);
                }
                else
                {
                    installTargetPackageFiles.Add(packageFile);
                }
            }
            ApplyAlreadyInstalledWarning(installedInLibraryFiles);
            ApplyAlreadyInstalledWarning(duplicateInBatchFiles);
            List<BMSFile> installWorkPackageFiles = installTargetPackageFiles;
            bool isResourceOnlyInstall = false;
            string destinationDirectory = null;
            if (installTargetPackageFiles.Count == 0)
            {
                if (installedInLibraryFiles.Count == 0)
                {
                    continue;
                }
                List<string> destinations = packageFiles.Select((BMSFile file) => file.instl_dst).Where((string dst) => !string.IsNullOrWhiteSpace(dst)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (destinations.Count != 1)
                {
                    continue;
                }
                destinationDirectory = destinations[0];
                isResourceOnlyInstall = true;
                installWorkPackageFiles = packageFiles;
            }
            else
            {
                if (installTargetPackageFiles.Any((BMSFile file) => string.IsNullOrWhiteSpace(file.instl_dst)))
                {
                    continue;
                }
                destinationDirectory = installTargetPackageFiles.Select((BMSFile file) => file.instl_dst).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(destinationDirectory) || installTargetPackageFiles.Any((BMSFile file) => !string.Equals(file.instl_dst, destinationDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            BMSPackage installWorkPackage = new BMSPackage(installWorkPackageFiles)
            {
                path = originalPackage.path,
                delete_parent = originalPackage.delete_parent
            };
            HashSet<string> excludedPaths = null;
            if (installedInLibraryFiles.Count > 0 || duplicateInBatchFiles.Count > 0 || isResourceOnlyInstall)
            {
                excludedPaths = new HashSet<string>(installedInLibraryFiles.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path)).Select((BMSFile file) => file.path), StringComparer.OrdinalIgnoreCase);
                foreach (BMSFile duplicateFile in duplicateInBatchFiles)
                {
                    if (!string.IsNullOrWhiteSpace(duplicateFile?.path))
                    {
                        excludedPaths.Add(duplicateFile.path);
                    }
                }
                if (isResourceOnlyInstall)
                {
                    foreach (BMSFile packageFile in packageFiles)
                    {
                        if (!string.IsNullOrWhiteSpace(packageFile?.path))
                        {
                            excludedPaths.Add(packageFile.path);
                        }
                    }
                }
                if (excludedPaths.Count == 0)
                {
                    excludedPaths = null;
                }
            }
            if (isResourceOnlyInstall)
            {
                int componentMoveTargetCount = countComponentMoveTargets?.Invoke(installWorkPackage, destinationDirectory, excludedPaths) ?? 0;
                if (componentMoveTargetCount == 0)
                {
                    if (deletePendingPackageSourceAfterInstall)
                    {
                        plan.CleanupOnlyCandidates.Add(originalPackage);
                    }
                    continue;
                }
            }
            if (!groupsByDestination.TryGetValue(destinationDirectory, out PendingInstallBatchGroup group))
            {
                group = new PendingInstallBatchGroup
                {
                    DestinationDirectory = destinationDirectory
                };
                groupsByDestination[destinationDirectory] = group;
                plan.Groups.Add(group);
            }
            group.Items.Add(new PendingInstallBatchItem
            {
                OriginalPackage = originalPackage,
                InstallWorkPackage = installWorkPackage,
                DestinationDirectory = destinationDirectory,
                ExcludedComponentPaths = excludedPaths,
                IsResourceOnlyInstall = isResourceOnlyInstall,
                InstallTargetFileCount = installTargetPackageFiles.Count
            });
            plan.GroupedPackageCount++;
            plan.InstallTargetFileCount += installTargetPackageFiles.Count;
        }
        groupBuildStopwatch.Stop();
        plan.GroupBuildMs = groupBuildStopwatch.ElapsedMilliseconds;
        plan.CleanupOnlyCandidateCount = plan.CleanupOnlyCandidates.Count;
        plan.MoveGuardHashes = moveGuardHashes;
        planStopwatch.Stop();
        plan.PlanBuildMs = planStopwatch.ElapsedMilliseconds;
        return plan;
    }

    public PendingInstallBatchResult ExecuteEstimatedInstallBatchPlan(
        PendingInstallBatchPlan plan,
        bool deletePendingPackageSourceAfterInstall,
        Func<IEnumerable<BMSPackage>, string, List<BMSFile>, List<BMSPackage>, Dictionary<BMSPackage, HashSet<string>>, HashSet<string>, bool, bool, List<BMSPackage>> installPackages,
        Func<BMSPackage, string, BMSPackage> createInstalledDisplayPackage,
        Func<BMSPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource,
        Action<string> logInfo = null)
    {
        PendingInstallBatchResult result = new PendingInstallBatchResult();
        if (plan == null)
        {
            return result;
        }
        foreach (PendingInstallBatchGroup groupEntry in plan.Groups)
        {
            Stopwatch groupStopwatch = Stopwatch.StartNew();
            List<BMSPackage> destinationPackages = groupEntry.Items.Select((PendingInstallBatchItem item) => item.OriginalPackage).Where((BMSPackage pkg) => pkg != null).ToList();
            List<BMSPackage> installWorkPackages = groupEntry.Items.Select((PendingInstallBatchItem item) => item.InstallWorkPackage).Where((BMSPackage pkg) => pkg != null).ToList();
            Dictionary<BMSPackage, HashSet<string>> excludedComponentPathsByWorkPackage = groupEntry.Items
                .Where((PendingInstallBatchItem item) => item.ExcludedComponentPaths != null && item.InstallWorkPackage != null)
                .ToDictionary((PendingInstallBatchItem item) => item.InstallWorkPackage, (PendingInstallBatchItem item) => item.ExcludedComponentPaths);
            Stopwatch installStopwatch = Stopwatch.StartNew();
            List<BMSPackage> failedInstallWorkPackages = installPackages?.Invoke(
                installWorkPackages,
                groupEntry.DestinationDirectory,
                result.DeferredMaintenanceTargets,
                result.DeferredInstalledPackages,
                excludedComponentPathsByWorkPackage,
                plan.MoveGuardHashes,
                true,
                deletePendingPackageSourceAfterInstall) ?? new List<BMSPackage>();
            installStopwatch.Stop();
            Dictionary<BMSPackage, PendingInstallBatchItem> itemByInstallWorkPackage = groupEntry.Items
                .Where((PendingInstallBatchItem item) => item.InstallWorkPackage != null)
                .ToDictionary((PendingInstallBatchItem item) => item.InstallWorkPackage);
            HashSet<BMSPackage> failedOriginalPackages = new HashSet<BMSPackage>(
                failedInstallWorkPackages.Where((BMSPackage workPkg) => itemByInstallWorkPackage.ContainsKey(workPkg))
                    .Select((BMSPackage workPkg) => itemByInstallWorkPackage[workPkg].OriginalPackage));
            foreach (BMSPackage failedOriginalPackage in failedOriginalPackages)
            {
                if (failedOriginalPackage != null)
                {
                    result.FailedPackages.Add(failedOriginalPackage);
                }
            }
            Stopwatch installDbStopwatch = Stopwatch.StartNew();
            List<string> installRowsToDelete = new List<string>();
            foreach (PendingInstallBatchItem item in groupEntry.Items)
            {
                if (item == null || item.OriginalPackage == null || failedOriginalPackages.Contains(item.OriginalPackage))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.OriginalPackage.path))
                {
                    installRowsToDelete.Add(item.OriginalPackage.path);
                    result.InstallRowsToDelete.Add(item.OriginalPackage.path);
                }
                if (item.IsResourceOnlyInstall)
                {
                    BMSPackage installedDisplayPackage = createInstalledDisplayPackage?.Invoke(item.OriginalPackage, groupEntry.DestinationDirectory);
                    if (installedDisplayPackage != null && installedDisplayPackage.BMSFiles != null && installedDisplayPackage.BMSFiles.Count > 0)
                    {
                        result.DeferredInstalledPackages.Add(installedDisplayPackage);
                    }
                }
                foreach (BMSFile packageFile in item.OriginalPackage.BMSFiles ?? Enumerable.Empty<BMSFile>())
                {
                    if (packageFile != null)
                    {
                        packageFile.instl_dst = null;
                    }
                }
            }
            installDbStopwatch.Stop();
            Stopwatch pendingMarkStopwatch = Stopwatch.StartNew();
            int pendingCountBeforeRemove = plan.SelectedPendingPackages.Count;
            int removedPendingCount = 0;
            foreach (BMSPackage pendingPackage in destinationPackages)
            {
                if (pendingPackage != null && result.PendingPackagesToRemove.Add(pendingPackage))
                {
                    removedPendingCount++;
                }
            }
            pendingMarkStopwatch.Stop();
            groupStopwatch.Stop();
            logInfo?.Invoke(
                "InstallBMSPackagesToEstimatedDir group dst=" + groupEntry.DestinationDirectory +
                " packages=" + destinationPackages.Count +
                " workPackages=" + installWorkPackages.Count +
                " failedPackages=" + failedInstallWorkPackages.Count +
                " installMs=" + installStopwatch.ElapsedMilliseconds +
                " installDbMs=" + installDbStopwatch.ElapsedMilliseconds +
                " pendingMarkMs=" + pendingMarkStopwatch.ElapsedMilliseconds +
                " pendingBefore=" + pendingCountBeforeRemove +
                " pendingMarked=" + removedPendingCount +
                " totalGroupMs=" + groupStopwatch.ElapsedMilliseconds);
        }
        if (deletePendingPackageSourceAfterInstall && plan.CleanupOnlyCandidates.Count > 0)
        {
            foreach (BMSPackage cleanupOnlyPackage in plan.CleanupOnlyCandidates.Distinct())
            {
                if (cleanupOnlyPackage == null)
                {
                    continue;
                }
                (bool Success, CleanupSourceKind SourceKind) cleanupResult = cleanupPendingPackageSource != null
                    ? cleanupPendingPackageSource(cleanupOnlyPackage)
                    : (false, CleanupSourceKind.MissingSource);
                if (cleanupResult.Success)
                {
                    result.CleanupOnlySucceeded++;
                    if (cleanupResult.SourceKind == CleanupSourceKind.MissingSource)
                    {
                        result.CleanupOnlyMissingSource++;
                    }
                    if (!string.IsNullOrWhiteSpace(cleanupOnlyPackage.path))
                    {
                        result.InstallRowsToDelete.Add(cleanupOnlyPackage.path);
                    }
                    result.PendingPackagesToRemove.Add(cleanupOnlyPackage);
                    foreach (BMSFile packageFile in cleanupOnlyPackage.BMSFiles ?? Enumerable.Empty<BMSFile>())
                    {
                        if (packageFile != null)
                        {
                            packageFile.instl_dst = null;
                        }
                    }
                    logInfo?.Invoke("estimated_install_cleanup_only_success package=" + cleanupOnlyPackage.path + " kind=" + cleanupResult.SourceKind.ToString().ToLowerInvariant());
                }
                else
                {
                    result.CleanupOnlyFailed++;
                    logInfo?.Invoke("estimated_install_cleanup_only_failed package=" + cleanupOnlyPackage.path);
                }
            }
        }
        return result;
    }

    private static void ApplyAlreadyInstalledWarning(IEnumerable<BMSFile> files)
    {
        foreach (BMSFile file in files ?? Enumerable.Empty<BMSFile>())
        {
            if (file != null)
            {
                file.warning = Properties.Resources.Warning_AlreadyInstalled;
            }
        }
    }

    private static bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
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

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash);
    }
}
