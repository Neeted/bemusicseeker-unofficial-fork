using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using Microsoft.VisualBasic.FileIO;
using Ribbit.Logging;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum ComponentMoveDecision
{
    Move,
    Overwrite,
    SkipSame,
    SkipOlderOrEqual
}

internal sealed class ComponentMovePlanItem
{
    public string SourcePath { get; set; }

    public string DestinationPath { get; set; }
}

internal sealed class ComponentMovePlanBuildResult
{
    public List<ComponentMovePlanItem> PlanItems { get; } = [];

    public int SkippedByExclusion { get; set; }
}

/// <summary>
/// Immutable package-source target captured before a pending mutation starts.
/// Filesystem work consumes only this frozen path and package metadata.
/// </summary>
internal sealed class PendingPackageSourceDeletionTarget
{
    internal PendingPackageSourceDeletionTarget(ChartPackage package)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Path = package.path;
        DeleteParent = package.delete_parent;
    }

    internal ChartPackage Package { get; }

    internal string Path { get; }

    internal bool DeleteParent { get; }

}

/// <summary>
/// One force-install callback result, including the terminal filesystem/DB state
/// that determines whether the enclosing batch may continue.
/// </summary>
internal sealed class ForceInstallPackageApplyResult
{
    internal ForceInstallPackageApplyResult(
        IEnumerable<ChartPackage> failedPackages,
        bool manualRecoveryRequired,
        FileDbMutationBatchReceipt mutationReceipt = null)
    {
        FailedPackages = new List<ChartPackage>((failedPackages ?? [])
            .Where(package => package != null))
            .AsReadOnly();
        ManualRecoveryRequired = manualRecoveryRequired;
        MutationReceipt = mutationReceipt;
    }

    internal IReadOnlyList<ChartPackage> FailedPackages { get; }

    internal bool ManualRecoveryRequired { get; }

    /// <summary>
    /// Gets whether a post-durable finalizer failed in this apply result.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;

    internal FileDbMutationBatchReceipt MutationReceipt { get; }
}

/// <summary>
/// Executes pending/package workflows against caller-owned state snapshots.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryPackageInstallService
{
    private sealed class RequiredArchiveMetadataRestoreException : Exception
    {
        public RequiredArchiveMetadataRestoreException(string restoredPath, string detailMessage)
            : base(detailMessage)
        {
            RestoredPath = restoredPath;
        }

        public RequiredArchiveMetadataRestoreException() : base()
        {
        }

        public RequiredArchiveMetadataRestoreException(string message) : base(message)
        {
        }

        public RequiredArchiveMetadataRestoreException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public string RestoredPath { get; }
    }

    public static readonly TimeSpan SmartComponentOverwriteTimeTolerance = TimeSpan.FromSeconds(2.0);

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService = new();

    private static readonly HashSet<string> smartOverwriteProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".bmx",
        ".pmx"
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

    /// <summary>
    /// 異なる宛先型を拒否した後、同じ型のコンポーネント衝突に対する既存ポリシーを決定します。
    /// </summary>
    /// <param name="srcFilePath">コンポーネントの元ファイル。</param>
    /// <param name="dstFilePath">コンポーネントの宛先ファイル。</param>
    public ComponentMoveDecision DecideComponentMove(string srcFilePath, string dstFilePath)
    {
        FileDbMutationDestinationTypeGuard.ValidatePath(
            srcFilePath,
            dstFilePath,
            expectedIsDirectory: false);
        if (!LongPathFileSystem.FileExists(dstFilePath))
        {
            return ComponentMoveDecision.Move;
        }
        LongPathFileSystem.FileMetadata sourceInfo = LongPathFileSystem.GetFileMetadata(srcFilePath);
        LongPathFileSystem.FileMetadata destinationInfo = LongPathFileSystem.GetFileMetadata(dstFilePath);
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
        var result = new ComponentMovePlanBuildResult();
        HashSet<string> excludedPathSet = ((excludedComponentPaths != null && excludedComponentPaths.Count > 0) ? new HashSet<string>(excludedComponentPaths, StringComparer.OrdinalIgnoreCase) : null);
        foreach (string installComponentFile in installComponentFiles ?? [])
        {
            if (LongPathFileSystem.FileExists(installComponentFile))
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
            if (!LongPathFileSystem.DirectoryExists(installComponentFile))
            {
                throw new FileNotFoundException("Component path was not found.", installComponentFile);
            }
            string destinationRoot = Path.Combine(destinationDirectory, Path.GetFileName(installComponentFile));
            foreach (string path in LongPathFileSystem.EnumerateFiles(installComponentFile, "*", System.IO.SearchOption.AllDirectories))
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

    private static ISet<string> BuildComponentExclusionSet(IEnumerable<string> excludedComponentPaths, IEnumerable<PackageChartEntry> chartEntries)
    {
        var excludedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedComponentPaths != null)
        {
            foreach (string path in excludedComponentPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    excludedPathSet.Add(path);
                }
            }
        }
        foreach (PackageChartEntry entry in chartEntries ?? [])
        {
            string chartPath = entry?.Chart?.Path;
            if (!string.IsNullOrWhiteSpace(chartPath))
            {
                excludedPathSet.Add(chartPath);
            }
        }
        return excludedPathSet;
    }

    private static List<PackageChartEntry> SelectInstallTargetEntries(ChartPackage package, ISet<string> installComponentPathSet, string sourcePath, bool isSingleFile)
    {
        List<PackageChartEntry> entries = package?.ChartEntries ?? [];
        if (isSingleFile)
        {
            return [.. entries.Where(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path) && installComponentPathSet.Contains(entry.Chart.Path))];
        }

        string normalizedSourceRoot = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return [.. entries.Where(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path)
            && (installComponentPathSet.Contains(entry.Chart.Path)
                || entry.Chart.Path.StartsWith(normalizedSourceRoot, StringComparison.OrdinalIgnoreCase)))];
    }

    private static string BuildDestinationChartPath(string sourceRootPath, string destinationDirectory, ChartFile chart)
    {
        string chartPath = chart?.Path;
        if (string.IsNullOrWhiteSpace(chartPath))
        {
            return string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(sourceRootPath) && LongPathFileSystem.DirectoryExists(sourceRootPath))
        {
            string normalizedRootDirectory = sourceRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relativePath = chartPath.StartsWith(normalizedRootDirectory, StringComparison.OrdinalIgnoreCase)
                ? chartPath.Substring(normalizedRootDirectory.Length)
                : Path.GetFileName(chartPath);
            return Path.Combine(destinationDirectory, relativePath);
        }
        return Path.Combine(destinationDirectory, Path.GetFileName(chartPath));
    }

    private static string ResolveChartDestinationPath(
        string sourceRootPath,
        string destinationDirectory,
        ChartFile chart,
        ISet<string> reservedDestinationPaths = null)
    {
        string destinationChartPath = BuildDestinationChartPath(
            sourceRootPath,
            destinationDirectory,
            chart);
        while (!string.IsNullOrWhiteSpace(destinationChartPath)
            && (LongPathFileSystem.EntryExists(destinationChartPath)
                || reservedDestinationPaths?.Contains(destinationChartPath) == true))
        {
            string fileName = Path.GetFileNameWithoutExtension(destinationChartPath);
            string extension = Path.GetExtension(destinationChartPath);
            destinationChartPath = Path.Combine(
                Path.GetDirectoryName(destinationChartPath) ?? destinationDirectory,
                fileName + "_" + extension);
        }
        return destinationChartPath;
    }

    private void ValidatePackageDestinationTypes(
        string sourcePath,
        bool isAutoNaming,
        string destinationDirectory,
        IEnumerable<string> installComponentFiles,
        IEnumerable<PackageChartEntry> installTargetEntries,
        ISet<string> excludedComponentPaths)
    {
        FileDbMutationDestinationTypeGuard.ValidatePath(
            sourcePath,
            destinationDirectory,
            expectedIsDirectory: true);

        if (!isAutoNaming)
        {
            // BuildComponentMovePlan は共有する明示順の候補境界です。
            // 下の executor 用計画と同じ除外規則と相対パス規則を適用します。
            ComponentMovePlanBuildResult componentPlan = BuildComponentMovePlan(
                installComponentFiles,
                destinationDirectory,
                excludedComponentPaths);
            foreach (ComponentMovePlanItem componentItem in componentPlan.PlanItems)
            {
                if (string.IsNullOrWhiteSpace(componentItem?.SourcePath))
                {
                    continue;
                }
                FileDbMutationDestinationTypeGuard.ValidatePath(
                    componentItem.SourcePath,
                    componentItem.DestinationPath,
                    expectedIsDirectory: false);
            }
        }

        foreach (PackageChartEntry entry in installTargetEntries ?? [])
        {
            ChartFile chart = entry?.Chart;
            string destinationChartPath = isAutoNaming
                ? BuildDestinationChartPath(
                    sourcePath,
                    destinationDirectory,
                    chart)
                : ResolveChartDestinationPath(
                    sourcePath,
                    destinationDirectory,
                    chart);
            if (string.IsNullOrWhiteSpace(destinationChartPath)
                || string.IsNullOrWhiteSpace(chart?.Path))
            {
                continue;
            }
            FileDbMutationDestinationTypeGuard.ValidatePath(
                chart.Path,
                destinationChartPath,
                expectedIsDirectory: false);
        }
    }

    private static FileDbMutationReceipt CreatePreflightFailureReceipt(
        FileDbMutationPlan emptyPlan,
        string sourcePath,
        string destinationDirectory,
        FileDbMutationDestinationTypeConflictException exception)
    {
        return new FileDbMutationReceipt(
            emptyPlan.OperationId,
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [sourcePath],
            [destinationDirectory],
            [],
            [],
            [],
            exception,
            destinationTypeConflicts: [exception.Conflict]);
    }

    public List<ChartPackage> DeduplicatePackagesByPathOrReference(IEnumerable<ChartPackage> packages)
    {
        List<ChartPackage> deduplicated = [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<ChartPackage> references = [];
        foreach (ChartPackage package in packages ?? [])
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

    private static List<PendingPackageSourceDeletionTarget> DeduplicatePendingPackageSourceDeletionTargets(
        IEnumerable<PendingPackageSourceDeletionTarget> targets)
    {
        List<PendingPackageSourceDeletionTarget> deduplicated = [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<ChartPackage> references = [];
        foreach (PendingPackageSourceDeletionTarget target in targets ?? [])
        {
            if (target?.Package == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(target.Path))
            {
                if (!paths.Add(target.Path))
                {
                    continue;
                }
            }
            else if (!references.Add(target.Package))
            {
                continue;
            }
            deduplicated.Add(target);
        }
        return deduplicated;
    }

    private List<PendingChartDeletionTarget> DeduplicatePendingChartDeletionTargets(IEnumerable<PendingChartDeletionTarget> targets)
    {
        List<PendingChartDeletionTarget> deduplicated = [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<ChartFile> references = [];
        foreach (PendingChartDeletionTarget target in targets ?? [])
        {
            if (target == null)
            {
                continue;
            }
            string path = target.Path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!paths.Add(path))
                {
                    continue;
                }
            }
            else if (target.Chart != null && !references.Add(target.Chart))
            {
                continue;
            }
            deduplicated.Add(target);
        }
        return deduplicated;
    }

    public List<ChartPackage> GetPendingPackagesContainingOnlyInstalledCharts(IEnumerable<ChartPackage> pendingPackages, Func<ChartFile, bool> isInstalledChart)
    {
        List<ChartPackage> result = [];
        foreach (ChartPackage package in pendingPackages ?? [])
        {
            List<PackageChartEntry> entries = package?.ChartEntries ?? [];
            if (entries.Count == 0)
            {
                continue;
            }
            if (entries.All(entry => isInstalledChart != null && isInstalledChart(entry?.Chart)))
            {
                result.Add(package);
            }
        }
        return result;
    }

    private static bool IsBmsFormatChartFile(BMSFile file)
    {
        string extension = Path.GetExtension(file?.path);
        return file != null
            && !string.IsNullOrWhiteSpace(extension)
            && ChartFileKindResolver.BmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBmsFormatChartEntry(PackageChartEntry entry)
    {
        ChartFile chart = entry?.Chart;
        return IsBmsFormatChartFile(chart);
    }

    private static bool IsBmsFormatChartFile(ChartFile chart)
    {
        string extension = Path.GetExtension(chart?.Path);
        return chart?.Kind == ChartFileKind.Bms
            && ChartFileKindResolver.IsBmsChartFile(chart)
            && !string.IsNullOrWhiteSpace(extension)
            && ChartFileKindResolver.BmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static List<ChartFile> DeduplicateBmsFormatChartsByPathOrBmsReference(IEnumerable<ChartFile> charts)
    {
        List<ChartFile> deduplicated = [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> references = [];
        foreach (ChartFile chart in charts ?? [])
        {
            if (!IsBmsFormatChartFile(chart))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(chart.Path))
            {
                if (!paths.Add(chart.Path))
                {
                    continue;
                }
            }
            else if (!references.Add(chart.GetBmsStorageOwner()))
            {
                continue;
            }
            deduplicated.Add(chart);
        }
        return deduplicated;
    }

    private static List<BMSFile> GetBmsFormatChartFiles(IEnumerable<ChartFile> charts)
    {
        return [.. DeduplicateBmsFormatChartsByPathOrBmsReference(charts)
            .Select(chart => chart.GetBmsStorageOwner())
            .Where(IsBmsFormatChartFile)];
    }

    internal List<ChartFile> GetPendingBmsFormatChartFilesSnapshot(IEnumerable<ChartPackage> pendingPackages)
    {
        return DeduplicateBmsFormatChartsByPathOrBmsReference((pendingPackages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(IsBmsFormatChartEntry)
            .Select(entry => entry?.Chart));
    }

    /// <summary>
    /// ディレクトリ探索で見つかった譜面パッケージと、再統合候補ディレクトリをまとめて返します。
    /// </summary>
    internal ChartPackageDiscoveryResult SearchChartPackagesRecursivelyWithMetadata(string dirfullpath, double dupRateThreshInOnePkg, bool recursive = false)
    {
        var result = new ChartPackageDiscoveryResult();
        IEnumerable<string> fileSystemEntries;
        try
        {
            fileSystemEntries = [.. LongPathFileSystem.EnumerateFileSystemEntries(dirfullpath, "*", System.IO.SearchOption.TopDirectoryOnly)];
        }
        catch
        {
            return result;
        }
        if (fileSystemEntries == null || !fileSystemEntries.Any())
        {
            return result;
        }
        List<string> files = [.. fileSystemEntries.Where(LongPathFileSystem.FileExists)];
        List<string> directories = [.. fileSystemEntries.Where(LongPathFileSystem.DirectoryExists)];
        List<string> chartFiles = [.. files.Where(file => ChartFileKindResolver.IsSupportedChartFilePath(file) && LongPathFileSystem.FileExists(file))];
        if (chartFiles.Count > 0)
        {
            List<PackageChartEntry> parsedChartEntries = [.. chartFiles
                .Select(CreatePackageChartEntryForDiscovery)
                .Where(entry => entry != null)];
            bool hasCompleteChartList = parsedChartEntries.Count == chartFiles.Count;
            bool anyChartHasExistingResources = parsedChartEntries.Any(HasExistingPackageChartResources);
            if (chartFiles.Count == 1 || anyChartHasExistingResources)
            {
                result.Packages.Add(CreatePackageWithKnownCharts(dirfullpath, deleteParent: false, hasCompleteChartList ? parsedChartEntries : null));
            }
            else
            {
                List<IEnumerable<string>> resourcesByChart = [.. parsedChartEntries
                    .Select(EnumeratePackageGroupingResourcePaths)
                    .Select(paths => paths
                        .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
                        .Distinct()
                        .ToList()
                        .AsEnumerable())];
                if (resourcesByChart.Count > 0
                    && (double)resourcesByChart.Aggregate(Enumerable.Intersect).Count() / (double)resourcesByChart.Select(resourceList => resourceList.Count()).Min() >= dupRateThreshInOnePkg)
                {
                    result.Packages.Add(CreatePackageWithKnownCharts(dirfullpath, deleteParent: false, hasCompleteChartList ? parsedChartEntries : null));
                }
                else
                {
                    var entryByPath = parsedChartEntries
                        .Where(entry => !string.IsNullOrWhiteSpace(entry.Chart?.Path))
                        .ToDictionary(entry => entry.Chart.Path, StringComparer.OrdinalIgnoreCase);
                    result.Packages.AddRange(chartFiles.Select(filePath => CreatePackageWithKnownCharts(filePath, recursive, entryByPath.TryGetValue(filePath, out PackageChartEntry entry) ? new[] { entry } : null)));
                    result.RegroupEligibleSourceDirectories.Add(NormalizeDirectoryPath(dirfullpath));
                }
            }
        }
        else if (directories.Count > 0)
        {
            foreach (string dir in directories)
            {
                ChartPackageDiscoveryResult childResult = SearchChartPackagesRecursivelyWithMetadata(dir, dupRateThreshInOnePkg, recursive: true);
                result.Packages.AddRange(childResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(childResult.RegroupEligibleSourceDirectories);
            }
        }
        List<string> distinctRegroupEligibleSourceDirectories = [.. result.RegroupEligibleSourceDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        result.RegroupEligibleSourceDirectories.Clear();
        result.RegroupEligibleSourceDirectories.AddRange(distinctRegroupEligibleSourceDirectories);
        return result;
    }

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        return string.IsNullOrWhiteSpace(directoryPath)
            ? null
            : directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static BMSFile CreateBmsChartForDiscovery(string filePath)
    {
        try
        {
            BMSFile entry = BMSFile.CreateBMSFileFromFile(filePath);
            return entry;
        }
        catch
        {
            return null;
        }
    }

    private static PackageChartEntry CreatePackageChartEntryForDiscovery(string filePath)
    {
        if (ChartFileKindResolver.IsBmsonFilePath(filePath))
        {
            return PackageChartEntry.FromPath(filePath);
        }
        return PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(
            CreateBmsChartForDiscovery(filePath),
            includeWarningSnapshot: true,
            includeResourceReferences: false));
    }

    private static bool HasExistingPackageChartResources(PackageChartEntry entry)
    {
        if (entry?.Chart == null)
        {
            return false;
        }
        BMSFileMaintenanceInfo maintenanceInfo = BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(entry.Chart);
        return maintenanceInfo?.wav_files_existing > 0
            || maintenanceInfo?.bga_files_existing > 0
            || maintenanceInfo?.movie_files_existing > 0;
    }

    private static IEnumerable<string> EnumeratePackageGroupingResourcePaths(PackageChartEntry entry)
    {
        ChartResourceSnapshot resources = entry?.ResourceSnapshot;
        if (resources == null)
        {
            return [];
        }
        return resources.AudioRelativePaths
            .Concat(resources.VisualRelativePaths)
            .Concat(resources.MovieRelativePaths);
    }

    private static BMSFileMaintenanceInfo BuildPendingResourceHealthMaintenanceInfo(PackageChartEntry entry)
    {
        if (entry?.Chart == null || entry.ResourceSnapshot.TotalReferenceCount <= 0)
        {
            return null;
        }

        return BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(entry.Chart);
    }

    internal static IReadOnlyList<ChartWarning> ApplyPendingResourceHealthProjection(PackageChartEntry entry)
    {
        BMSFileMaintenanceInfo maintenanceInfo = BuildPendingResourceHealthMaintenanceInfo(entry);
        if (maintenanceInfo == null)
        {
            entry?.ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
            return [];
        }
        IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(maintenanceInfo);
        entry.ReplaceResourceHealthProjection(maintenanceInfo, warnings);
        return warnings;
    }

    internal static int ApplyPendingResourceHealthProjectionToEntries(IEnumerable<PackageChartEntry> entries)
    {
        int warningEntryCount = 0;
        foreach (PackageChartEntry entry in entries ?? [])
        {
            if (entry?.Chart == null)
            {
                continue;
            }

            IReadOnlyList<ChartWarning> warnings = ApplyPendingResourceHealthProjection(entry);
            if (warnings.Count > 0)
            {
                warningEntryCount++;
            }
        }
        return warningEntryCount;
    }

    private static ChartPackage CreatePackageWithKnownCharts(string packagePath, bool deleteParent, IEnumerable<PackageChartEntry> knownChartEntries)
    {
        string canonicalPackagePath = PackageLifecycleOwner.TryNormalizePendingPackagePath(
            packagePath,
            out string normalizedPackagePath)
            ? normalizedPackagePath
            : packagePath;
        List<PackageChartEntry> knownEntries = [.. (knownChartEntries ?? [])
            .Where(entry => entry?.Chart != null)];
        if (LongPathFileSystem.DirectoryExists(canonicalPackagePath))
        {
            List<PackageChartEntry> recursiveEntries = [.. PackageInstallEstimationSnapshotBuilder
                .BuildPackageChartDiscoverySnapshot(canonicalPackagePath)
                .ChartEntries
                .Where(entry => entry?.Chart != null)];
            if (recursiveEntries.Count > 0)
            {
                knownEntries = recursiveEntries;
            }
        }

        if (knownEntries.Count == 0)
        {
            return new ChartPackage
            {
                path = canonicalPackagePath,
                delete_parent = deleteParent
            };
        }

        ChartPackage package = ChartPackage.FromChartEntries(knownEntries);
        package.path = canonicalPackagePath;
        package.delete_parent = deleteParent;
        return package;
    }

    /// <summary>
    /// パッケージ直下以外にある譜面へ、保留画面で最優先表示する警告を付与します。
    /// </summary>
    /// <param name="package">判定対象の保留パッケージ。</param>
    /// <returns>入れ子譜面の警告を付与または維持した場合は true。</returns>
    internal static bool ApplyNestedChartFileWarnings(ChartPackage package)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.path) || !LongPathFileSystem.DirectoryExists(package.path))
        {
            return false;
        }

        bool hasNestedChart = false;
        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            if (!IsNestedChartFileInPackage(package.path, entry?.Chart?.Path))
            {
                continue;
            }

            entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
            entry.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
            hasNestedChart = true;
        }

        return hasNestedChart;
    }

    private static bool IsNestedChartFileInPackage(string packageDirectoryPath, string chartFilePath)
    {
        if (string.IsNullOrWhiteSpace(packageDirectoryPath) || string.IsNullOrWhiteSpace(chartFilePath))
        {
            return false;
        }

        try
        {
            string normalizedPackageDirectory = Path.GetFullPath(packageDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartFilePath)) ?? string.Empty;
            chartDirectory = chartDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.Equals(normalizedPackageDirectory, chartDirectory, StringComparison.OrdinalIgnoreCase)
                && chartDirectory.StartsWith(normalizedPackageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathUnderDirectory(string fullPath, string rootDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(rootDirectoryPath))
        {
            return false;
        }

        string normalizedRootDirectoryPath = rootDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRootDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteExtractedTemporaryDirectory(string tempDirectoryPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions, Action<string> logInfo)
    {
        if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !LongPathFileSystem.DirectoryExists(tempDirectoryPath))
        {
            return;
        }

        try
        {
            if (fileMutationService != null)
            {
                fileMutationService.DeleteDirectoryDirect(tempDirectoryPath, recursive: true, targetOnlyFileMutationOptions);
            }
            else
            {
                LongPathFileSystem.DeleteDirectory(tempDirectoryPath, recursive: true);
            }
        }
        catch (Exception cleanupException)
        {
            logInfo?.Invoke("auto_install cleanup_failed path=" + tempDirectoryPath + " error=" + cleanupException.Message);
        }
    }

    /// <summary>
    /// Expands package sources while publishing progress through the narrow
    /// feature-local writer.  The writer is intentionally independent from
    /// the filesystem and catalog mutation callbacks so a producer cannot
    /// retain per-item post-commit closures.
    /// </summary>
    internal List<string> ExpandInstallSourcesWithProgress(
        IEnumerable<string> installPaths,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInfo,
        IBmsLibraryDialogService dialogService,
        IPackageInstallProgressWriter progressWriter,
        CancellationToken token,
        Action<Action> deferPostCommitEffect)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        string[] archiveExtensions = [".zip", ".7z", ".rar", ".lzh"];
        string[] normalizedInstallPaths = [.. (installPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        int archiveSourceTotal = normalizedInstallPaths.Count(path => archiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
        int archiveSourceIndex = 0;
        List<string> expandedPaths = [];
        for (int i = 0; i < normalizedInstallPaths.Length; i++)
        {
            string installPath = normalizedInstallPaths[i];
            if (token.IsCancellationRequested)
            {
                break;
            }
            bool isArchivePath = archiveExtensions.Any(ext => installPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            string extractedTempDirectoryPath = null;
            Stopwatch sourceStopwatch = Stopwatch.StartNew();
            try
            {
                if (!isArchivePath)
                {
                    expandedPaths.Add(installPath);
                }
                else
                {
                    archiveSourceIndex++;
                    progressWriter.TryWrite(PackageInstallProgressUpdate.ArchiveExtractStarted(
                        installPath,
                        archiveSourceIndex,
                        archiveSourceTotal));
                    extractedTempDirectoryPath = TempDirectoryPublisher.Get();
                    logInfo?.Invoke("auto_install extract_start path=" + installPath + " destination=" + extractedTempDirectoryPath);
                    Stopwatch extractStopwatch = Stopwatch.StartNew();
                    List<ArchiveEntryMetadata> archiveEntries = SevenZipArchiveExtractor.ExtractArchiveEntries(installPath, extractedTempDirectoryPath);
                    extractStopwatch.Stop();
                    logInfo?.Invoke("auto_install extract_done path=" + installPath + " destination=" + extractedTempDirectoryPath + " entries=" + archiveEntries.Count + " elapsedMs=" + extractStopwatch.ElapsedMilliseconds);
                    foreach (ArchiveEntryMetadata entry in archiveEntries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.FileName))
                        {
                            continue;
                        }

                        string entryPath = entry.FileName.Replace('/', Path.DirectorySeparatorChar);
                        string fullPath = Path.GetFullPath(Path.Combine(extractedTempDirectoryPath, entryPath));
                        if (!IsPathUnderDirectory(fullPath, extractedTempDirectoryPath))
                        {
                            continue;
                        }

                        bool isFile = !entry.IsFolder && LongPathFileSystem.FileExists(fullPath);
                        bool isDirectory = entry.IsFolder && LongPathFileSystem.DirectoryExists(fullPath);
                        if (!isFile && !isDirectory)
                        {
                            continue;
                        }

                        if (isFile)
                        {
                            if (entry.LastWriteTime <= DateTime.MinValue)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_required_failed archive=" + installPath + " path=" + fullPath + " reason=last_write_time_missing");
                                throw new RequiredArchiveMetadataRestoreException(fullPath, Resources.Warn_ArchiveLastWriteTimeMissingDetail);
                            }

                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, false, null, entry.LastWriteTime, targetOnlyFileMutationOptions);
                            }
                            catch (Exception lastWriteTimeRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_required_failed archive=" + installPath + " path=" + fullPath + " error=" + lastWriteTimeRestoreException.Message);
                                throw new RequiredArchiveMetadataRestoreException(fullPath, lastWriteTimeRestoreException.Message);
                            }
                        }
                        else if (isDirectory && entry.LastWriteTime > DateTime.MinValue)
                        {
                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, true, null, entry.LastWriteTime, targetOnlyFileMutationOptions);
                            }
                            catch (Exception directoryLastWriteRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=directory_last_write_time error=" + directoryLastWriteRestoreException.Message);
                            }
                        }

                        DateTime? creationTimeToRestore = entry.CreationTime > DateTime.MinValue ? entry.CreationTime : (DateTime?)null;
                        if (creationTimeToRestore.HasValue)
                        {
                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, isDirectory, creationTimeToRestore, null, targetOnlyFileMutationOptions);
                            }
                            catch (Exception creationTimeRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=creation_time error=" + creationTimeRestoreException.Message);
                            }
                        }

                        if (entry.LastAccessTime > DateTime.MinValue)
                        {
                            try
                            {
                                if (isFile)
                                {
                                    LongPathFileSystem.SetLastAccessTime(fullPath, isDirectory: false, entry.LastAccessTime);
                                }
                                else
                                {
                                    LongPathFileSystem.SetLastAccessTime(fullPath, isDirectory: true, entry.LastAccessTime);
                                }
                            }
                            catch (Exception lastAccessTimeRestoreException)
                            {
                                logInfo?.Invoke("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=last_access_time error=" + lastAccessTimeRestoreException.Message);
                            }
                        }
                    }
                    expandedPaths.Add(extractedTempDirectoryPath);
                    extractedTempDirectoryPath = null;
                    TempDirectoryPublisher.TryDeleteManagedPath(
                        installPath,
                        logInfo,
                        (path, cleanupException) => logInfo?.Invoke("auto_install cleanup_failed path=" + path + " error=" + cleanupException.Message));
                }
            }
            catch (RequiredArchiveMetadataRestoreException metadataRestoreException)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logInfo);
                Action showFailure = () => dialogService?.Show(
                    string.Format(Resources.Warn_ArchiveTimestampRestoreFailed, installPath, metadataRestoreException.RestoredPath, metadataRestoreException.Message),
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
                deferPostCommitEffect(showFailure);
            }
            catch (Exception ex)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logInfo);
                sourceStopwatch.Stop();
                logInfo?.Invoke("auto_install extract_failed path=" + installPath + " elapsedMs=" + sourceStopwatch.ElapsedMilliseconds + " error=" + ex.Message);
                Action showFailure = () => dialogService?.Show(
                    string.Format(Resources.Warn_ArchiveExtractFailed, installPath, ex.Message),
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
                deferPostCommitEffect(showFailure);
            }
            finally
            {
                progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
            }
        }
        return expandedPaths;
    }

    /// <summary>
    /// Executes only the package physical phase and returns its detached destination projection.
    /// The executor-local durable marker releases source cleanup after physical validation; it does
    /// not represent canonical library durability. The caller must append confirmed facts to one
    /// operation-scoped library mutation session.
    /// </summary>
    /// <param name="package">Detached package source to move.</param>
    /// <param name="installationDirectory">Explicit destination directory.</param>
    /// <param name="options">Library options captured for the owning operation.</param>
    /// <param name="createFolderPath">Auto-naming callback used by the package mover.</param>
    /// <param name="getDisplayedExceptionMessage">Failure text formatter for optional diagnostics.</param>
    /// <param name="fileMutationService">Physical file mutation service.</param>
    /// <param name="dialogService">Dialog service used only when failure dialogs are requested.</param>
    /// <param name="targetOnlyFileMutationOptions">Single-target mutation options.</param>
    /// <param name="recursiveDirectoryTreeFileMutationOptions">Recursive source cleanup options.</param>
    /// <param name="logInstallPerformance">Performance diagnostic sink.</param>
    /// <param name="sourceCleanupPolicy">Source cleanup policy after a validated physical move.</param>
    /// <param name="showMessageBoxOnInstallFail">Whether the package mover queues its failure dialog.</param>
    /// <param name="existingHashes">Operation snapshot plus any caller-owned overlay used for duplicate guarding.</param>
    /// <param name="independentOwnershipLookup">Independent ownership evidence for residual cleanup.</param>
    /// <param name="excludedComponentPaths">Source components excluded from the physical move.</param>
    /// <param name="validatePhysicalResult">Optional validation performed before source cleanup is released.</param>
    /// <param name="enqueueDiagnosticEffect">Post-lease diagnostic-effect sink.</param>
    /// <returns>Detached physical result plus an executor-local receipt.</returns>
    internal PackagePhysicalMoveResult MovePackageFilesPhysicalWithReceipt(
        ChartPackage package,
        string installationDirectory,
        BmsLibraryOptionsSnapshot options,
        Func<IEnumerable<ChartFile>, string, string> createFolderPath,
        Func<Exception, string> getDisplayedExceptionMessage,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        Action<string> logInstallPerformance,
        PackageSourceCleanupPolicy sourceCleanupPolicy,
        bool showMessageBoxOnInstallFail = true,
        IPrimaryHashLookup existingHashes = null,
        IInstalledChartLookupIndex independentOwnershipLookup = null,
        ISet<string> excludedComponentPaths = null,
        Func<PackageInstallExecutionResult, Exception> validatePhysicalResult = null,
        Action<Action> enqueueDiagnosticEffect = null)
    {
        PackageInstallExecutionResult physicalResult = null;
        FileDbMutationReceipt receipt = MovePackageFilesWithReceipt(
            package,
            installationDirectory,
            options,
            createFolderPath,
            getDisplayedExceptionMessage,
            fileMutationService,
            dialogService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions,
            logInstallPerformance,
            installResult =>
            {
                physicalResult = installResult;
                Exception validationFailure = null;
                try
                {
                    validationFailure = validatePhysicalResult?.Invoke(installResult);
                }
                catch (Exception exception)
                {
                    validationFailure = exception;
                }
                // FileDbMutationExecutor needs a durable boundary before it releases source cleanup.
                // For this S4 physical-only route the marker authorizes that physical transition only;
                // canonical library durability is established later by the owning mutation session.
                return validationFailure == null
                    ? FileDbMutationCommitResult.Durable()
                    : FileDbMutationCommitResult.Failed(validationFailure);
            },
            sourceCleanupPolicy,
            showMessageBoxOnInstallFail,
            existingHashes,
            independentOwnershipLookup,
            excludedComponentPaths,
            onPreflightPrepared: installResult => physicalResult ??= installResult,
            enqueueDiagnosticEffect: enqueueDiagnosticEffect);
        return new PackagePhysicalMoveResult(physicalResult, receipt);
    }

    /// <summary>
    /// package install の filesystem 操作を destination-local staging と durable DB receipt の境界で実行します。
    /// source は DB owner が durable receipt を返すまで保持し、cleanup はその後だけ行います。
    /// </summary>
    internal FileDbMutationReceipt MovePackageFilesWithReceipt(
        ChartPackage package,
        string installationDirectory,
        BmsLibraryOptionsSnapshot options,
        Func<IEnumerable<ChartFile>, string, string> createFolderPath,
        Func<Exception, string> getDisplayedExceptionMessage,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        Action<string> logInstallPerformance,
        Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableCommit,
        PackageSourceCleanupPolicy sourceCleanupPolicy,
        bool showMessageBoxOnInstallFail = true,
        IPrimaryHashLookup existingHashes = null,
        IInstalledChartLookupIndex independentOwnershipLookup = null,
        ISet<string> excludedComponentPaths = null,
        Action<PackageInstallExecutionResult> onPreflightPrepared = null,
        Action<Action> enqueueDiagnosticEffect = null)
    {
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        if (fileMutationService == null)
        {
            throw new ArgumentNullException(nameof(fileMutationService));
        }
        if (applyDurableCommit == null)
        {
            throw new ArgumentNullException(nameof(applyDurableCommit));
        }

        string sourcePath = package.path;
        FileDbMutationPlan emptyPlan = new(
            Guid.NewGuid(),
            [],
            string.IsNullOrWhiteSpace(sourcePath) ? [] : [sourcePath],
            Array.Empty<FileDbMutationCleanupPathPlan>(),
            recursiveSourceCleanup: false);
        FileDbMutationReceipt failedReceipt = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new FileNotFoundException(Resources.Error_FileNotFound, sourcePath);
            }

            bool isSingleFile = LongPathFileSystem.FileExists(sourcePath);
            bool isDirectory = LongPathFileSystem.DirectoryExists(sourcePath);
            if (!isSingleFile && !isDirectory)
            {
                throw new FileNotFoundException(Resources.Error_FileNotFound, sourcePath);
            }

            bool isAutoNaming = isDirectory && string.IsNullOrWhiteSpace(installationDirectory);
            List<string> installComponentFiles = isSingleFile
                ? [sourcePath]
                : [.. LongPathFileSystem.EnumerateFileSystemEntries(sourcePath)];
            var installComponentPathSet = new HashSet<string>(installComponentFiles, StringComparer.OrdinalIgnoreCase);
            List<PackageChartEntry> installTargetEntries = SelectInstallTargetEntries(
                package,
                installComponentPathSet,
                sourcePath,
                isSingleFile);
            var skippedByInstalledHash = new List<PackageChartEntry>();
            if (!string.IsNullOrWhiteSpace(installationDirectory))
            {
                IPrimaryHashLookup hashSnapshot = existingHashes ?? EmptyPrimaryHashLookup.Instance;
                skippedByInstalledHash.AddRange(installTargetEntries.Where(entry =>
                {
                    string lookupKey = ChartLookupKey.GetPrimaryHash(entry?.Chart);
                    return !string.IsNullOrWhiteSpace(lookupKey) && hashSnapshot.ContainsPrimaryHash(lookupKey);
                }));
                if (skippedByInstalledHash.Count > 0)
                {
                    var skippedPathSet = new HashSet<string>(
                        skippedByInstalledHash.Select(entry => entry?.Chart?.Path)
                            .Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);
                    installTargetEntries = [.. installTargetEntries.Where(entry =>
                        !string.IsNullOrWhiteSpace(entry?.Chart?.Path)
                        && !skippedPathSet.Contains(entry.Chart.Path))];
                }
            }
            HashSet<string> componentExclusionPaths = new(
                BuildComponentExclusionSet(excludedComponentPaths, installTargetEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (PackageChartEntry skippedEntry in skippedByInstalledHash)
            {
                if (!string.IsNullOrWhiteSpace(skippedEntry?.Chart?.Path))
                {
                    componentExclusionPaths.Add(skippedEntry.Chart.Path);
                }
            }

            string destinationDirectory;
            if (isAutoNaming)
            {
                destinationDirectory = createFolderPath?.Invoke(
                    installTargetEntries.Select(entry => entry.Chart),
                    options?.BMSInstallDir);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    throw new IOException("The package destination could not be determined.");
                }
                string baseDirectory = destinationDirectory;
                int suffix = 1;
                while (LongPathFileSystem.EntryExists(destinationDirectory))
                {
                    suffix++;
                    destinationDirectory = baseDirectory + "(" + suffix + ")";
                }
            }
            else
            {
                destinationDirectory = installationDirectory;
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    throw new ArgumentException("An installation directory is required for an explicit install.", nameof(installationDirectory));
                }
            }

            if (isDirectory)
            {
                EnsureDirectoryMutationDestinationIsDistinct(sourcePath, destinationDirectory);
            }

            // mutation plan と同じコンポーネント／譜面候補を解決し、smart 判定、
            // source cleanup 計画、DB callback の前にパッケージ全体を検証します。
            try
            {
                ValidatePackageDestinationTypes(
                    sourcePath,
                    isAutoNaming,
                    destinationDirectory,
                    installComponentFiles,
                    installTargetEntries,
                    componentExclusionPaths);
            }
            catch (FileDbMutationDestinationTypeConflictException exception)
            {
                // 型衝突をパッケージ拒否へ分類するのは、この外側の read-only 検証だけです。
                // executor 実行中の衝突は、下の通常の補償／エラー receipt 経路に残します。
                return CreatePreflightFailureReceipt(
                    emptyPlan,
                    sourcePath,
                    destinationDirectory,
                    exception);
            }

            var reservedDestinationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mutationPaths = new List<FileDbMutationPathPlan>();
            var chartDestinationPaths = new List<KeyValuePair<string, string>>();
            var sourceCleanupFiles = new List<string>();
            var sourceCleanupDirectories = new List<FileDbMutationCleanupPathPlan>();
            var reservedTemporaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> sourceFilesBeforeMutation = null;
            List<string> sourceCleanupBoundaryDirectories = [sourcePath];
            if (!isAutoNaming
                && (sourceCleanupPolicy == PackageSourceCleanupPolicy.DeleteVerifiedResidualContents
                    || sourceCleanupPolicy == PackageSourceCleanupPolicy.MergeOwnedSourceContents))
            {
                try
                {
                    sourceFilesBeforeMutation = isSingleFile
                        ? [sourcePath]
                        : [.. LongPathFileSystem.EnumerateFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories)];

                    if (package.delete_parent)
                    {
                        string parentPath = Path.GetDirectoryName(sourcePath);
                        bool destinationSharesParentRange = !string.IsNullOrWhiteSpace(parentPath)
                            && (IsSamePath(destinationDirectory, parentPath)
                                || IsPathUnderDirectory(destinationDirectory, parentPath)
                                || IsPathUnderDirectory(parentPath, destinationDirectory));
                        if (!string.IsNullOrWhiteSpace(parentPath) && !destinationSharesParentRange)
                        {
                            sourceFilesBeforeMutation = [.. sourceFilesBeforeMutation
                                .Concat(LongPathFileSystem.EnumerateFiles(parentPath, "*", System.IO.SearchOption.AllDirectories))
                                .Distinct(StringComparer.OrdinalIgnoreCase)];
                            sourceCleanupBoundaryDirectories.Add(parentPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A partial candidate scan cannot authorize deletion of
                    // residual contents.  Keep every extra candidate when any
                    // required source/parent enumeration is incomplete.
                    sourceFilesBeforeMutation = null;
                    logInstallPerformance?.Invoke(
                        "package_source_cleanup preflight_scan_failed path="
                        + sourcePath
                        + " errorType="
                        + ex.GetType().FullName
                        + " error="
                    + ex.Message);
                }
            }

            if (isAutoNaming)
            {
                mutationPaths.Add(CreateMutationPathPlan(
                    sourcePath,
                    destinationDirectory,
                    isDirectory: true,
                    destinationExists: false,
                    reservedTemporaryPaths));
                foreach (PackageChartEntry entry in installTargetEntries)
                {
                    ChartFile chart = entry?.Chart;
                    if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
                    {
                        continue;
                    }
                    string destinationChartPath = BuildDestinationChartPath(sourcePath, destinationDirectory, chart);
                    if (string.IsNullOrWhiteSpace(destinationChartPath))
                    {
                        throw new InvalidOperationException(
                            "The package-install preflight did not produce a destination for chart path: "
                            + chart.Path);
                    }
                    chartDestinationPaths.Add(new KeyValuePair<string, string>(chart.Path, destinationChartPath));
                }
                sourceCleanupDirectories.Add(new FileDbMutationCleanupPathPlan(sourcePath, recursive: true));
            }
            else
            {
                ComponentMovePlanBuildResult componentPlan = BuildComponentMovePlan(
                    installComponentFiles,
                    destinationDirectory,
                    componentExclusionPaths);
                foreach (ComponentMovePlanItem componentItem in componentPlan.PlanItems)
                {
                    if (string.IsNullOrWhiteSpace(componentItem?.SourcePath))
                    {
                        continue;
                    }
                    if (!LongPathFileSystem.FileExists(componentItem.SourcePath))
                    {
                        throw new FileNotFoundException(Resources.Error_FileNotFound, componentItem.SourcePath);
                    }
                    string finalPath = componentItem.DestinationPath;
                    if (options?.EnableSmartComponentOverwrite == true)
                    {
                        bool samePath = IsSamePath(componentItem.SourcePath, finalPath);
                        ComponentMoveDecision decision = samePath
                            ? ComponentMoveDecision.SkipSame
                            : DecideComponentMove(componentItem.SourcePath, finalPath);
                        bool keepByRename = options.KeepSmartOverwriteProtectedFilesByRenaming
                            && LongPathFileSystem.FileExists(finalPath)
                            && IsSmartOverwriteProtectedExtension(componentItem.SourcePath)
                            && decision != ComponentMoveDecision.SkipSame;
                        if (keepByRename)
                        {
                            FileCollisionResolutionResult resolution = libraryFileOperationsService.ResolveFileCollisionWithSuffix(
                                componentItem.SourcePath,
                                finalPath,
                                null,
                                "smart_overwrite",
                                logInstallPerformance);
                            if (resolution.DuplicateMatched)
                            {
                                // The existing suffixed candidate is
                                // authoritative; defer deleting this duplicate
                                // source until after the durable DB receipt.
                                sourceCleanupFiles.Add(componentItem.SourcePath);
                                continue;
                            }
                            finalPath = resolution.FinalPath;
                        }
                        else if (decision == ComponentMoveDecision.SkipSame
                            || decision == ComponentMoveDecision.SkipOlderOrEqual)
                        {
                            // A duplicate at a different path is still a source
                            // component consumed by this install.  Retain it only
                            // for the true same-path no-op; all other smart-skip
                            // source deletes belong to post-durable finalize.
                            if (!samePath)
                            {
                                sourceCleanupFiles.Add(componentItem.SourcePath);
                            }
                            continue;
                        }
                    }
                    sourceCleanupFiles.Add(componentItem.SourcePath);
                    EnsureMutationDestinationIsDistinct(componentItem.SourcePath, finalPath);
                    EnsureUniqueDestinationPath(reservedDestinationPaths, finalPath);
                    mutationPaths.Add(CreateMutationPathPlan(
                        componentItem.SourcePath,
                        finalPath,
                        isDirectory: false,
                        destinationExists: LongPathFileSystem.EntryExists(finalPath),
                        reservedTemporaryPaths));
                }

                foreach (PackageChartEntry entry in installTargetEntries)
                {
                    ChartFile chart = entry?.Chart;
                    string destinationChartPath = ResolveChartDestinationPath(
                        sourcePath,
                        destinationDirectory,
                        chart,
                        reservedDestinationPaths);
                    if (string.IsNullOrWhiteSpace(destinationChartPath))
                    {
                        continue;
                    }
                    if (!LongPathFileSystem.FileExists(chart?.Path))
                    {
                        throw new FileNotFoundException(Resources.Error_FileNotFound, chart?.Path);
                    }
                    sourceCleanupFiles.Add(chart.Path);
                    EnsureMutationDestinationIsDistinct(chart.Path, destinationChartPath);
                    EnsureUniqueDestinationPath(reservedDestinationPaths, destinationChartPath);
                    chartDestinationPaths.Add(new KeyValuePair<string, string>(chart.Path, destinationChartPath));
                    mutationPaths.Add(CreateMutationPathPlan(
                        chart.Path,
                        destinationChartPath,
                        isDirectory: false,
                        destinationExists: false,
                        reservedTemporaryPaths));
                }

                if (isDirectory)
                {
                    foreach (string sourceDirectoryPath in LongPathFileSystem.EnumerateDirectories(
                        sourcePath,
                        "*",
                        System.IO.SearchOption.AllDirectories)
                        .OrderByDescending(path => path.Length))
                    {
                        sourceCleanupDirectories.Add(new FileDbMutationCleanupPathPlan(sourceDirectoryPath, recursive: false));
                    }
                    sourceCleanupDirectories.Add(new FileDbMutationCleanupPathPlan(sourcePath, recursive: false));
                }
                if (package.delete_parent)
                {
                    string parentPath = Path.GetDirectoryName(sourcePath);
                    if (!string.IsNullOrWhiteSpace(parentPath))
                    {
                        bool destinationSharesParentRange = IsSamePath(destinationDirectory, parentPath)
                            || IsPathUnderDirectory(destinationDirectory, parentPath)
                            || IsPathUnderDirectory(parentPath, destinationDirectory);
                        if (!destinationSharesParentRange)
                        {
                            try
                            {
                                foreach (string parentDirectoryPath in LongPathFileSystem.EnumerateDirectories(
                                    parentPath,
                                    "*",
                                    System.IO.SearchOption.AllDirectories)
                                    .OrderByDescending(path => path.Length))
                                {
                                    sourceCleanupDirectories.Add(new FileDbMutationCleanupPathPlan(parentDirectoryPath, recursive: false));
                                }
                            }
                            catch (Exception ex)
                            {
                                logInstallPerformance?.Invoke(
                                    "package_source_cleanup parent_directory_scan_failed path="
                                    + parentPath
                                    + " errorType="
                                    + ex.GetType().FullName
                                    + " error="
                                    + ex.Message);
                            }
                            sourceCleanupDirectories.Add(new FileDbMutationCleanupPathPlan(parentPath, recursive: false));
                        }
                    }
                }

                if (sourceCleanupPolicy == PackageSourceCleanupPolicy.DeleteVerifiedResidualContents
                    || sourceCleanupPolicy == PackageSourceCleanupPolicy.MergeOwnedSourceContents)
                {
                    HashSet<string> plannedDestinationChartPaths = new(
                        chartDestinationPaths
                            .Select(item => item.Key)
                            .Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);
                    IPrimaryHashLookup plannedDestinationHashes = new PrimaryHashSetLookup(
                        installTargetEntries
                            .Where(entry => entry?.Chart != null
                                && plannedDestinationChartPaths.Contains(entry.Chart.Path))
                            .Select(entry => ChartLookupKey.GetPrimaryHash(entry.Chart))
                            .Where(hash => !string.IsNullOrWhiteSpace(hash)));
                    if (TryBuildVerifiedResidualCleanupFiles(
                        sourceFilesBeforeMutation,
                        sourceCleanupFiles,
                        independentOwnershipLookup,
                        plannedDestinationHashes,
                        sourceCleanupBoundaryDirectories,
                        sourceCleanupPolicy,
                        out List<string> verifiedResidualCleanupFiles,
                        out string residualCleanupDecisionReason))
                    {
                        sourceCleanupFiles.AddRange(verifiedResidualCleanupFiles);
                        logInstallPerformance?.Invoke(
                            "package_source_cleanup verified_residuals path="
                            + sourcePath
                            + " count="
                            + verifiedResidualCleanupFiles.Count
                            + " reason="
                            + residualCleanupDecisionReason);
                    }
                    else
                    {
                        logInstallPerformance?.Invoke(
                            "package_source_cleanup residuals_retained path="
                            + sourcePath
                            + " reason="
                            + residualCleanupDecisionReason);
                    }
                }
            }

            FileDbMutationPlan plan = new(
                Guid.NewGuid(),
                mutationPaths,
                sourceCleanupFiles,
                sourceCleanupDirectories,
                recursiveSourceCleanup: false);
            string packageDestinationPath = isSingleFile
                ? chartDestinationPaths
                    .Where(path => string.Equals(path.Key, sourcePath, StringComparison.OrdinalIgnoreCase))
                    .Select(path => path.Value)
                    .SingleOrDefault()
                    ?? mutationPaths
                        .Where(path => string.Equals(path.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        .Select(path => path.DestinationPath)
                        .SingleOrDefault()
                    // A single-file package with no installable chart has no
                    // moved chart destination.  Keep its package path at the
                    // chosen installation directory instead of deriving a
                    // basename after preflight.
                    ?? destinationDirectory
                : destinationDirectory;
            var destinationMap = new PackageInstallDestinationMap(
                sourcePath,
                packageDestinationPath,
                mutationPaths,
                chartDestinationPaths);
            // Durable storage finalization may update a shared chart owner to
            // its destination before live package state is applied.  Preserve
            // each source key now so that finalization cannot make the map
            // lookup derive from an already-promoted path.
            List<(PackageChartEntry Entry, string SourcePath)> liveInstallEntrySnapshots = [];
            foreach (PackageChartEntry entry in installTargetEntries)
            {
                if (entry?.Chart == null)
                {
                    continue;
                }
                liveInstallEntrySnapshots.Add((entry, entry.Chart.Path));
            }
            var executor = new FileDbMutationExecutor(
                plan,
                fileMutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            // Build detached destination projections before entering the executor.
            // A ChartFile projection still points at the live storage owner, so
            // changing its path through PackageChartEntry.ApplyInstalledPath
            // would mutate the source before the durable receipt.  Keep the
            // entries source-oriented and carry destination projections
            // separately; the catalog owner maps storage rows only while its
            // durable callback is executing.
            List<PackageChartEntry> detachedInstallEntries = [.. installTargetEntries
                .Select(entry => entry?.Chart)
                .Where(chart => chart != null)
                .Select(chart => PackageChartEntry.FromChart(chart))
                .Where(entry => entry != null)];
            foreach (PackageChartEntry detachedEntry in detachedInstallEntries)
            {
                detachedEntry.ClearPostInstallState();
            }
            List<ChartFile> detachedInstalledCharts = [.. detachedInstallEntries
                .Select(entry => entry?.Chart)
                .Where(chart => chart != null)
                .Select(chart => CreateInstalledChartProjection(
                    chart,
                    destinationMap.GetRequiredDestinationPath(chart.Path)))];
            PackageInstallExecutionResult detachedPackageResult =
                CreatePackageInstallExecutionResult(detachedInstallEntries, detachedInstalledCharts);
            detachedPackageResult.InstallPathToDelete = sourcePath;
            onPreflightPrepared?.Invoke(detachedPackageResult);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                FileDbMutationCommitResult databaseResult = applyDurableCommit(detachedPackageResult);
                if (!databaseResult.DurableCommit)
                {
                    return databaseResult;
                }

                return FileDbMutationCommitResult.Durable(
                    () =>
                    {
                        if (databaseResult.Failure == null)
                        {
                            databaseResult.DurableFinalizer?.Invoke();
                            ApplyLivePackageInstallState(
                                package,
                                destinationMap,
                                liveInstallEntrySnapshots);
                        }
                    },
                    databaseResult.Failure);
            });
            if ((!receipt.DurableCommit
                || receipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                && showMessageBoxOnInstallFail)
            {
                Action showFailure = () => ShowPackageMutationFailure(
                    package,
                    destinationDirectory,
                    receipt.Failure,
                    dialogService,
                    getDisplayedExceptionMessage);
                enqueueDiagnosticEffect?.Invoke(showFailure);
            }
            return receipt;
        }
        catch (Exception exception)
        {
            failedReceipt = new FileDbMutationReceipt(
                emptyPlan.OperationId,
                FileDbMutationTerminalState.Failed,
                durableCommit: false,
                compensationAttemptCount: 0,
                cleanupAttemptCount: 0,
                [sourcePath],
                [],
                [],
                [],
                [],
                exception);
            if (showMessageBoxOnInstallFail)
            {
                Action showFailure = () => ShowPackageMutationFailure(
                    package,
                    string.Empty,
                    exception,
                    dialogService,
                    getDisplayedExceptionMessage);
                enqueueDiagnosticEffect?.Invoke(showFailure);
            }
            return failedReceipt;
        }
    }

    private static void ApplyLivePackageInstallState(
        ChartPackage package,
        PackageInstallDestinationMap destinationMap,
        IEnumerable<(PackageChartEntry Entry, string SourcePath)> installTargetEntrySnapshots)
    {
        if (package == null || destinationMap == null)
        {
            return;
        }
        List<(PackageChartEntry Entry, string SourcePath)> entries = [.. (installTargetEntrySnapshots ?? [])
            .Where(snapshot => snapshot.Entry?.Chart != null)];
        foreach ((PackageChartEntry Entry, string SourcePath) entry in entries)
        {
            entry.Entry.ApplyInstalledPath(destinationMap.GetRequiredDestinationPath(entry.SourcePath));
        }
        package.path = destinationMap.PackageDestinationPath;
        foreach ((PackageChartEntry Entry, string SourcePath) entry in entries)
        {
            entry.Entry.ClearPostInstallState();
        }
        package.ReplaceChartEntries(entries.Select(entry => entry.Entry));
    }

    private static PackageInstallExecutionResult CreatePackageInstallExecutionResult(ChartPackage package)
    {
        return CreatePackageInstallExecutionResult(package?.ChartEntries);
    }

    private static PackageInstallExecutionResult CreatePackageInstallExecutionResult(
        IEnumerable<PackageChartEntry> packageEntries)
    {
        return CreatePackageInstallExecutionResult(packageEntries, null);
    }

    private static PackageInstallExecutionResult CreatePackageInstallExecutionResult(
        IEnumerable<PackageChartEntry> packageEntries,
        IEnumerable<ChartFile> addedCharts)
    {
        var result = new PackageInstallExecutionResult();
        List<PackageChartEntry> entries = [.. packageEntries ?? []];
        result.AddedEntries.AddRange(entries);
        foreach (PackageChartEntry entry in entries)
        {
            entry?.ClearPostInstallState();
        }
        result.AddedCharts.AddRange(
            addedCharts?.Where(chart => chart != null)
            ?? entries.Select(entry => entry?.Chart).Where(chart => chart != null));
        return result;
    }

    private static ChartFile CreateInstalledChartProjection(ChartFile source, string installedPath)
    {
        if (source == null || string.IsNullOrWhiteSpace(installedPath))
        {
            return source;
        }

        return new ChartFile(
            source.Kind,
            installedPath,
            source.Md5,
            source.Sha256,
            source.Title,
            source.RawTitle,
            source.Artist,
            source.Genre,
            source.Folder,
            source.Tag,
            source.LevelText,
            source.Level,
            source.Mode,
            source.ChartInfo,
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            source.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            source.InstallDestinationSuggestions,
            source.Warnings,
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName,
            source.Score,
            source.Status,
            source.ResourceHealthWarningsIgnored,
            source.ResourceHealthMaintenanceSnapshot);
    }

    private static bool TryBuildVerifiedResidualCleanupFiles(
        IEnumerable<string> sourceFilesBeforeMutation,
        IEnumerable<string> plannedSourceCleanupFiles,
        IInstalledChartLookupIndex independentlyOwnedLookup,
        IPrimaryHashLookup plannedDestinationHashes,
        IEnumerable<string> cleanupBoundaryDirectories,
        PackageSourceCleanupPolicy sourceCleanupPolicy,
        out List<string> verifiedResidualCleanupFiles,
        out string reason)
    {
        verifiedResidualCleanupFiles = [];
        if (sourceFilesBeforeMutation == null)
        {
            reason = "source_scan_unavailable";
            return false;
        }

        HashSet<string> plannedCleanupPaths = new(
            (plannedSourceCleanupFiles ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        List<string> residualFiles = [.. (sourceFilesBeforeMutation ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && !plannedCleanupPaths.Contains(path))];
        if (residualFiles.Count == 0)
        {
            reason = "safe_cleanup_allowed no_residual_candidates";
            return true;
        }

        foreach (string residualFilePath in residualFiles)
        {
            if (!IsSupportedChartFilePath(residualFilePath))
            {
                reason = "residual_non_chart_file path=" + residualFilePath;
                verifiedResidualCleanupFiles.Clear();
                return false;
            }

            if (!TryGetRemainingChartLookupKey(residualFilePath, out string lookupKey, out reason))
            {
                verifiedResidualCleanupFiles.Clear();
                return false;
            }

            bool hasPlannedDestinationCopy = plannedDestinationHashes?.ContainsPrimaryHash(lookupKey) == true;
            IReadOnlyList<string> independentDirectories = independentlyOwnedLookup?.GetDistinctDirectoriesByPrimaryHash(lookupKey) ?? [];
            bool hasOwnedContentInsideCleanupBoundary = independentDirectories
                .Any(directory => IsPathWithinAnyCleanupBoundary(directory, cleanupBoundaryDirectories));
            bool hasIndependentInstalledCopy = independentDirectories
                .Any(directory => !IsPathWithinAnyCleanupBoundary(directory, cleanupBoundaryDirectories));
            if (hasOwnedContentInsideCleanupBoundary
                && sourceCleanupPolicy != PackageSourceCleanupPolicy.MergeOwnedSourceContents)
            {
                reason = "residual_owned_chart_inside_cleanup_boundary path="
                    + residualFilePath
                    + " hash="
                    + lookupKey
                    + " directories="
                    + string.Join("|", independentDirectories);
                verifiedResidualCleanupFiles.Clear();
                return false;
            }
            if (!hasPlannedDestinationCopy && !hasIndependentInstalledCopy)
            {
                reason = "residual_chart_not_independently_owned path="
                    + residualFilePath
                    + " hash="
                    + lookupKey
                    + " independentDirectories="
                    + string.Join("|", independentDirectories);
                verifiedResidualCleanupFiles.Clear();
                return false;
            }
            verifiedResidualCleanupFiles.Add(residualFilePath);
        }

        reason = "safe_cleanup_allowed residual_files_all_independently_owned count="
            + verifiedResidualCleanupFiles.Count;
        return true;
    }

    /// <summary>
    /// 同じ導入計画で先に durable 化された destination を、後続 package の
    /// 残存候補判定へ追加する読み取り lookup です。未実行の予約は追加せず、
    /// executor が返した確定結果の chart path だけを登録します。
    /// </summary>
    private sealed class PlannedDestinationOwnershipLookup : IInstalledChartLookupIndex
    {
        private readonly IInstalledChartLookupIndex baseline;
        private readonly Dictionary<string, HashSet<string>> plannedDirectoriesByHash =
            new(StringComparer.OrdinalIgnoreCase);

        internal PlannedDestinationOwnershipLookup(IInstalledChartLookupIndex baseline)
        {
            this.baseline = baseline;
        }

        public int HashCount => (baseline?.HashCount ?? 0) + plannedDirectoriesByHash.Count;

        public int DistinctPrimaryHashCount => (baseline?.DistinctPrimaryHashCount ?? 0)
            + plannedDirectoriesByHash.Keys.Count(hash => baseline?.ContainsPrimaryHash(hash) != true);

        public bool ContainsPrimaryHash(string lookupHash)
        {
            return baseline?.ContainsPrimaryHash(lookupHash) == true
                || (!string.IsNullOrWhiteSpace(lookupHash)
                    && plannedDirectoriesByHash.ContainsKey(lookupHash));
        }

        public int GetPrimaryHashCount(string lookupHash)
        {
            return (baseline?.GetPrimaryHashCount(lookupHash) ?? 0)
                + (plannedDirectoriesByHash.TryGetValue(lookupHash, out HashSet<string> directories)
                    ? directories.Count
                    : 0);
        }

        public IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
        {
            HashSet<string> directories = new(
                baseline?.GetDistinctDirectoriesByPrimaryHash(lookupHash) ?? [],
                StringComparer.OrdinalIgnoreCase);
            if (plannedDirectoriesByHash.TryGetValue(lookupHash, out HashSet<string> plannedDirectories))
            {
                directories.UnionWith(plannedDirectories);
            }
            return [.. directories];
        }

        public int GetUniquePrimaryHashCountByDirectory(string directoryPath)
        {
            int baselineCount = baseline?.GetUniquePrimaryHashCountByDirectory(directoryPath) ?? 0;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return baselineCount;
            }
            string normalizedDirectory = Path.GetFullPath(directoryPath);
            int plannedCount = plannedDirectoriesByHash.Count(item => item.Value.Contains(normalizedDirectory));
            return baselineCount + plannedCount;
        }

        internal void AddCommittedEntries(IEnumerable<PackageChartEntry> entries)
        {
            foreach (PackageChartEntry entry in entries ?? [])
            {
                ChartFile chart = entry?.Chart;
                string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
                string chartDirectory = string.IsNullOrWhiteSpace(chart?.Path)
                    ? null
                    : Path.GetDirectoryName(chart.Path);
                if (string.IsNullOrWhiteSpace(lookupHash) || string.IsNullOrWhiteSpace(chartDirectory))
                {
                    continue;
                }
                string normalizedDirectory = Path.GetFullPath(chartDirectory);
                if (!plannedDirectoriesByHash.TryGetValue(lookupHash, out HashSet<string> directories))
                {
                    directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    plannedDirectoriesByHash[lookupHash] = directories;
                }
                directories.Add(normalizedDirectory);
            }
        }
    }

    private static bool IsPathWithinAnyCleanupBoundary(
        string path,
        IEnumerable<string> cleanupBoundaryDirectories)
    {
        return (cleanupBoundaryDirectories ?? [])
            .Where(boundary => !string.IsNullOrWhiteSpace(boundary))
            .Any(boundary => IsSameOrContainedPath(path, boundary));
    }

    private static bool IsSameOrContainedPath(string path, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }
        try
        {
            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(
                    normalizedDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(
                    normalizedDirectory + Path.AltDirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return IsPathUnderDirectory(path, directoryPath)
                || string.Equals(path, directoryPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void EnsureDirectoryMutationDestinationIsDistinct(
        string sourceDirectory,
        string destinationDirectory)
    {
        if (IsSameOrContainedPath(sourceDirectory, destinationDirectory)
            || IsSameOrContainedPath(destinationDirectory, sourceDirectory))
        {
            throw new IOException("A directory source and destination must not overlap.");
        }
    }

    private static FileDbMutationPathPlan CreateMutationPathPlan(
        string sourcePath,
        string destinationPath,
        bool isDirectory,
        bool destinationExists,
        ISet<string> reservedTemporaryPaths)
    {
        string stagingPath = CreateReservedSiblingPath(destinationPath, "stage", reservedTemporaryPaths);
        string backupPath = destinationExists
            ? CreateReservedSiblingPath(destinationPath, "backup", reservedTemporaryPaths)
            : string.Empty;
        return new FileDbMutationPathPlan(
            sourcePath,
            destinationPath,
            stagingPath,
            backupPath,
            isDirectory);
    }

    private static string CreateReservedSiblingPath(
        string destinationPath,
        string purpose,
        ISet<string> reservedPaths)
    {
        string candidatePath;
        do
        {
            candidatePath = LongPathFileSystem.CreateMutationSiblingPath(destinationPath, purpose);
        }
        while (!reservedPaths.Add(candidatePath));
        return candidatePath;
    }

    private static void EnsureMutationDestinationIsDistinct(string sourcePath, string destinationPath)
    {
        if (string.Equals(
            LongPathFileSystem.NormalizePathForStorage(sourcePath),
            LongPathFileSystem.NormalizePathForStorage(destinationPath),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Source path and destination path are the same.");
        }
    }

    private static void EnsureUniqueDestinationPath(ISet<string> reservedPaths, string destinationPath)
    {
        if (!reservedPaths.Add(destinationPath))
        {
            throw new IOException("The immutable mutation plan contains duplicate destination paths.");
        }
    }

    private static void ShowPackageMutationFailure(
        ChartPackage package,
        string destinationDirectory,
        Exception exception,
        IBmsLibraryDialogService dialogService,
        Func<Exception, string> getDisplayedExceptionMessage)
    {
        dialogService?.Show(
            string.Format(
                Resources.Error_InstallFailed,
                package?.path,
                destinationDirectory,
                getDisplayedExceptionMessage?.Invoke(exception) ?? exception?.Message ?? "Unknown mutation failure."),
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    private static bool TryGetRemainingChartLookupKey(string remainingFilePath, out string lookupKey, out string reason)
    {
        lookupKey = null;
        reason = null;
        try
        {
            lookupKey = ChartFileKindResolver.IsBmsonFilePath(remainingFilePath)
                ? ChartLookupKey.GetPrimaryHash(ChartFileProjection.FromBmsonSong(
                    BmsonSongParser.Parse(remainingFilePath),
                    includeWarningSnapshot: false,
                    includeResourceReferences: false))
                : ChartFileContentReader.ReadSnapshot(remainingFilePath).Md5;
        }
        catch (Exception ex)
        {
            reason = "remaining_chart_load_failed path=" + remainingFilePath + " errorType=" + ex.GetType().FullName + " error=" + ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            reason = "remaining_chart_hash_unavailable path=" + remainingFilePath;
            return false;
        }
        return true;
    }

    private static bool IsSupportedChartFilePath(string filePath)
    {
        return ChartFileKindResolver.IsSupportedChartFilePath(filePath);
    }

    private static void ClearPackageInstallDestinations(ChartPackage chartPackage)
    {
        foreach (PackageChartEntry entry in chartPackage?.ChartEntries ?? [])
        {
            entry?.ClearInstallDestination();
        }
    }

    /// <summary>
    /// Discovers detached install candidates, excluding registered BMS roots and
    /// their descendants before either automatic installation or pending publication.
    /// The roots describe configured ownership, not directories found in the chart index.
    /// </summary>
    public AutoInstallWorkflowResult PrepareAutoInstallWorkflow(
        IEnumerable<string> installPaths,
        IEnumerable<ChartPackage> currentPendingPackages,
        IEnumerable<string> registeredBmsRoots,
        Func<ChartFile, bool> isInstalledChart,
        double dupRateThreshInOnePkg,
        IPrimaryHashLookup installedChartLookup = null,
        CancellationToken token = default)
    {
        var result = new AutoInstallWorkflowResult();
        var totalStopwatch = Stopwatch.StartNew();
        var discoveryStopwatch = Stopwatch.StartNew();
        List<string> normalizedInstallPaths = [.. (installPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && LongPathFileSystem.EntryExists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (token.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }
        IEnumerable<IGrouping<string, string>> groupedPaths = from file in normalizedInstallPaths
                                                              group file by Path.GetDirectoryName(file)?.ToUpperInvariant();
        List<ChartPackage> discoveredPackages = [];
        foreach (IGrouping<string, string> paths in groupedPaths)
        {
            if (string.IsNullOrWhiteSpace(paths.Key) || !LongPathFileSystem.DirectoryExists(paths.Key))
            {
                continue;
            }
            List<string> files = [.. paths.Where(LongPathFileSystem.FileExists)];
            List<string> directories = [.. paths.Where(LongPathFileSystem.DirectoryExists)];
            List<string> chartFiles = [.. files.Where(file => ChartFileKindResolver.IsSupportedChartFilePath(file) && LongPathFileSystem.FileExists(file))];
            string[] topEntries = [.. LongPathFileSystem.EnumerateFileSystemEntries(paths.Key, "*", System.IO.SearchOption.TopDirectoryOnly)];
            if (files.Count + directories.Count == topEntries.Length)
            {
                ChartPackageDiscoveryResult discoveryResult = SearchChartPackagesRecursivelyWithMetadata(paths.Key, dupRateThreshInOnePkg);
                discoveredPackages.AddRange(discoveryResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(discoveryResult.RegroupEligibleSourceDirectories);
                continue;
            }
            List<PackageChartEntry> parsedChartEntries = [.. chartFiles
                .Select(CreatePackageChartEntryForDiscovery)
                .Where(entry => entry != null)];
            bool anyChartHasExistingResources = parsedChartEntries.Any(HasExistingPackageChartResources);
            if (chartFiles.Count > 0 && anyChartHasExistingResources)
            {
                ChartPackageDiscoveryResult discoveryResult = SearchChartPackagesRecursivelyWithMetadata(paths.Key, dupRateThreshInOnePkg);
                discoveredPackages.AddRange(discoveryResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(discoveryResult.RegroupEligibleSourceDirectories);
                continue;
            }
            var entryByPath = parsedChartEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Chart?.Path))
                .ToDictionary(entry => entry.Chart.Path, StringComparer.OrdinalIgnoreCase);
            List<ChartPackage> singleFilePackages = [.. chartFiles.Select(filePath => CreatePackageWithKnownCharts(filePath, deleteParent: false, entryByPath.TryGetValue(filePath, out PackageChartEntry entry) ? new[] { entry } : null))];
            List<ChartPackage> directoryPackages = [];
            foreach (string dir in directories)
            {
                ChartPackageDiscoveryResult discoveryResult = SearchChartPackagesRecursivelyWithMetadata(dir, dupRateThreshInOnePkg);
                directoryPackages.AddRange(discoveryResult.Packages);
                result.RegroupEligibleSourceDirectories.AddRange(discoveryResult.RegroupEligibleSourceDirectories);
            }
            discoveredPackages.AddRange(singleFilePackages.Concat(directoryPackages));
        }
        discoveryStopwatch.Stop();
        result.DiscoveryMs = discoveryStopwatch.ElapsedMilliseconds;

        List<string> libraryDirectories = [.. (registeredBmsRoots ?? []).Where(dir => !string.IsNullOrWhiteSpace(dir))];
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        discoveredPackages = [.. discoveredPackages
            .Where(pkg => pkg != null && !libraryDirectories.Any(dir =>
                LongPathFileSystem.IsSameOrDescendantDirectoryPath(pkg.path, dir)))
            .Where(newPkg => !pendingPackages.Any(oldPkg => !string.IsNullOrWhiteSpace(oldPkg.path) && newPkg.path.Equals(oldPkg.path, StringComparison.OrdinalIgnoreCase)))
            .Where(newPkg => !pendingPackages.Any(oldPkg => !string.IsNullOrWhiteSpace(oldPkg.path) && LongPathFileSystem.DirectoryExists(oldPkg.path) && newPkg.path.StartsWith(oldPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))];
        List<string> distinctWorkflowRegroupEligibleSourceDirectories = [.. result.RegroupEligibleSourceDirectories
            .Where(sourceDirectoryPath => !string.IsNullOrWhiteSpace(sourceDirectoryPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        result.RegroupEligibleSourceDirectories.Clear();
        result.RegroupEligibleSourceDirectories.AddRange(distinctWorkflowRegroupEligibleSourceDirectories);
        result.DiscoveredPackages.AddRange(discoveredPackages);
        if (discoveredPackages.Count == 0)
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }

        if (discoveredPackages.Any(newPkg => !string.IsNullOrWhiteSpace(newPkg.path) && LongPathFileSystem.DirectoryExists(newPkg.path)))
        {
            List<ChartPackage> pendingPackagesToRemove = [.. pendingPackages.Where(delegate (ChartPackage oldPkg)
            {
                IEnumerable<ChartPackage> sourcePackages = discoveredPackages.Where(newPkg => !string.IsNullOrWhiteSpace(newPkg.path) && LongPathFileSystem.DirectoryExists(newPkg.path));
                string dir;
                if (LongPathFileSystem.DirectoryExists(oldPkg.path))
                {
                    dir = oldPkg.path + Path.DirectorySeparatorChar;
                }
                else
                {
                    if (!LongPathFileSystem.FileExists(oldPkg.path))
                    {
                        return true;
                    }
                    dir = oldPkg.path;
                }
                return sourcePackages.Any(newPkg => dir.StartsWith(newPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            })];
            result.PendingPackagesToRemove.AddRange(pendingPackagesToRemove);
        }

        var classificationStopwatch = Stopwatch.StartNew();
        var installedCheckStopwatch = Stopwatch.StartNew();
        Dictionary<ChartPackage, bool> pendingByPackage = [];
        IPrimaryHashLookup installedHashes = installedChartLookup ?? EmptyPrimaryHashLookup.Instance;
        foreach (ChartPackage pkg in discoveredPackages)
        {
            bool hasInstalledChart = false;
            foreach (PackageChartEntry entry in pkg.ChartEntries)
            {
                ChartFile chart = entry?.Chart;
                string lookupKey = ChartLookupKey.GetPrimaryHash(chart);
                if ((!string.IsNullOrWhiteSpace(lookupKey) && installedHashes.ContainsPrimaryHash(lookupKey))
                    || (isInstalledChart != null && isInstalledChart(chart)))
                {
                    ApplyAlreadyInstalledWarning([entry]);
                    hasInstalledChart = true;
                }
            }
            pendingByPackage[pkg] = hasInstalledChart;
        }
        installedCheckStopwatch.Stop();
        result.InstalledCheckMs = installedCheckStopwatch.ElapsedMilliseconds;

        var warningClassificationStopwatch = Stopwatch.StartNew();
        foreach (ChartPackage pkg in discoveredPackages)
        {
            bool isSingleFilePackage = !LongPathFileSystem.DirectoryExists(pkg.path);
            foreach (PackageChartEntry entry in pkg.ChartEntries)
            {
                if (isSingleFilePackage && !HasAlreadyInstalledWarning(entry))
                {
                    bool isBmson = entry.Chart?.Kind == ChartFileKind.Bmson;
                    entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                    entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
                    pendingByPackage[pkg] = true;
                }
            }
            if (ApplyPendingResourceHealthProjectionToEntries(pkg.ChartEntries) > 0)
            {
                pendingByPackage[pkg] = true;
            }
        }
        warningClassificationStopwatch.Stop();
        result.WarningClassificationMs = warningClassificationStopwatch.ElapsedMilliseconds;
        foreach (ChartPackage discoveredPackage in discoveredPackages)
        {
            bool isPending = pendingByPackage.TryGetValue(discoveredPackage, out bool pendingValue) && pendingValue;
            if (ApplyNestedChartFileWarnings(discoveredPackage))
            {
                isPending = true;
            }

            if (isPending)
            {
                result.PendingPackagesToAdd.Add(discoveredPackage);
            }
            else
            {
                result.AutoInstallCandidates.Add(discoveredPackage);
            }
        }
        classificationStopwatch.Stop();
        result.ClassificationMs = classificationStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool HasAlreadyInstalledWarning(PackageChartEntry entry)
    {
        return (entry?.Chart?.Warnings ?? []).Any(warning => warning?.Kind == ChartWarningKind.AlreadyInstalled);
    }

    public AutoInstallApplyResult ApplyAutoInstallWorkflow(
        AutoInstallWorkflowResult workflow,
        bool keepInstallablePackagesPending,
        bool canAutoInstallImmediately,
        Func<IEnumerable<ChartPackage>, List<ChartPackage>> installPackages,
        CancellationToken token = default)
    {
        return ApplyAutoInstallWorkflowCore(
            workflow,
            keepInstallablePackagesPending,
            canAutoInstallImmediately,
            packages => new AutoInstallCandidateApplyResult(
                installPackages?.Invoke(packages),
                mutationReceipt: null),
            token);
    }

    /// <summary>
    /// Applies auto-install candidates through the durable filesystem/DB
    /// receipt route.  A manual-recovery receipt stops candidate classification
    /// so an unattempted candidate is never reported as installed.
    /// </summary>
    internal AutoInstallApplyResult ApplyAutoInstallWorkflowWithFileMutationReceipts(
        AutoInstallWorkflowResult workflow,
        bool keepInstallablePackagesPending,
        bool canAutoInstallImmediately,
        Func<IEnumerable<ChartPackage>, AutoInstallCandidateApplyResult> installPackages,
        CancellationToken token = default)
    {
        return ApplyAutoInstallWorkflowCore(
            workflow,
            keepInstallablePackagesPending,
            canAutoInstallImmediately,
            installPackages,
            token);
    }

    private AutoInstallApplyResult ApplyAutoInstallWorkflowCore(
        AutoInstallWorkflowResult workflow,
        bool keepInstallablePackagesPending,
        bool canAutoInstallImmediately,
        Func<IEnumerable<ChartPackage>, AutoInstallCandidateApplyResult> installPackages,
        CancellationToken token)
    {
        var result = new AutoInstallApplyResult();
        if (workflow == null)
        {
            return result;
        }
        var totalStopwatch = Stopwatch.StartNew();
        var installStopwatch = Stopwatch.StartNew();
        if (workflow.PendingPackagesToRemove.Count > 0)
        {
            result.PendingPackagesToRemove.AddRange(workflow.PendingPackagesToRemove.Where(pkg => pkg != null));
            result.InstallRowsToDelete.AddRange(
                workflow.PendingPackagesToRemove
                    .Where(pkg => pkg != null && !string.IsNullOrWhiteSpace(pkg.path))
                    .Select(pkg => pkg.path)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        List<ChartPackage> pendingPackagesToAdd = [.. workflow.PendingPackagesToAdd.Where(pkg => pkg != null)];
        if (workflow.AutoInstallCandidates.Count > 0)
        {
            if (token.IsCancellationRequested)
            {
                installStopwatch.Stop();
                result.InstallMs = installStopwatch.ElapsedMilliseconds;
                totalStopwatch.Stop();
                result.TotalMs = totalStopwatch.ElapsedMilliseconds;
                return result;
            }
            if (!keepInstallablePackagesPending && canAutoInstallImmediately)
            {
                AutoInstallCandidateBatchClassification autoInstallClassification = ClassifyAutoInstallCandidateBatch(workflow.AutoInstallCandidates);
                AutoInstallCandidateApplyResult candidateApplyResult = installPackages?.Invoke(autoInstallClassification.InstallCandidates)
                    ?? new AutoInstallCandidateApplyResult([], null);
                result.MutationReceipt = candidateApplyResult.MutationReceipt;
                List<ChartPackage> failedPackages = [.. candidateApplyResult.FailedPackages];
                var failedSet = new HashSet<ChartPackage>(failedPackages);
                result.AutoInstallFailures.AddRange(failedPackages.Where(pkg => pkg != null));
                List<ChartPackage> succeededPackages = [.. autoInstallClassification.InstallCandidates.Where(pkg => pkg != null && !failedSet.Contains(pkg))];
                result.AutoInstalledPackages.AddRange(succeededPackages);
                IPrimaryHashLookup succeededHashes = CreatePackagePrimaryHashLookup(succeededPackages);
                foreach (AutoInstallDuplicateCandidate duplicateCandidate in autoInstallClassification.DuplicateCandidates)
                {
                    ApplyAlreadyInstalledWarning(GetEntriesMatchedByPrimaryHashes(
                        duplicateCandidate.Package?.ChartEntries,
                        succeededHashes,
                        duplicateCandidate.DuplicatePrimaryHashes));
                }
                pendingPackagesToAdd = [.. pendingPackagesToAdd, .. failedPackages, .. autoInstallClassification.DuplicateCandidates.Select(candidate => candidate.Package).Where(pkg => pkg != null)];
            }
            else
            {
                pendingPackagesToAdd = [.. pendingPackagesToAdd, .. workflow.AutoInstallCandidates.Where(pkg => pkg != null)];
            }
        }
        installStopwatch.Stop();
        result.InstallMs = installStopwatch.ElapsedMilliseconds;

        var applyStopwatch = Stopwatch.StartNew();
        result.PendingPackagesToAdd.AddRange(pendingPackagesToAdd);
        result.InstallRowsToUpsert.AddRange(pendingPackagesToAdd.Where(pkg => !string.IsNullOrWhiteSpace(pkg.path)));
        result.EstimateTargets.AddRange(pendingPackagesToAdd);
        applyStopwatch.Stop();
        result.ApplyMs = applyStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    public PendingPackageMutationDelta BuildPendingPackageMutationDelta(IEnumerable<ChartPackage> pendingPackages, IEnumerable<ChartPackage> packagesToRemove = null, IEnumerable<string> chartPathsToRemove = null, bool clearAll = false)
    {
        List<ChartPackage> currentPending = [.. (pendingPackages ?? []).Where(pkg => pkg != null)];
        var delta = new PendingPackageMutationDelta();
        if (clearAll)
        {
            delta.HasChanges = currentPending.Count > 0;
            delta.InstallPathsToDelete = [.. currentPending.Where(pkg => !string.IsNullOrWhiteSpace(pkg.path)).Select(pkg => pkg.path).Distinct(StringComparer.Ordinal)];
            return delta;
        }
        var removedPackages = new HashSet<ChartPackage>((packagesToRemove ?? []).Where(pkg => pkg != null));
        var removedPackagePaths = new HashSet<string>((packagesToRemove ?? []).Where(pkg => pkg != null && !string.IsNullOrWhiteSpace(pkg.path)).Select(pkg => pkg.path), StringComparer.OrdinalIgnoreCase);
        var removedPaths = new HashSet<string>((chartPathsToRemove ?? []).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        bool removeFiles = removedPaths.Count > 0;
        var installPathsToDelete = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChartPackage package in currentPending)
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
            List<PackageChartEntry> packageEntries = package.ChartEntries;
            if (packageEntries.Count == 0)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            List<PackageChartEntry> remainingEntries = [.. packageEntries.Where(entry => !IsMatchedRemovedEntry(entry, removedPaths))];
            if (remainingEntries.Count == packageEntries.Count)
            {
                delta.RemainingPackages.Add(package);
                continue;
            }
            delta.HasChanges = true;
            if (remainingEntries.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(package.path))
                {
                    installPathsToDelete.Add(package.path);
                }
                continue;
            }
            delta.EntryMutations.Add(new PendingPackageEntryMutation(package, remainingEntries));
            delta.RemainingPackages.Add(package);
        }
        delta.InstallPathsToDelete = [.. installPathsToDelete];
        return delta;
    }

    /// <summary>
    /// 推定導入で実行可能な候補を、副作用なしで構築します。
    /// 現在の保留照合、入力順重複排除、全 DST 空候補の除外は短い
    /// model guard の保持中に行い、package 分類と component 列挙は
    /// package ごとの実行段階まで遅延します。
    /// </summary>
    /// <param name="requestedPackages">ユーザーが指定した pending package の順序付き一覧です。</param>
    /// <param name="currentPendingPackages">現在 DB/メモリに保持している pending package の snapshot です。</param>
    /// <param name="installedChartLookup">開始時に所持している譜面 hash の lookup です。</param>
    public PendingInstallBatchPlan BuildEstimatedInstallBatchPlan(
        IEnumerable<ChartPackage> requestedPackages,
        IEnumerable<ChartPackage> currentPendingPackages,
        IPrimaryHashLookup installedChartLookup)
    {
        var planStopwatch = Stopwatch.StartNew();
        var plan = new PendingInstallBatchPlan();
        var filterStopwatch = Stopwatch.StartNew();
        List<ChartPackage> pendingSnapshot = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        var selectedReferences = new HashSet<ChartPackage>();
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage requestedPackage in DeduplicatePackagesByPathOrReference(requestedPackages))
        {
            ChartPackage pendingPackage = pendingSnapshot.FirstOrDefault(package =>
                ReferenceEquals(package, requestedPackage)
                || (!string.IsNullOrWhiteSpace(package.path)
                    && !string.IsNullOrWhiteSpace(requestedPackage.path)
                    && string.Equals(package.path, requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null
                || (!string.IsNullOrWhiteSpace(pendingPackage.path)
                    ? !selectedPaths.Add(pendingPackage.path)
                    : !selectedReferences.Add(pendingPackage)))
            {
                continue;
            }

            List<PackageChartEntry> validEntries = [.. (pendingPackage.ChartEntries ?? [])
                .Where(entry => entry?.Chart != null)];
            if (validEntries.Count == 0
                || validEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)))
            {
                if (pendingPackage.DeferredEstimateReason != PendingEstimateDeferredReason.None)
                {
                    plan.DeferredManualHoldCount++;
                }
                continue;
            }
            plan.SelectedPendingPackages.Add(pendingPackage);
        }
        filterStopwatch.Stop();
        plan.FilterMs = filterStopwatch.ElapsedMilliseconds;
        plan.SelectedPendingCount = plan.SelectedPendingPackages.Count;
        plan.IndependentOwnershipLookup = new PlannedDestinationOwnershipLookup(
            installedChartLookup as IInstalledChartLookupIndex);
        plan.MoveGuardLookup = new PrimaryHashGuardLookup(
            installedChartLookup ?? EmptyPrimaryHashLookup.Instance);
        planStopwatch.Stop();
        plan.PlanBuildMs = planStopwatch.ElapsedMilliseconds;
        return plan;
    }

    /// <summary>
    /// 保留 package を入力順に一件ずつ分類、実行、確定します。
    /// callback は現在の package work item を受け、既存の filesystem/DB
    /// 実行結果を返します。durable receipt が確定した候補は保留除去と
    /// DST クリアを行い、durable 前の失敗が補償済みなら次候補を評価します。
    /// manual recovery または durable finalization failure では後続を停止します。
    /// </summary>
    /// <param name="plan">副作用なしに確定した候補 plan です。</param>
    /// <param name="deletePendingPackageSourceAfterInstall">成功した候補 source の削除を許可する設定です。</param>
    /// <param name="installPackage">一件分の work item を既存 executor へ渡す callback です。</param>
    /// <param name="createInstalledDisplayPackage">resource-only 成功時の installed 表示 package を作成する callback です。</param>
    /// <param name="cleanupPendingPackageSource">receipt 非対応 caller 用の source cleanup callback です。</param>
    /// <param name="logInfo">診断ログ callback です。</param>
    /// <param name="cleanupPendingPackageSourceWithReceipt">durable receipt を返す source cleanup callback です。</param>
    /// <param name="mutationReceiptObserver">確定した mutation receipt の観測 callback です。</param>
    /// <param name="manualRecoveryObserved">既存 executor の terminal failure を観測する callback です。</param>
    /// <param name="countComponentMoveTargets">resource-only 候補の component 移動対象数を数える callback です。</param>
    public PendingInstallBatchResult ExecuteEstimatedInstallBatchPlan(
        PendingInstallBatchPlan plan,
        bool deletePendingPackageSourceAfterInstall,
        Func<PendingInstallBatchItem, PackageInstallExecutionResult> installPackage,
        Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage,
        Func<ChartPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource,
        Action<string> logInfo = null,
        Func<ChartPackage, FileDbMutationReceipt> cleanupPendingPackageSourceWithReceipt = null,
        Action<FileDbMutationReceipt> mutationReceiptObserver = null,
        Func<bool> manualRecoveryObserved = null,
        Func<ChartPackage, string, ISet<string>, int> countComponentMoveTargets = null)
    {
        var result = new PendingInstallBatchResult();
        List<FileDbMutationReceipt> mutationReceipts = [];
        Exception batchFinalizationFailure = null;
        if (plan == null)
        {
            return result;
        }
        plan.IndependentOwnershipLookup = plan.IndependentOwnershipLookup
            is PlannedDestinationOwnershipLookup plannedDestinationOwnershipLookup
                ? plannedDestinationOwnershipLookup
                : new PlannedDestinationOwnershipLookup(plan.IndependentOwnershipLookup);

        foreach (ChartPackage originalPackage in plan.SelectedPendingPackages)
        {
            if (originalPackage == null)
            {
                continue;
            }
            var itemStopwatch = Stopwatch.StartNew();
            PendingInstallBatchItem item = PrepareEstimatedInstallBatchItem(
                originalPackage,
                plan.MoveGuardLookup);
            if (item == null)
            {
                itemStopwatch.Stop();
                continue;
            }
            if (item.IsResourceOnlyInstall
                && (countComponentMoveTargets?.Invoke(
                        item.InstallWorkPackage,
                        item.DestinationDirectory,
                        item.ExcludedComponentPaths)
                    ?? 0) == 0)
            {
                if (!deletePendingPackageSourceAfterInstall)
                {
                    itemStopwatch.Stop();
                    continue;
                }

                plan.CleanupOnlyCandidates.Add(originalPackage);
                FileDbMutationReceipt cleanupReceipt = cleanupPendingPackageSourceWithReceipt?.Invoke(originalPackage);
                bool cleanupSucceeded;
                CleanupSourceKind sourceKind;
                if (cleanupReceipt != null)
                {
                    mutationReceipts.Add(cleanupReceipt);
                    mutationReceiptObserver?.Invoke(cleanupReceipt);
                    cleanupSucceeded = cleanupReceipt.DurableCommit
                        && cleanupReceipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed;
                    sourceKind = ClassifyCleanupSource(originalPackage);
                }
                else
                {
                    (cleanupSucceeded, sourceKind) = cleanupPendingPackageSource != null
                        ? cleanupPendingPackageSource(originalPackage)
                        : (false, CleanupSourceKind.MissingSource);
                }
                if (cleanupSucceeded)
                {
                    result.CleanupOnlySucceeded++;
                    if (sourceKind == CleanupSourceKind.MissingSource)
                    {
                        result.CleanupOnlyMissingSource++;
                    }
                    if (!string.IsNullOrWhiteSpace(originalPackage.path))
                    {
                        result.InstallRowsToDelete.Add(originalPackage.path);
                    }
                    result.PendingPackagesToRemove.Add(originalPackage);
                    ClearPackageInstallDestinations(originalPackage);
                    logInfo?.Invoke("estimated_install_cleanup_only_success package=" + originalPackage.path + " kind=" + sourceKind.ToString().ToLowerInvariant());
                }
                else
                {
                    result.CleanupOnlyFailed++;
                    logInfo?.Invoke("estimated_install_cleanup_only_failed package=" + originalPackage.path);
                }
                itemStopwatch.Stop();
                if (cleanupReceipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                    || cleanupReceipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                {
                    break;
                }
                continue;
            }

            PackageInstallExecutionResult installResult = installPackage?.Invoke(item);
            batchFinalizationFailure ??= installResult?.MutationReceipt?.FinalizationFailure;
            List<FileDbMutationReceipt> packageReceipts = [..
                installResult?.MutationReceipt?.Receipts ?? []];
            mutationReceipts.AddRange(packageReceipts);
            FileDbMutationReceipt packageReceipt = packageReceipts.LastOrDefault();
            bool packageRequiresTerminalStop = packageReceipts.Any(receipt =>
                receipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                || receipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                || installResult?.HasDurableFinalizationFailure == true
                || manualRecoveryObserved?.Invoke() == true;
            bool packageSucceeded = installResult != null
                && installResult.FailedPackages.Count == 0
                && packageReceipt?.DurableCommit == true
                && packageReceipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed
                && !packageRequiresTerminalStop;
            if (!packageSucceeded)
            {
                result.FailedPackages.Add(originalPackage);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(originalPackage.path))
                {
                    result.InstallRowsToDelete.Add(originalPackage.path);
                }
                if (item.IsResourceOnlyInstall)
                {
                    ChartPackage installedDisplayPackage = createInstalledDisplayPackage?.Invoke(
                        originalPackage,
                        item.DestinationDirectory);
                    if (installedDisplayPackage != null
                        && installedDisplayPackage.ChartEntries.Count > 0)
                    {
                        result.DeferredInstalledPackages.Add(installedDisplayPackage);
                    }
                }
                ClearPackageInstallDestinations(originalPackage);
                result.PendingPackagesToRemove.Add(originalPackage);
            }
            itemStopwatch.Stop();
            logInfo?.Invoke(
                "install_pending_packages_to_estimated_destinations item dst="
                + item.DestinationDirectory
                + " packages=1"
                + " failedPackages="
                + (packageSucceeded ? 0 : 1)
                + " installMs="
                + installResult?.MoveMs
                + " totalItemMs="
                + itemStopwatch.ElapsedMilliseconds);
            if (packageRequiresTerminalStop)
            {
                break;
            }
        }
        plan.CleanupOnlyCandidateCount = plan.CleanupOnlyCandidates.Count;
        result.MutationReceipt = new FileDbMutationBatchReceipt(
            mutationReceipts,
            batchFinalizationFailure);
        return result;
    }

    private static PendingInstallBatchItem PrepareEstimatedInstallBatchItem(
        ChartPackage originalPackage,
        IPrimaryHashLookup installedHashes)
    {
        List<PackageChartEntry> packageEntries = [.. (originalPackage?.ChartEntries ?? [])
            .Where(entry => entry?.Chart != null)];
        if (packageEntries.Count == 0)
        {
            return null;
        }
        PackageInstallEntryClassification installClassification = ClassifyPackageInstallEntries(
            packageEntries,
            installedHashes);
        List<PackageChartEntry> installedInLibraryEntries = installClassification.InstalledInLibraryEntries;
        List<PackageChartEntry> installTargetPackageEntries = installClassification.InstallTargetEntries;
        List<PackageChartEntry> duplicateInBatchEntries = installClassification.DuplicateInBatchEntries;
        ApplyAlreadyInstalledWarning(installedInLibraryEntries);

        List<PackageChartEntry> installWorkPackageEntries = installTargetPackageEntries;
        bool isResourceOnlyInstall = false;
        string destinationDirectory;
        if (installTargetPackageEntries.Count == 0)
        {
            if (installedInLibraryEntries.Count == 0)
            {
                return null;
            }
            List<string> destinations = [.. packageEntries
                .Select(entry => entry.Chart.InstallDestination)
                .Where(dst => !string.IsNullOrWhiteSpace(dst))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (destinations.Count != 1)
            {
                return null;
            }
            destinationDirectory = destinations[0];
            isResourceOnlyInstall = true;
            installWorkPackageEntries = packageEntries;
        }
        else
        {
            if (installTargetPackageEntries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)))
            {
                return null;
            }
            destinationDirectory = installTargetPackageEntries[0].Chart.InstallDestination;
            if (string.IsNullOrWhiteSpace(destinationDirectory)
                || installTargetPackageEntries.Any(entry =>
                    !string.Equals(
                        entry.Chart.InstallDestination,
                        destinationDirectory,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        ChartPackage installWorkPackage = ChartPackage.FromChartEntries(installWorkPackageEntries);
        installWorkPackage.path = originalPackage.path;
        installWorkPackage.delete_parent = originalPackage.delete_parent;
        HashSet<string> excludedPaths = null;
        if (installedInLibraryEntries.Count > 0
            || duplicateInBatchEntries.Count > 0
            || isResourceOnlyInstall)
        {
            excludedPaths = new HashSet<string>(
                installedInLibraryEntries
                    .Select(entry => entry.Chart?.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            foreach (PackageChartEntry duplicateEntry in duplicateInBatchEntries)
            {
                if (!string.IsNullOrWhiteSpace(duplicateEntry.Chart?.Path))
                {
                    excludedPaths.Add(duplicateEntry.Chart.Path);
                }
            }
            if (isResourceOnlyInstall)
            {
                foreach (PackageChartEntry packageEntry in packageEntries)
                {
                    if (!string.IsNullOrWhiteSpace(packageEntry.Chart?.Path))
                    {
                        excludedPaths.Add(packageEntry.Chart.Path);
                    }
                }
            }
            if (excludedPaths.Count == 0)
            {
                excludedPaths = null;
            }
        }
        return new PendingInstallBatchItem
        {
            OriginalPackage = originalPackage,
            InstallWorkPackage = installWorkPackage,
            DestinationDirectory = destinationDirectory,
            ExcludedComponentPaths = excludedPaths,
            IsResourceOnlyInstall = isResourceOnlyInstall
        };
    }

    private static CleanupSourceKind ClassifyCleanupSource(ChartPackage package)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.path))
        {
            return CleanupSourceKind.MissingSource;
        }
        if (LongPathFileSystem.DirectoryExists(package.path))
        {
            return CleanupSourceKind.Directory;
        }
        return LongPathFileSystem.FileExists(package.path)
            ? CleanupSourceKind.File
            : CleanupSourceKind.MissingSource;
    }

    /// <summary>
    /// package ごとに filesystem receipt を確定します。durable DB receipt が確定した場合、
    /// または durable 前の失敗が補償済みとなった場合だけ、次の package へ進みます。
    /// manual recovery または durable finalization failure では後続を停止します。
    /// durable receipt 後の package-level finalizer failure も batch receipt に保持し、
    /// 後続を開始しません。
    /// </summary>
    internal PackageInstallExecutionResult InstallPackagesWithFileMutationReceipts(
        IEnumerable<ChartPackage> chartPackagesInstall,
        string installationDirectory,
        Func<ChartPackage, string, PackageSourceCleanupPolicy, IPrimaryHashLookup, IInstalledChartLookupIndex, ISet<string>, Func<PackageInstallExecutionResult, FileDbMutationCommitResult>, FileDbMutationReceipt> movePackageFiles,
        Func<PackageInstallExecutionResult, FileDbMutationCommitResult> applyDurableStorageRows,
        Action<PackageInstallExecutionResult> updateMaintenance,
        Action<PackageInstallExecutionResult> applyScores,
        Action<PackageInstallExecutionResult> applyState,
        PackageSourceCleanupPolicy sourceCleanupPolicy,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null,
        IPrimaryHashLookup existingHashes = null,
        bool skipInstalledPackageWhenNoBms = false,
        IInstalledChartLookupIndex independentOwnershipLookup = null)
    {
        var result = new PackageInstallExecutionResult();
        List<ChartPackage> packages = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        List<FileDbMutationReceipt> mutationReceipts = [];
        Exception batchFinalizationFailure = null;
        PlannedDestinationOwnershipLookup plannedDestinationOwnershipLookup =
            independentOwnershipLookup as PlannedDestinationOwnershipLookup
                ?? new PlannedDestinationOwnershipLookup(independentOwnershipLookup);
        var totalStopwatch = Stopwatch.StartNew();
        var moveStopwatch = Stopwatch.StartNew();
        foreach (ChartPackage package in packages)
        {
            HashSet<string> excludedComponentPaths = null;
            excludedComponentPathsByPackage?.TryGetValue(package, out excludedComponentPaths);
            PackageInstallExecutionResult committedPackageResult = null;
            FileDbMutationReceipt mutationReceipt = movePackageFiles?.Invoke(
                package,
                installationDirectory,
                sourceCleanupPolicy,
                existingHashes,
                plannedDestinationOwnershipLookup,
                excludedComponentPaths,
                packageResult =>
                {
                    committedPackageResult = packageResult;
                    return applyDurableStorageRows?.Invoke(packageResult)
                        ?? FileDbMutationCommitResult.Durable();
                });
            if (mutationReceipt != null)
            {
                mutationReceipts.Add(mutationReceipt);
                // Keep the receipt observable before package-level maintenance,
                // score, and state callbacks can fail.
                result.MutationReceipt = new FileDbMutationBatchReceipt(mutationReceipts);
            }
            if (mutationReceipt?.DurableCommit == true
                && mutationReceipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed)
            {
                committedPackageResult ??= CreatePackageInstallExecutionResult(package);
                plannedDestinationOwnershipLookup.AddCommittedEntries(committedPackageResult.AddedEntries);
                result.AddedEntries.AddRange(committedPackageResult.AddedEntries);
                if (committedPackageResult.AddedCharts.Count > 0)
                {
                    result.AddedCharts.AddRange(committedPackageResult.AddedCharts);
                }
                else
                {
                    result.AddedCharts.AddRange(committedPackageResult.AddedEntries
                        .Select(entry => entry?.Chart)
                        .Where(chart => chart != null));
                }
                if (existingHashes is IMutablePrimaryHashLookup mutableExistingHashes)
                {
                    foreach (PackageChartEntry entry in committedPackageResult.AddedEntries)
                    {
                        string lookupKey = ChartLookupKey.GetPrimaryHash(entry?.Chart);
                        if (!string.IsNullOrWhiteSpace(lookupKey))
                        {
                            mutableExistingHashes.AddPrimaryHash(lookupKey);
                        }
                    }
                }
                bool shouldSkipInstalledPackageRegistration = skipInstalledPackageWhenNoBms
                    && committedPackageResult.AddedEntries.Count == 0;
                if (!shouldSkipInstalledPackageRegistration)
                {
                    result.InstalledPackagesToRegister.Add(package);
                }
                try
                {
                    updateMaintenance?.Invoke(committedPackageResult);
                    applyScores?.Invoke(committedPackageResult);
                    applyState?.Invoke(committedPackageResult);
                }
                catch (Exception exception)
                {
                    batchFinalizationFailure = exception;
                    result.MutationReceipt = result.MutationReceipt.WithFinalizationFailure(exception);
                    result.InstalledPackagesToRegister.RemoveAll(item => ReferenceEquals(item, package));
                    result.FailedPackages.Add(package);
                    // The filesystem/DB receipt is durable, but the package
                    // finalizer is no longer safe to continue after a required
                    // projection callback fails.
                    break;
                }
            }
            if (mutationReceipt != null)
            {
                if (!mutationReceipt.DurableCommit
                    || mutationReceipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                {
                    result.FailedPackages.Add(package);
                    if (mutationReceipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                        || mutationReceipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                    {
                        // A manual-recovery or durable-finalization stop leaves the
                        // current receipt authoritative; continuing the batch would
                        // create a second mutation owner while the first one still
                        // requires terminal handling.
                        break;
                    }
                }
            }
            else
            {
                result.FailedPackages.Add(package);
            }
        }
        moveStopwatch.Stop();
        result.MoveMs = moveStopwatch.ElapsedMilliseconds;
        foreach (PackageChartEntry addedEntry in result.AddedEntries.Where(entry => entry?.Chart != null))
        {
            // A receipt-aware move clears package-only warnings before the DB owner sees the rows.
            addedEntry.ClearPostInstallState();
        }
        result.MutationReceipt = new FileDbMutationBatchReceipt(mutationReceipts, batchFinalizationFailure);
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    public ForceInstallBatchResult ForceInstallPackages(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        Func<ChartPackage, bool> confirmNormalInstallOverride,
        Func<IEnumerable<ChartPackage>, List<ChartPackage>, List<ChartPackage>> installPackages,
        Action<string> logInfo = null)
    {
        return ForceInstallPackagesCore(
            packages,
            currentPendingPackages,
            confirmNormalInstallOverride,
            (packagesToInstall, deferredInstalledPackages) => new ForceInstallPackageApplyResult(
                installPackages?.Invoke(packagesToInstall, deferredInstalledPackages),
                manualRecoveryRequired: false),
            logInfo);
    }

    /// <summary>
    /// Runs force-install packages while carrying each package's typed mutation
    /// terminal state.  A manual-recovery result is terminal for the batch.
    /// </summary>
    internal ForceInstallBatchResult ForceInstallPackagesWithFileMutationReceipts(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        Func<ChartPackage, bool> confirmNormalInstallOverride,
        Func<IEnumerable<ChartPackage>, List<ChartPackage>, ForceInstallPackageApplyResult> installPackages,
        Action<string> logInfo = null)
    {
        return ForceInstallPackagesCore(
            packages,
            currentPendingPackages,
            confirmNormalInstallOverride,
            installPackages,
            logInfo);
    }

    private ForceInstallBatchResult ForceInstallPackagesCore(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        Func<ChartPackage, bool> confirmNormalInstallOverride,
        Func<IEnumerable<ChartPackage>, List<ChartPackage>, ForceInstallPackageApplyResult> installPackages,
        Action<string> logInfo)
    {
        var result = new ForceInstallBatchResult();
        List<FileDbMutationReceipt> mutationReceipts = [];
        Exception batchFinalizationFailure = null;
        List<ChartPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        result.Requested = requestedPackages.Count;
        foreach (ChartPackage requestedPackage in requestedPackages)
        {
            ChartPackage pendingPackage = pendingPackages.FirstOrDefault(pkg => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.Skipped++;
                logInfo?.Invoke("force_install_batch skip_not_pending path=" + (requestedPackage.path ?? "(null)"));
                continue;
            }
            bool hasInstallDestination = pendingPackage.ChartEntries.Any(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestination));
            if (hasInstallDestination && confirmNormalInstallOverride != null && !confirmNormalInstallOverride(pendingPackage))
            {
                result.Skipped++;
                logInfo?.Invoke("force_install_batch skipped_by_confirm path=" + (pendingPackage.path ?? "(null)"));
                continue;
            }
            List<ChartPackage> deferredInstalledPackages = [];
            ForceInstallPackageApplyResult applyResult = installPackages?.Invoke(
                [pendingPackage],
                deferredInstalledPackages)
                ?? new ForceInstallPackageApplyResult([], manualRecoveryRequired: false);
            IReadOnlyList<ChartPackage> failedPackages = applyResult.FailedPackages;
            if (applyResult.MutationReceipt?.Receipts != null)
            {
                mutationReceipts.AddRange(applyResult.MutationReceipt.Receipts);
            }
            batchFinalizationFailure ??= applyResult.MutationReceipt?.FinalizationFailure;
            result.Processed++;
            if (failedPackages.Count == 0 && !applyResult.HasDurableFinalizationFailure)
            {
                result.PendingPackagesToRemove.Add(pendingPackage);
                result.DeferredInstalledPackages.AddRange(deferredInstalledPackages.Where(pkg => pkg != null));
                result.Succeeded++;
                ClearPackageInstallDestinations(pendingPackage);
                logInfo?.Invoke("force_install_batch success path=" + (pendingPackage.path ?? "(null)"));
            }
            else
            {
                result.Failed++;
                logInfo?.Invoke("force_install_batch failed path=" + (pendingPackage.path ?? "(null)"));
            }
            if (applyResult.ManualRecoveryRequired
                || applyResult.HasDurableFinalizationFailure)
            {
                // Recovery paths from this package remain authoritative; no
                // later package may start a second mutation owner.
                break;
            }
        }
        result.MutationReceipt = new FileDbMutationBatchReceipt(
            mutationReceipts,
            batchFinalizationFailure);
        return result;
    }

    public PendingResourceOverwriteExecutionResult ExecuteInstalledOnlyResourceOverwrite(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        bool deletePendingPackageSourceAfterInstall,
        Func<ChartPackage, InstalledOnlyPackageResolutionResult> resolveDestination,
        Func<InstalledOnlyPackageResolutionResult, ChartPackage, string> describeSkipDetail,
        Func<ChartPackage, string, bool> hasResourceOverwriteTargets,
        Func<ChartPackage, string, bool> installPackageToEstimatedDestination,
        Func<ChartPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource,
        Func<ChartPackage, bool> isPackageStillPending,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null,
        Func<ChartPackage, string, PendingInstallBatchResult> installPackageToEstimatedDestinationWithReceipt = null,
        Func<ChartPackage, FileDbMutationReceipt> cleanupPendingPackageSourceWithReceipt = null,
        Action<FileDbMutationReceipt> mutationReceiptObserver = null,
        Func<bool> manualRecoveryObserved = null)
    {
        var result = new PendingResourceOverwriteExecutionResult();
        List<ChartPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        result.Requested = requestedPackages.Count;
        foreach (ChartPackage requestedPackage in requestedPackages)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            ChartPackage pendingPackage = pendingPackages.FirstOrDefault(pkg => ReferenceEquals(pkg, requestedPackage) || (!string.IsNullOrWhiteSpace(pkg.path) && !string.IsNullOrWhiteSpace(requestedPackage.path) && pkg.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.SkippedNotPending++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_resource_overwrite skip_not_pending path=" + requestedPackage.path);
                onEachProcessed?.Invoke();
                continue;
            }
            InstalledOnlyPackageResolutionResult resolution = resolveDestination?.Invoke(pendingPackage) ?? new InstalledOnlyPackageResolutionResult();
            if (!resolution.Success)
            {
                if (resolution.Reason == InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories || resolution.Reason == InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories)
                {
                    result.SkippedMultiDestination++;
                }
                else
                {
                    result.SkippedMissingInstlDst++;
                }
                logInfo?.Invoke(describeSkipDetail?.Invoke(resolution, pendingPackage));
                result.Processed++;
                onEachProcessed?.Invoke();
                continue;
            }
            string destinationDir = resolution.DestinationDirectory;
            logInfo?.Invoke("advanced_pending_resource_overwrite resolve_selected path=" + pendingPackage.path + " dst=" + destinationDir + " charts=" + pendingPackage.ChartEntries.Count);
            if (!hasResourceOverwriteTargets(pendingPackage, destinationDir))
            {
                bool stopAfterCurrent = false;
                if (deletePendingPackageSourceAfterInstall)
                {
                    CleanupSourceKind sourceKindBeforeCleanup = ClassifyCleanupSource(pendingPackage);
                    FileDbMutationReceipt cleanupReceipt = cleanupPendingPackageSourceWithReceipt?.Invoke(pendingPackage);
                    if (cleanupReceipt != null)
                    {
                        result.MutationReceipt = AppendMutationReceipt(
                            result.MutationReceipt,
                            cleanupReceipt);
                        mutationReceiptObserver?.Invoke(cleanupReceipt);
                    }
                    (bool Success, CleanupSourceKind SourceKind) = cleanupReceipt != null
                        ? (cleanupReceipt.DurableCommit
                            && cleanupReceipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed, sourceKindBeforeCleanup)
                        : cleanupPendingPackageSource != null
                            ? cleanupPendingPackageSource(pendingPackage)
                            : (false, CleanupSourceKind.MissingSource);
                    if (Success)
                    {
                        result.SucceededCleanupOnly++;
                        result.PendingPackagesToRemove.Add(pendingPackage);
                        if (!string.IsNullOrWhiteSpace(pendingPackage.path))
                        {
                            result.InstallRowsToDelete.Add(pendingPackage.path);
                        }
                        logInfo?.Invoke("advanced_pending_resource_overwrite cleanup_only_success path=" + pendingPackage.path + " kind=" + SourceKind.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        result.Failed++;
                        logInfo?.Invoke("advanced_pending_resource_overwrite cleanup_only_failed path=" + pendingPackage.path);
                    }
                    stopAfterCurrent = cleanupReceipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                        || cleanupReceipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed
                        || manualRecoveryObserved?.Invoke() == true;
                }
                else
                {
                    result.SkippedNoComponentTarget++;
                    logInfo?.Invoke("advanced_pending_resource_overwrite skip_no_component_target path=" + pendingPackage.path);
                }
                result.Processed++;
                onEachProcessed?.Invoke();
                if (stopAfterCurrent)
                {
                    break;
                }
                continue;
            }
            List<PackageChartEntry> packageEntries = [.. pendingPackage.ChartEntries.Where(entry => entry != null)];
            var installDestinations = packageEntries.ToDictionary(entry => entry, entry => entry.CaptureInstallDestinationState());
            foreach (PackageChartEntry entry in packageEntries)
            {
                entry.SetInstallDestinationPathOnly(destinationDir);
            }
            bool installSucceeded = false;
            PendingInstallBatchResult installBatchResult = null;
            try
            {
                if (installPackageToEstimatedDestinationWithReceipt != null)
                {
                    installBatchResult = installPackageToEstimatedDestinationWithReceipt(pendingPackage, destinationDir);
                    result.MutationReceipt = CombineMutationReceipts(
                        result.MutationReceipt,
                        installBatchResult?.MutationReceipt);
                    foreach (FileDbMutationReceipt mutationReceipt in installBatchResult?.MutationReceipt?.Receipts ?? [])
                    {
                        mutationReceiptObserver?.Invoke(mutationReceipt);
                    }
                    installSucceeded = installBatchResult?.HasDurableCommit == true
                        && !installBatchResult.HasDurableFinalizationFailure;
                }
                else
                {
                    installSucceeded = installPackageToEstimatedDestination != null
                        && installPackageToEstimatedDestination(pendingPackage, destinationDir);
                }
            }
            finally
            {
                if (isPackageStillPending != null && isPackageStillPending(pendingPackage))
                {
                    foreach (KeyValuePair<PackageChartEntry, PackageChartInstallDestinationState> item in installDestinations)
                    {
                        item.Key.RestoreInstallDestinationState(item.Value);
                    }
                }
            }
            if (!installSucceeded || (isPackageStillPending != null && isPackageStillPending(pendingPackage)))
            {
                result.Failed++;
                logInfo?.Invoke("advanced_pending_resource_overwrite install_failed path=" + pendingPackage.path + " dst=" + destinationDir);
            }
            else
            {
                result.SucceededInstall++;
                logInfo?.Invoke("advanced_pending_resource_overwrite install_success path=" + pendingPackage.path + " dst=" + destinationDir);
            }
            result.Processed++;
            onEachProcessed?.Invoke();
            if (installBatchResult?.ManualRecoveryRequired == true
                || installBatchResult?.HasDurableFinalizationFailure == true
                || manualRecoveryObserved?.Invoke() == true)
            {
                break;
            }
        }
        return result;
    }

    private static FileDbMutationBatchReceipt AppendMutationReceipt(
        FileDbMutationBatchReceipt existing,
        FileDbMutationReceipt receipt)
    {
        return CombineMutationReceipts(existing, receipt == null ? null : new FileDbMutationBatchReceipt([receipt]));
    }

    private static FileDbMutationBatchReceipt CombineMutationReceipts(
        FileDbMutationBatchReceipt first,
        FileDbMutationBatchReceipt second)
    {
        if (first == null)
        {
            return second;
        }
        if (second == null)
        {
            return first;
        }
        return new FileDbMutationBatchReceipt(
            first.Receipts.Concat(second.Receipts),
            first.FinalizationFailure ?? second.FinalizationFailure);
    }

    internal PendingZeroNoteRenameResult RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
        IEnumerable<ChartFile> targetCharts,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        var result = new PendingZeroNoteRenameResult();
        List<BMSFile> files = GetBmsFormatChartFiles(targetCharts);
        result.Total = files.Count;
        foreach (BMSFile file in files)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            string extension = Path.GetExtension(file.path);
            string targetExtension = null;
            if (!string.IsNullOrWhiteSpace(extension))
            {
                if (extension.StartsWith(".b", StringComparison.OrdinalIgnoreCase))
                {
                    targetExtension = ".bmx";
                }
                else if (extension.StartsWith(".p", StringComparison.OrdinalIgnoreCase))
                {
                    targetExtension = ".pmx";
                }
            }
            if (string.IsNullOrWhiteSpace(targetExtension) || extension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_zero_note_rename skip_unsupported_ext path=" + file.path + " ext=" + extension);
                onEachProcessed?.Invoke();
                continue;
            }
            bool isZeroNote;
            try
            {
                isZeroNote = BMSFile.IsZeroNoteBMSFile(file.path);
            }
            catch
            {
                result.Skipped++;
                result.Processed++;
                onEachProcessed?.Invoke();
                continue;
            }
            if (!isZeroNote)
            {
                result.Skipped++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_zero_note_rename skip_not_zero path=" + file.path);
                onEachProcessed?.Invoke();
                continue;
            }
            result.ZeroNote++;
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + targetExtension);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.path);
                    result.Renamed++;
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.path);
                    result.DuplicateDeleted++;
                    break;
                default:
                    result.Failed++;
                    result.Failures.Add(new PendingZeroNoteRenameFailure
                    {
                        File = file,
                        Outcome = renameResult
                    });
                    break;
            }
            result.Processed++;
            onEachProcessed?.Invoke();
        }
        return result;
    }

    internal PendingExtensionRenameResult RenamePendingBmsFormatChartFileExtensions(
        IEnumerable<ChartFile> targetCharts,
        string newExt,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        var result = new PendingExtensionRenameResult();
        var stopwatch = Stopwatch.StartNew();
        List<BMSFile> files = [.. GetBmsFormatChartFiles(targetCharts).Where(file => LongPathFileSystem.FileExists(file.path))];
        result.Total = files.Count;
        foreach (BMSFile file in files)
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    result.Renamed++;
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.path);
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    result.DuplicateDeleted++;
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.path);
                    break;
                default:
                    result.Skipped++;
                    if (renameResult.FailureException != null)
                    {
                        result.Failed++;
                        result.Failures.Add(new PendingExtensionRenameFailure
                        {
                            File = file,
                            Outcome = renameResult
                        });
                    }
                    break;
            }
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    internal PendingPackageSourceDeletionResult DeletePendingPackageSources(
        IEnumerable<PendingPackageSourceDeletionTarget> targets,
        bool sendToRecycleBin,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        var result = new PendingPackageSourceDeletionResult();
        List<PendingPackageSourceDeletionTarget> requestedTargets =
            DeduplicatePendingPackageSourceDeletionTargets(targets);
        result.Requested = requestedTargets.Count;
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        foreach (PendingPackageSourceDeletionTarget target in requestedTargets)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            ChartPackage pendingPackage = target.Package;
            string sourcePath = target.Path;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                result.Failed++;
                result.Failures.Add(new PendingPackageSourceDeletionFailure
                {
                    Package = pendingPackage,
                    Exception = new InvalidOperationException("Pending package source path is empty."),
                    IsDirectory = false
                });
                result.Processed++;
                logInfo?.Invoke("advanced_pending_cleanup failed_empty_source");
                onEachProcessed?.Invoke();
                continue;
            }
            bool isDirectory = LongPathFileSystem.DirectoryExists(sourcePath);
            bool isFile = !isDirectory && LongPathFileSystem.FileExists(sourcePath);
            try
            {
                if (isDirectory)
                {
                    if (!TryValidatePendingDirectoryPath(sourcePath, out Exception directoryValidationFailure))
                    {
                        result.Failed++;
                        result.Failures.Add(new PendingPackageSourceDeletionFailure
                        {
                            Package = pendingPackage,
                            Exception = directoryValidationFailure,
                            IsDirectory = true
                        });
                        result.Processed++;
                        onEachProcessed?.Invoke();
                        continue;
                    }
                    if (sendToRecycleBin)
                    {
                        fileMutationService.DeleteDirectoryShell(sourcePath, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    }
                    else
                    {
                        fileMutationService.DeleteDirectoryDirect(sourcePath, recursive: true, recursiveDirectoryTreeFileMutationOptions);
                    }
                    logInfo?.Invoke("advanced_pending_cleanup deleted path=" + sourcePath + " kind=directory");
                }
                else if (isFile)
                {
                    if (!TryValidatePendingFilePath(sourcePath, out Exception fileValidationFailure))
                    {
                        result.Failed++;
                        result.Failures.Add(new PendingPackageSourceDeletionFailure
                        {
                            Package = pendingPackage,
                            Exception = fileValidationFailure,
                            IsDirectory = false
                        });
                        result.Processed++;
                        onEachProcessed?.Invoke();
                        continue;
                    }
                    if (sendToRecycleBin)
                    {
                        fileMutationService.DeleteFileShell(sourcePath, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                    }
                    else
                    {
                        fileMutationService.DeleteFileDirect(sourcePath, targetOnlyFileMutationOptions);
                    }
                    logInfo?.Invoke("advanced_pending_cleanup deleted path=" + sourcePath + " kind=file");
                }
                else
                {
                    logInfo?.Invoke("advanced_pending_cleanup missing_source_removed path=" + sourcePath);
                }
                result.PackagesToRemove.Add(pendingPackage);
                result.Removed++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Failures.Add(new PendingPackageSourceDeletionFailure
                {
                    Package = pendingPackage,
                    Exception = ex,
                    IsDirectory = isDirectory
                });
            }
            result.Processed++;
            onEachProcessed?.Invoke();
        }
        return result;
    }

    /// <summary>
    /// Deletes selected pending charts.  With whole-package deletion enabled,
    /// fully selected directory packages (including resources) are deleted in
    /// one shell operation using the requested recycle policy.  Failed package
    /// operations retain their pending rows and do not fall back to file deletion.
    /// Single-file packages and partial selections delete only selected files.
    /// </summary>
    public PendingFileDeletionResult DeletePendingCharts(
        IEnumerable<ChartFile> charts,
        IEnumerable<ChartPackage> pendingPackages,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        return DeletePendingChartTargets(
            (charts ?? []).Where(chart => chart != null).Select(PendingChartDeletionTarget.FromChartFile),
            pendingPackages,
            sendToRecycleBin,
            deleteContainingPackageFoldersWhenNoBms,
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions);
    }

    private PendingFileDeletionResult DeletePendingChartTargets(
        IEnumerable<PendingChartDeletionTarget> targets,
        IEnumerable<ChartPackage> pendingPackages,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        var result = new PendingFileDeletionResult();
        List<PendingChartDeletionTarget> selectedTargets = DeduplicatePendingChartDeletionTargets(targets);
        result.Requested = selectedTargets.Count;
        if (selectedTargets.Count == 0)
        {
            return result;
        }

        var selectedPaths = new HashSet<string>(
            selectedTargets.Select(target => target.Path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;

        var handledByPackageDeletionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (deleteContainingPackageFoldersWhenNoBms)
        {
            foreach (ChartPackage package in libraryFileOperationsService.GetPendingPackagesFullyCoveredBySelection(
                pendingPackages,
                selectedPaths))
            {
                // Directory packages are identified during discovery.  A single-file
                // package never authorizes deleting its containing directory.
                if (string.IsNullOrWhiteSpace(package.path)
                    || !LongPathFileSystem.DirectoryExists(package.path))
                {
                    continue;
                }
                List<string> packageChartPaths = [.. package.ChartEntries
                    .Select(entry => entry.Chart.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                handledByPackageDeletionPaths.UnionWith(packageChartPaths);
                result.Processed += packageChartPaths.Count;
                try
                {
                    if (!TryValidatePendingDirectoryPath(package.path, out Exception directoryValidationFailure))
                    {
                        throw directoryValidationFailure;
                    }

                    // Selecting every remaining chart with this option authorizes
                    // the package, including resources, as one deletion target.  Do
                    // not delete charts first: a failed folder operation must not
                    // fall back to destroying individual files or removing rows.
                    fileMutationService.DeleteDirectoryShell(
                        package.path,
                        UIOption.OnlyErrorDialogs,
                        recycleOption,
                        recursiveDirectoryTreeFileMutationOptions);
                    result.ChartPathsToRemove.AddRange(packageChartPaths);
                    result.Removed += packageChartPaths.Count;
                }
                catch (Exception failure)
                {
                    result.Failed += packageChartPaths.Count;
                    result.Failures.Add(new PendingFileDeletionFailure
                    {
                        Path = package.path,
                        Exception = failure,
                        IsDirectory = true
                    });
                }
            }
        }

        foreach (PendingChartDeletionTarget pendingChart in selectedTargets)
        {
            string pendingChartPath = pendingChart.Path;
            if (handledByPackageDeletionPaths.Contains(pendingChartPath))
            {
                continue;
            }
            result.Processed++;
            if (!TryValidatePendingFilePath(pendingChartPath, out Exception validationFailure))
            {
                result.Failed++;
                result.Failures.Add(new PendingFileDeletionFailure
                {
                    Path = pendingChartPath,
                    Exception = validationFailure,
                    IsDirectory = false
                });
                continue;
            }

            try
            {
                fileMutationService.DeleteFileShell(pendingChartPath, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                result.ChartPathsToRemove.Add(pendingChartPath);
                result.Removed++;
            }
            catch (Exception failure)
            {
                result.Failed++;
                result.Failures.Add(new PendingFileDeletionFailure
                {
                    Path = pendingChartPath,
                    Exception = failure,
                    IsDirectory = false
                });
            }
        }

        return result;
    }

    private static bool TryValidatePendingFilePath(string path, out Exception failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            failure = new InvalidOperationException("Pending chart source path is empty.");
            return false;
        }
        if (!LongPathFileSystem.FileExists(path))
        {
            failure = new FileNotFoundException("Pending chart source file was not found.", path);
            return false;
        }
        try
        {
            FileAttributes attributes = LongPathFileSystem.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                failure = new IOException("Pending chart source is not a file.");
                return false;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                failure = new IOException("Pending chart source reparse points are not supported.");
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException
            || ex is SecurityException)
        {
            failure = ex;
            return false;
        }
    }

    private static bool TryValidatePendingDirectoryPath(string path, out Exception failure)
    {
        failure = null;
        try
        {
            FileAttributes attributes = LongPathFileSystem.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                failure = new IOException("Pending package cleanup target is not a directory.");
                return false;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                failure = new IOException("Pending package cleanup reparse points are not supported.");
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException
            || ex is SecurityException)
        {
            failure = ex;
            return false;
        }
    }

    private sealed class PendingChartDeletionTarget
    {
        private PendingChartDeletionTarget(ChartFile chart)
        {
            Chart = chart;
        }

        internal ChartFile Chart { get; }

        internal string Path => Chart?.Path;

        internal static PendingChartDeletionTarget FromChartFile(ChartFile chart)
        {
            if (chart == null)
            {
                return null;
            }
            return new PendingChartDeletionTarget(chart);
        }
    }

    private sealed class PackageInstallEntryClassification
    {
        internal List<PackageChartEntry> InstalledInLibraryEntries { get; } = [];

        internal List<PackageChartEntry> DuplicateInBatchEntries { get; } = [];

        internal List<PackageChartEntry> InstallTargetEntries { get; } = [];
    }

    private sealed class AutoInstallCandidateBatchClassification
    {
        internal List<ChartPackage> InstallCandidates { get; } = [];

        internal List<AutoInstallDuplicateCandidate> DuplicateCandidates { get; } = [];
    }

    private sealed class AutoInstallDuplicateCandidate
    {
        internal ChartPackage Package { get; set; }

        internal HashSet<string> DuplicatePrimaryHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyAlreadyInstalledWarning(IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            entry?.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
            entry?.SetWarning(ChartWarningKind.AlreadyInstalled, Properties.Resources.Warning_AlreadyInstalled);
        }
    }

    private static PackageInstallEntryClassification ClassifyPackageInstallEntries(
        IEnumerable<PackageChartEntry> entries,
        IPrimaryHashLookup installedHashes,
        Func<ChartFile, bool> isInstalledChart = null)
    {
        installedHashes ??= EmptyPrimaryHashLookup.Instance;
        var result = new PackageInstallEntryClassification();
        var packageHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry entry in entries ?? [])
        {
            ChartFile chart = entry?.Chart;
            string lookupKey = ChartLookupKey.GetPrimaryHash(chart);
            bool hasLookupKey = !string.IsNullOrWhiteSpace(lookupKey);
            if ((hasLookupKey && installedHashes.ContainsPrimaryHash(lookupKey))
                || (isInstalledChart != null && isInstalledChart(chart)))
            {
                result.InstalledInLibraryEntries.Add(entry);
                continue;
            }
            if (hasLookupKey && !packageHashes.Add(lookupKey))
            {
                result.DuplicateInBatchEntries.Add(entry);
                continue;
            }
            result.InstallTargetEntries.Add(entry);
        }
        return result;
    }

    private static AutoInstallCandidateBatchClassification ClassifyAutoInstallCandidateBatch(IEnumerable<ChartPackage> packages)
    {
        var result = new AutoInstallCandidateBatchClassification();
        var reservedHashes = new PrimaryHashGuardLookup(EmptyPrimaryHashLookup.Instance);
        foreach (ChartPackage package in (packages ?? []).Where(pkg => pkg != null))
        {
            var duplicateHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> packageHashes = [.. (package.ChartEntries ?? [])
                .Select(entry => ChartLookupKey.GetPrimaryHash(entry?.Chart))
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            foreach (string hash in packageHashes)
            {
                if (reservedHashes.ContainsPrimaryHash(hash))
                {
                    duplicateHashes.Add(hash);
                }
            }
            if (duplicateHashes.Count > 0)
            {
                var duplicateCandidate = new AutoInstallDuplicateCandidate
                {
                    Package = package,
                };
                duplicateCandidate.DuplicatePrimaryHashes.UnionWith(duplicateHashes);
                result.DuplicateCandidates.Add(duplicateCandidate);
                continue;
            }
            result.InstallCandidates.Add(package);
            foreach (string hash in packageHashes)
            {
                reservedHashes.AddPrimaryHash(hash);
            }
        }
        return result;
    }

    private static IPrimaryHashLookup CreatePackagePrimaryHashLookup(IEnumerable<ChartPackage> packages)
    {
        var result = new PrimaryHashGuardLookup(EmptyPrimaryHashLookup.Instance);
        foreach (PackageChartEntry entry in (packages ?? []).Where(pkg => pkg != null).SelectMany(pkg => pkg.ChartEntries))
        {
            string lookupKey = ChartLookupKey.GetPrimaryHash(entry?.Chart);
            if (!string.IsNullOrWhiteSpace(lookupKey))
            {
                result.AddPrimaryHash(lookupKey);
            }
        }
        return result;
    }

    private static IEnumerable<PackageChartEntry> GetEntriesMatchedByPrimaryHashes(
        IEnumerable<PackageChartEntry> entries,
        IPrimaryHashLookup lookup,
        ISet<string> allowedPrimaryHashes = null)
    {
        lookup ??= EmptyPrimaryHashLookup.Instance;
        foreach (PackageChartEntry entry in entries ?? [])
        {
            string lookupKey = ChartLookupKey.GetPrimaryHash(entry?.Chart);
            if (!string.IsNullOrWhiteSpace(lookupKey)
                && (allowedPrimaryHashes == null || allowedPrimaryHashes.Contains(lookupKey))
                && lookup.ContainsPrimaryHash(lookupKey))
            {
                yield return entry;
            }
        }
    }

    private static bool IsMatchedRemovedEntry(PackageChartEntry entry, HashSet<string> removedPaths)
    {
        if (entry == null)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart?.Path) && removedPaths.Contains(entry.Chart.Path);
    }

    private static void AddChartPathToRemove(List<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths?.Add(path);
        }
    }

}
