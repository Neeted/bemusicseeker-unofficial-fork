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
using Microsoft.VisualBasic.FileIO;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

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
            var entry = BMSFile.CreateBMSFileFromFile(filePath);
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

        var package = ChartPackage.FromChartEntries(knownEntries);
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
            var sourceStopwatch = Stopwatch.StartNew();
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
                    var extractStopwatch = Stopwatch.StartNew();
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
                // この局所物理経路の marker は source cleanup の許可だけを表します。
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
        return MovePackageFilesCore(
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
            applyDurableCommit,
            sourceCleanupPolicy,
            showMessageBoxOnInstallFail,
            existingHashes,
            independentOwnershipLookup,
            excludedComponentPaths,
            onPreflightPrepared,
            enqueueDiagnosticEffect,
            deferDurableCommitToSession: false,
            sessionPreparedObserver: null,
            isPreflightRefusal: out _);
    }

    /// <summary>
    /// 導入・フォルダ統合の session 向けに filesystem promotion までを実行します。
    /// canonical durable apply と source cleanup は outer session が所有し、ここでは実行しません。
    /// </summary>
    internal PackageInstallSessionMoveResult MovePackageFilesForInstallSession(
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
        Action<PackageInstallExecutionResult> onPreflightPrepared = null,
        Action<Action> enqueueDiagnosticEffect = null)
    {
        PackageInstallExecutionResult executionResult = null;
        PackageInstallSessionPhysicalMutation physicalMutation = null;
        FileDbMutationReceipt failureReceipt = MovePackageFilesCore(
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
            applyDurableCommit: null,
            sourceCleanupPolicy,
            showMessageBoxOnInstallFail,
            existingHashes,
            independentOwnershipLookup,
            excludedComponentPaths,
            onPreflightPrepared,
            enqueueDiagnosticEffect,
            deferDurableCommitToSession: true,
            sessionPreparedObserver: (result, mutation) =>
            {
                executionResult = result;
                physicalMutation = mutation;
            },
            isPreflightRefusal: out bool isPreflightRefusal);
        return new PackageInstallSessionMoveResult(executionResult, physicalMutation, failureReceipt, isPreflightRefusal);
    }

    private FileDbMutationReceipt MovePackageFilesCore(
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
        bool showMessageBoxOnInstallFail,
        IPrimaryHashLookup existingHashes,
        IInstalledChartLookupIndex independentOwnershipLookup,
        ISet<string> excludedComponentPaths,
        Action<PackageInstallExecutionResult> onPreflightPrepared,
        Action<Action> enqueueDiagnosticEffect,
        bool deferDurableCommitToSession,
        Action<PackageInstallExecutionResult, PackageInstallSessionPhysicalMutation> sessionPreparedObserver,
        out bool isPreflightRefusal)
    {
        isPreflightRefusal = false;
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        if (fileMutationService == null)
        {
            throw new ArgumentNullException(nameof(fileMutationService));
        }
        if (!deferDurableCommitToSession && applyDurableCommit == null)
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
                isPreflightRefusal = true;
                throw new FileNotFoundException(Resources.Error_FileNotFound, sourcePath);
            }

            bool isSingleFile = LongPathFileSystem.FileExists(sourcePath);
            bool isDirectory = LongPathFileSystem.DirectoryExists(sourcePath);
            if (!isSingleFile && !isDirectory)
            {
                // 変更開始前に確定した source 欠落だけを拒否として扱います。
                // executor 内の同じ例外型は、予期しない physical failure のまま保持します。
                isPreflightRefusal = true;
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
                isPreflightRefusal = true;
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

            if (deferDurableCommitToSession)
            {
                FileDbMutationPreparedCommit preparedCommit = executor.Prepare();
                if (!preparedCommit.Prepared)
                {
                    FileDbMutationReceipt prepareFailureReceipt = preparedCommit.FailureReceipt;
                    if (showMessageBoxOnInstallFail)
                    {
                        enqueueDiagnosticEffect?.Invoke(() => ShowPackageMutationFailure(
                            package,
                            destinationDirectory,
                            prepareFailureReceipt?.Failure,
                            dialogService,
                            getDisplayedExceptionMessage));
                    }
                    return prepareFailureReceipt;
                }

                try
                {
                    sessionPreparedObserver?.Invoke(
                        detachedPackageResult,
                        new PackageInstallSessionPhysicalMutation(
                            preparedCommit,
                            () => ApplyLivePackageInstallState(
                                package,
                                destinationMap,
                                liveInstallEntrySnapshots),
                            destinationDirectory));
                    return null;
                }
                catch (Exception exception)
                {
                    return preparedCommit.FailBeforeDurableCommit(exception);
                }
            }

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
        // resource-only install では chart 自体を移動しないため install target は 0 件です。
        // その場合に元 pending package を空 package へ置換すると、後段の installed display
        // projection と DST clear/publication が元 chart identity を失います。chart live-state の
        // 更新対象がある場合だけ package path/entry を導入先へ進めます。
        if (entries.Count == 0)
        {
            return;
        }
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
    /// operation 開始時 snapshot に、同じ install session で実際に physical success した chart だけを重ねる lookup です。
    /// 未実行予約や failed/skipped package は追加しません。
    /// </summary>
    private sealed class SessionSuccessOwnershipOverlay : IInstalledChartLookupIndex
    {
        private readonly IInstalledChartLookupIndex ownershipBaseline;
        private readonly IPrimaryHashLookup hashBaseline;
        private readonly PrimaryHashSetLookup successfulPrimaryHashes = new();
        private readonly Dictionary<string, HashSet<string>> successfulDirectoriesByHash =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>operation 開始時 snapshot に physical-success facts だけを重ねる lookup を作成します。</summary>
        /// <param name="ownershipBaseline">operation 開始時の directory ownership snapshot。</param>
        /// <param name="hashBaseline">primary hash 判定用 snapshot。省略時は ownership baseline を使用します。</param>
        internal SessionSuccessOwnershipOverlay(
            IInstalledChartLookupIndex ownershipBaseline,
            IPrimaryHashLookup hashBaseline = null)
        {
            this.ownershipBaseline = ownershipBaseline;
            this.hashBaseline = hashBaseline ?? ownershipBaseline;
        }

        /// <summary>baseline と operation-local physical-success directory を合わせた hash 数。</summary>
        public int HashCount => (ownershipBaseline?.HashCount ?? 0)
            + successfulDirectoriesByHash.Keys.Count(hash =>
                ownershipBaseline == null
                || ownershipBaseline.GetDistinctDirectoriesByPrimaryHash(hash).Count == 0);

        /// <summary>baseline と operation-local physical-success hash を合わせた distinct primary hash 数。</summary>
        public int DistinctPrimaryHashCount => (hashBaseline?.DistinctPrimaryHashCount ?? 0)
            + successfulPrimaryHashes.PrimaryHashes.Count(hash => hashBaseline?.ContainsPrimaryHash(hash) != true);

        public bool ContainsPrimaryHash(string lookupHash)
            => hashBaseline?.ContainsPrimaryHash(lookupHash) == true
               || successfulPrimaryHashes.ContainsPrimaryHash(lookupHash);

        public int GetPrimaryHashCount(string lookupHash)
            => (hashBaseline?.GetPrimaryHashCount(lookupHash) ?? 0)
               + successfulPrimaryHashes.GetPrimaryHashCount(lookupHash);

        public IReadOnlyList<string> GetDistinctDirectoriesByPrimaryHash(string lookupHash)
        {
            HashSet<string> directories = new(
                ownershipBaseline?.GetDistinctDirectoriesByPrimaryHash(lookupHash) ?? [],
                StringComparer.OrdinalIgnoreCase);
            if (successfulDirectoriesByHash.TryGetValue(lookupHash, out HashSet<string> successfulDirectories))
            {
                directories.UnionWith(successfulDirectories);
            }
            return [.. directories];
        }

        public int GetUniquePrimaryHashCountByDirectory(string directoryPath)
        {
            int baselineCount = ownershipBaseline?.GetUniquePrimaryHashCountByDirectory(directoryPath) ?? 0;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return baselineCount;
            }
            string normalizedDirectory = Path.GetFullPath(directoryPath);
            int successfulCount = successfulDirectoriesByHash.Count(item =>
                item.Value.Contains(normalizedDirectory)
                && !(ownershipBaseline?.GetDistinctDirectoriesByPrimaryHash(item.Key) ?? [])
                    .Contains(normalizedDirectory, StringComparer.OrdinalIgnoreCase));
            return baselineCount + successfulCount;
        }

        /// <summary>physical prepare が成功した chart だけを後続 package 判定用 overlay へ追加します。</summary>
        internal void AddSuccessfulCharts(IEnumerable<ChartFile> charts)
        {
            foreach (ChartFile chart in charts ?? [])
            {
                string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
                if (string.IsNullOrWhiteSpace(lookupHash))
                {
                    continue;
                }
                successfulPrimaryHashes.AddPrimaryHash(lookupHash);
                string chartDirectory = string.IsNullOrWhiteSpace(chart?.Path)
                    ? null
                    : Path.GetDirectoryName(chart.Path);
                if (string.IsNullOrWhiteSpace(chartDirectory))
                {
                    continue;
                }
                string normalizedDirectory = Path.GetFullPath(chartDirectory);
                if (!successfulDirectoriesByHash.TryGetValue(lookupHash, out HashSet<string> directories))
                {
                    directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    successfulDirectoriesByHash.Add(lookupHash, directories);
                }
                directories.Add(normalizedDirectory);
            }
        }

    }

    /// <summary>operation 開始時 snapshot に success overlay を重ねた install lookup を作成します。</summary>
    internal IInstalledChartLookupIndex CreateSessionSuccessOwnershipOverlay(
        IInstalledChartLookupIndex ownershipBaseline,
        IPrimaryHashLookup hashBaseline = null)
    {
        return new SessionSuccessOwnershipOverlay(ownershipBaseline, hashBaseline);
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

    /// <summary>
    /// auto-install 候補を一つの operation-scoped mutation session へ逐次追加します。
    /// 後続候補の重複判定には、この session で実際に physical success した chart のみを使用します。
    /// </summary>
    /// <param name="workflow">副作用なしに構築済みの auto-install workflow。</param>
    /// <param name="keepInstallablePackagesPending">install 可能 package を pending に残すか。</param>
    /// <param name="canAutoInstallImmediately">現在の library root へ即時導入できるか。</param>
    /// <param name="installPackages">一件分の physical install を session へ append する callback。</param>
    /// <param name="mutationSession">外側 ingress が所有する唯一の install mutation session。</param>
    /// <param name="token">cancellation token。</param>
    /// <returns>session commit 前の auto-install aggregate。</returns>
    internal AutoInstallApplyResult ApplyAutoInstallWorkflowForMutationSession(
        AutoInstallWorkflowResult workflow,
        bool keepInstallablePackagesPending,
        bool canAutoInstallImmediately,
        Func<IEnumerable<ChartPackage>, AutoInstallCandidateApplyResult> installPackages,
        LibraryMutationOwner.LibraryMutationSession mutationSession,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(mutationSession);
        var result = new AutoInstallApplyResult();
        if (workflow == null)
        {
            return result;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var installStopwatch = Stopwatch.StartNew();
        if (workflow.PendingPackagesToRemove.Count > 0)
        {
            result.PendingPackagesToRemove.AddRange(workflow.PendingPackagesToRemove.Where(package => package != null));
            result.InstallRowsToDelete.AddRange(
                workflow.PendingPackagesToRemove
                    .Where(package => package != null && !string.IsNullOrWhiteSpace(package.path))
                    .Select(package => package.path)
                    .Distinct(StringComparer.Ordinal));
        }

        List<ChartPackage> pendingPackagesToAdd = [.. workflow.PendingPackagesToAdd.Where(package => package != null)];
        List<ChartPackage> candidates = [.. workflow.AutoInstallCandidates.Where(package => package != null)];
        if (candidates.Count > 0)
        {
            if (!keepInstallablePackagesPending && canAutoInstallImmediately)
            {
                var successfulHashes = new PrimaryHashGuardLookup(EmptyPrimaryHashLookup.Instance);
                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    if (token.IsCancellationRequested)
                    {
                        pendingPackagesToAdd.AddRange(candidates.Skip(candidateIndex));
                        break;
                    }

                    ChartPackage package = candidates[candidateIndex];
                    try
                    {
                        List<string> packageHashes = [.. (package.ChartEntries ?? [])
                            .Select(entry => ChartLookupKey.GetPrimaryHash(entry?.Chart))
                            .Where(hash => !string.IsNullOrWhiteSpace(hash))
                            .Distinct(StringComparer.OrdinalIgnoreCase)];
                        HashSet<string> duplicateHashes = [.. packageHashes
                            .Where(successfulHashes.ContainsPrimaryHash)];
                        if (duplicateHashes.Count > 0)
                        {
                            ApplyAlreadyInstalledWarning(GetEntriesMatchedByPrimaryHashes(
                                package.ChartEntries,
                                successfulHashes,
                                duplicateHashes));
                            pendingPackagesToAdd.Add(package);
                            continue;
                        }

                        AutoInstallCandidateApplyResult candidateApplyResult = installPackages?.Invoke([package])
                            ?? new AutoInstallCandidateApplyResult([package]);
                        List<ChartPackage> failedPackages = [.. candidateApplyResult.FailedPackages
                            .Where(failedPackage => failedPackage != null)];
                        bool stopped = candidateApplyResult.StoppedByPhysicalFailure;
                        bool failed = failedPackages.Count > 0 || stopped;
                        if (failed)
                        {
                            if (failedPackages.Count > 0)
                            {
                                result.AutoInstallFailures.AddRange(failedPackages);
                            }
                            else
                            {
                                result.AutoInstallFailures.Add(package);
                            }
                            pendingPackagesToAdd.Add(package);
                        }
                        else
                        {
                            result.AutoInstalledPackages.Add(package);
                            foreach (string hash in candidateApplyResult.SuccessfulPrimaryHashes)
                            {
                                successfulHashes.AddPrimaryHash(hash);
                            }
                        }

                        if (stopped)
                        {
                            List<ChartPackage> unprocessedPackages = [.. candidates.Skip(candidateIndex + 1)];
                            pendingPackagesToAdd.AddRange(unprocessedPackages);
                            FileDbMutationReceipt physicalFailureReceipt = candidateApplyResult.PhysicalFailureReceipt;
                            mutationSession.RecordStoppedSuffix(
                                physicalFailureReceipt?.SourcePaths.FirstOrDefault() ?? package.path,
                                physicalFailureReceipt?.DestinationPaths.FirstOrDefault(),
                                physicalFailureReceipt?.Failure ?? new IOException("Auto-install physical mutation failed."),
                                unprocessedPackages.Select(unprocessedPackage =>
                                    new LibraryMutationSessionTarget(unprocessedPackage.path, destinationPath: null)));
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        List<ChartPackage> unprocessedPackages = [.. candidates.Skip(candidateIndex + 1)];
                        result.AutoInstallFailures.Add(package);
                        pendingPackagesToAdd.Add(package);
                        pendingPackagesToAdd.AddRange(unprocessedPackages);
                        RecordUnexpectedInstallSessionFailure(
                            mutationSession,
                            package.path,
                            destinationPath: null,
                            exception,
                            unprocessedPackages.Select(unprocessedPackage =>
                                new LibraryMutationSessionTarget(unprocessedPackage.path, destinationPath: null)));
                        break;
                    }
                }
            }
            else
            {
                pendingPackagesToAdd.AddRange(candidates);
            }
        }
        installStopwatch.Stop();
        result.InstallMs = installStopwatch.ElapsedMilliseconds;

        var applyStopwatch = Stopwatch.StartNew();
        result.PendingPackagesToAdd.AddRange(pendingPackagesToAdd);
        result.InstallRowsToUpsert.AddRange(pendingPackagesToAdd.Where(package => !string.IsNullOrWhiteSpace(package.path)));
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
        plan.IndependentOwnershipLookup = new SessionSuccessOwnershipOverlay(
            installedChartLookup as IInstalledChartLookupIndex);
        plan.MoveGuardLookup = new PrimaryHashGuardLookup(
            installedChartLookup ?? EmptyPrimaryHashLookup.Instance);
        planStopwatch.Stop();
        plan.PlanBuildMs = planStopwatch.ElapsedMilliseconds;
        return plan;
    }

    /// <summary>
    /// 推定導入候補を一つの operation-scoped mutation session へ集約します。
    /// physical success だけを pending lifecycle 成功として収集し、DST clear と collection publication は caller が
    /// session commit 成功後に一度だけ適用します。
    /// </summary>
    /// <param name="plan">副作用なしに確定した候補 plan。</param>
    /// <param name="deletePendingPackageSourceAfterInstall">source cleanup を durable apply 後に許可するか。</param>
    /// <param name="installPackage">一件分の physical install を session へ append する callback。</param>
    /// <param name="createInstalledDisplayPackage">resource-only 成功時の installed 表示 package 作成 callback。</param>
    /// <param name="prepareCleanupOnly">cleanup-only source を prepare する callback。</param>
    /// <param name="mutationSession">外側 ingress が所有する唯一の mutation session。</param>
    /// <param name="logInfo">診断ログ callback。</param>
    /// <param name="countComponentMoveTargets">resource-only component の物理移動対象数を返す callback。</param>
    /// <returns>session commit 前の physical success / failure aggregate。</returns>
    internal PendingInstallBatchResult ExecuteEstimatedInstallBatchPlanForMutationSession(
        PendingInstallBatchPlan plan,
        bool deletePendingPackageSourceAfterInstall,
        Func<PendingInstallBatchItem, PackageInstallExecutionResult> installPackage,
        Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage,
        Func<ChartPackage, PackageInstallSessionMoveResult> prepareCleanupOnly,
        LibraryMutationOwner.LibraryMutationSession mutationSession,
        Action<string> logInfo = null,
        Func<ChartPackage, string, ISet<string>, int> countComponentMoveTargets = null)
    {
        ArgumentNullException.ThrowIfNull(mutationSession);
        var result = new PendingInstallBatchResult();
        if (plan == null)
        {
            return result;
        }
        plan.IndependentOwnershipLookup = plan.IndependentOwnershipLookup
            is SessionSuccessOwnershipOverlay sessionSuccessOwnershipOverlay
                ? sessionSuccessOwnershipOverlay
                : new SessionSuccessOwnershipOverlay(plan.IndependentOwnershipLookup, plan.MoveGuardLookup);

        for (int packageIndex = 0; packageIndex < plan.SelectedPendingPackages.Count; packageIndex++)
        {
            ChartPackage originalPackage = plan.SelectedPendingPackages[packageIndex];
            if (originalPackage == null)
            {
                continue;
            }
            try
            {
                var itemStopwatch = Stopwatch.StartNew();
                PendingInstallBatchItem item = PrepareEstimatedInstallBatchItem(
                    originalPackage,
                    plan.IndependentOwnershipLookup);
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
                    CleanupSourceKind sourceKind = ClassifyCleanupSource(originalPackage);
                    PackageInstallSessionMoveResult cleanupResult = prepareCleanupOnly?.Invoke(originalPackage);
                    if (cleanupResult?.Succeeded == true)
                    {
                        mutationSession.AppendInstalledPackageChange(
                            cleanupResult.ExecutionResult,
                            cleanupResult.PhysicalMutation);
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
                        result.PackagesToClearInstallDestinations.Add(originalPackage);
                        logInfo?.Invoke("estimated_install_cleanup_only_success package=" + originalPackage.path + " kind=" + sourceKind.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        result.CleanupOnlyFailed++;
                        FileDbMutationReceipt failureReceipt = cleanupResult?.FailureReceipt;
                        mutationSession.AppendPackagePhysicalFailure(failureReceipt);
                        logInfo?.Invoke("estimated_install_cleanup_only_failed package=" + originalPackage.path);
                        if (failureReceipt == null || failureReceipt.DestinationTypeConflicts.Count == 0)
                        {
                            mutationSession.RecordStoppedSuffix(
                                failureReceipt?.SourcePaths.FirstOrDefault() ?? originalPackage.path,
                                failureReceipt?.DestinationPaths.FirstOrDefault(),
                                failureReceipt?.Failure ?? new IOException("Package cleanup physical mutation failed."),
                                plan.SelectedPendingPackages
                                    .Skip(packageIndex + 1)
                                    .Where(package => package != null)
                                    .Select(package => new LibraryMutationSessionTarget(package.path, null)));
                            itemStopwatch.Stop();
                            break;
                        }
                    }
                    itemStopwatch.Stop();
                    continue;
                }

                PackageInstallExecutionResult installResult = installPackage?.Invoke(item);
                bool packageSucceeded = installResult != null
                    && installResult.FailedPackages.Count == 0;
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
                    if (item.IsResourceOnlyInstall && createInstalledDisplayPackage != null)
                    {
                        mutationSession.AppendRequiredDurableFinalizer(() =>
                        {
                            ChartPackage installedDisplayPackage = createInstalledDisplayPackage(
                                originalPackage,
                                item.DestinationDirectory);
                            if (installedDisplayPackage != null
                                && installedDisplayPackage.ChartEntries.Count > 0)
                            {
                                result.DeferredInstalledPackages.Add(installedDisplayPackage);
                            }
                        });
                    }
                    result.PendingPackagesToRemove.Add(originalPackage);
                    result.PackagesToClearInstallDestinations.Add(originalPackage);
                }
                itemStopwatch.Stop();
                logInfo?.Invoke(
                    "install_pending_packages_to_estimated_destinations item dst="
                    + item.DestinationDirectory
                    + " packages=1 failedPackages="
                    + (packageSucceeded ? 0 : 1)
                    + " installMs="
                    + installResult?.MoveMs
                    + " totalItemMs="
                    + itemStopwatch.ElapsedMilliseconds);
                if (installResult?.StoppedByPhysicalFailure == true)
                {
                    FileDbMutationReceipt failureReceipt = installResult.PhysicalFailureReceipt;
                    mutationSession.RecordStoppedSuffix(
                        failureReceipt?.SourcePaths.FirstOrDefault() ?? originalPackage.path,
                        failureReceipt?.DestinationPaths.FirstOrDefault() ?? item.DestinationDirectory,
                        failureReceipt?.Failure ?? new IOException("Package physical mutation failed."),
                        plan.SelectedPendingPackages
                            .Skip(packageIndex + 1)
                            .Where(package => package != null)
                            .Select(package => new LibraryMutationSessionTarget(package.path, null)));
                    break;
                }
            }
            catch (Exception exception)
            {
                result.FailedPackages.Add(originalPackage);
                RecordUnexpectedInstallSessionFailure(
                    mutationSession,
                    originalPackage.path,
                    destinationPath: null,
                    exception,
                    plan.SelectedPendingPackages
                        .Skip(packageIndex + 1)
                        .Where(package => package != null)
                        .Select(package => new LibraryMutationSessionTarget(package.path, null)));
                break;
            }
        }
        plan.CleanupOnlyCandidateCount = plan.CleanupOnlyCandidates.Count;
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

        var installWorkPackage = ChartPackage.FromChartEntries(installWorkPackageEntries);
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
    /// operation-scoped install session のため、各 package の physical prepare だけを逐次実行します。
    /// canonical storage apply と source cleanup は <paramref name="mutationSession"/> の commit に集約します。
    /// </summary>
    internal PackageInstallExecutionResult InstallPackagesForMutationSession(
        IEnumerable<ChartPackage> chartPackagesInstall,
        string installationDirectory,
        Func<ChartPackage, string, PackageSourceCleanupPolicy, IPrimaryHashLookup, IInstalledChartLookupIndex, ISet<string>, PackageInstallSessionMoveResult> movePackageFiles,
        LibraryMutationOwner.LibraryMutationSession mutationSession,
        PackageSourceCleanupPolicy sourceCleanupPolicy,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null,
        IPrimaryHashLookup existingHashes = null,
        bool skipInstalledPackageWhenNoBms = false,
        IInstalledChartLookupIndex independentOwnershipLookup = null)
    {
        ArgumentNullException.ThrowIfNull(mutationSession);
        var result = new PackageInstallExecutionResult();
        List<ChartPackage> packages = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        SessionSuccessOwnershipOverlay sessionSuccessOverlay =
            independentOwnershipLookup as SessionSuccessOwnershipOverlay
                ?? new SessionSuccessOwnershipOverlay(independentOwnershipLookup, existingHashes);
        var totalStopwatch = Stopwatch.StartNew();
        var moveStopwatch = Stopwatch.StartNew();

        for (int packageIndex = 0; packageIndex < packages.Count; packageIndex++)
        {
            ChartPackage package = packages[packageIndex];
            HashSet<string> excludedComponentPaths = null;
            excludedComponentPathsByPackage?.TryGetValue(package, out excludedComponentPaths);
            PackageInstallSessionMoveResult moveResult;
            try
            {
                moveResult = movePackageFiles?.Invoke(
                    package,
                    installationDirectory,
                    sourceCleanupPolicy,
                    sessionSuccessOverlay,
                    sessionSuccessOverlay,
                    excludedComponentPaths);
            }
            catch (Exception exception)
            {
                result.FailedPackages.Add(package);
                result.StoppedByPhysicalFailure = true;
                result.PhysicalFailureReceipt = RecordUnexpectedInstallSessionFailure(
                    mutationSession,
                    package.path,
                    installationDirectory,
                    exception,
                    packages.Skip(packageIndex + 1)
                        .Select(item => new LibraryMutationSessionTarget(item.path, installationDirectory)));
                break;
            }

            if (moveResult?.Succeeded == true)
            {
                PackageInstallExecutionResult packageResult = moveResult.ExecutionResult;
                mutationSession.AppendInstalledPackageChange(packageResult, moveResult.PhysicalMutation);
                IReadOnlyList<ChartFile> successfulCharts = packageResult.AddedCharts.Count > 0
                    ? packageResult.AddedCharts
                    : [.. packageResult.AddedEntries
                        .Select(entry => entry?.Chart)
                        .Where(chart => chart != null)];
                sessionSuccessOverlay.AddSuccessfulCharts(successfulCharts);
                result.AddedEntries.AddRange(packageResult.AddedEntries);
                result.AddedCharts.AddRange(successfulCharts);
                if (!(skipInstalledPackageWhenNoBms && packageResult.AddedEntries.Count == 0))
                {
                    result.InstalledPackagesToRegister.Add(package);
                }
                continue;
            }

            result.FailedPackages.Add(package);
            FileDbMutationReceipt failureReceipt = moveResult?.FailureReceipt;
            bool isPreflightRefusal = moveResult?.IsPreflightRefusal == true;
            mutationSession.AppendPackagePhysicalFailure(failureReceipt, isPreflightRefusal);
            if (isPreflightRefusal)
            {
                continue;
            }

            result.StoppedByPhysicalFailure = true;
            result.PhysicalFailureReceipt = failureReceipt;
            mutationSession.RecordStoppedSuffix(
                failureReceipt?.SourcePaths.FirstOrDefault() ?? package.path,
                failureReceipt?.DestinationPaths.FirstOrDefault() ?? installationDirectory,
                failureReceipt?.Failure ?? new IOException("Package physical prepare failed without a terminal receipt."),
                packages.Skip(packageIndex + 1)
                    .Select(item => new LibraryMutationSessionTarget(item.path, installationDirectory)));
            break;
        }

        moveStopwatch.Stop();
        result.MoveMs = moveStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    private static FileDbMutationReceipt RecordUnexpectedInstallSessionFailure(
        LibraryMutationOwner.LibraryMutationSession mutationSession,
        string sourcePath,
        string destinationPath,
        Exception failure,
        IEnumerable<LibraryMutationSessionTarget> unprocessedTargets)
    {
        ArgumentNullException.ThrowIfNull(mutationSession);
        ArgumentNullException.ThrowIfNull(failure);
        var receipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            string.IsNullOrWhiteSpace(sourcePath) ? [] : [sourcePath],
            string.IsNullOrWhiteSpace(destinationPath) ? [] : [destinationPath],
            [],
            [],
            [],
            failure);
        mutationSession.AppendPackagePhysicalFailure(receipt);
        mutationSession.RecordStoppedSuffix(
            sourcePath,
            destinationPath,
            failure,
            unprocessedTargets);
        return receipt;
    }

    /// <summary>
    /// force install の全 package を一つの operation-scoped install session へ追加します。
    /// pending / installed collection と install-destination clear は caller が session commit 後に一括反映します。
    /// </summary>
    internal ForceInstallBatchResult ForceInstallPackagesForMutationSession(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        Func<ChartPackage, bool> confirmNormalInstallOverride,
        Func<IEnumerable<ChartPackage>, IInstalledChartLookupIndex, PackageInstallExecutionResult> installPackages,
        LibraryMutationOwner.LibraryMutationSession mutationSession,
        IInstalledChartLookupIndex baselineOwnershipLookup,
        IPrimaryHashLookup baselineHashLookup,
        Action<string> logInfo = null)
    {
        ArgumentNullException.ThrowIfNull(mutationSession);
        var result = new ForceInstallBatchResult();
        List<ChartPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(package => package != null)];
        result.Requested = requestedPackages.Count;
        var sessionSuccessOverlay = new SessionSuccessOwnershipOverlay(
            baselineOwnershipLookup,
            baselineHashLookup);

        for (int requestedIndex = 0; requestedIndex < requestedPackages.Count; requestedIndex++)
        {
            ChartPackage requestedPackage = requestedPackages[requestedIndex];
            ChartPackage pendingPackage = pendingPackages.FirstOrDefault(package =>
                ReferenceEquals(package, requestedPackage)
                || (!string.IsNullOrWhiteSpace(package.path)
                    && !string.IsNullOrWhiteSpace(requestedPackage.path)
                    && package.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.Skipped++;
                logInfo?.Invoke("force_install_batch skip_not_pending path=" + (requestedPackage.path ?? "(null)"));
                continue;
            }

            bool hasInstallDestination = pendingPackage.ChartEntries.Any(entry =>
                !string.IsNullOrWhiteSpace(entry?.Chart?.InstallDestination));
            if (hasInstallDestination
                && confirmNormalInstallOverride != null
                && !confirmNormalInstallOverride(pendingPackage))
            {
                result.Skipped++;
                logInfo?.Invoke("force_install_batch skipped_by_confirm path=" + (pendingPackage.path ?? "(null)"));
                continue;
            }

            PackageInstallExecutionResult installResult;
            try
            {
                installResult = installPackages?.Invoke([pendingPackage], sessionSuccessOverlay);
            }
            catch (Exception exception)
            {
                result.Processed++;
                result.Failed++;
                RecordUnexpectedInstallSessionFailure(
                    mutationSession,
                    pendingPackage.path,
                    destinationPath: null,
                    exception,
                    requestedPackages
                        .Skip(requestedIndex + 1)
                        .Select(package => new LibraryMutationSessionTarget(package.path, destinationPath: null)));
                logInfo?.Invoke("force_install_batch failed path=" + (pendingPackage.path ?? "(null)"));
                break;
            }

            result.Processed++;
            bool succeeded = installResult != null
                && installResult.FailedPackages.Count == 0
                && !installResult.StoppedByPhysicalFailure;
            if (succeeded)
            {
                result.PendingPackagesToRemove.Add(pendingPackage);
                result.DeferredInstalledPackages.AddRange(
                    installResult.InstalledPackagesToRegister.Where(package => package != null));
                result.PackagesToClearInstallDestinations.Add(pendingPackage);
                result.Succeeded++;
                logInfo?.Invoke("force_install_batch success path=" + (pendingPackage.path ?? "(null)"));
            }
            else
            {
                result.Failed++;
                logInfo?.Invoke("force_install_batch failed path=" + (pendingPackage.path ?? "(null)"));
            }

            if (installResult?.StoppedByPhysicalFailure == true)
            {
                FileDbMutationReceipt failureReceipt = installResult.PhysicalFailureReceipt;
                mutationSession.RecordStoppedSuffix(
                    failureReceipt?.SourcePaths.FirstOrDefault() ?? pendingPackage.path,
                    failureReceipt?.DestinationPaths.FirstOrDefault(),
                    failureReceipt?.Failure ?? new IOException("Force-install physical mutation failed."),
                    requestedPackages
                        .Skip(requestedIndex + 1)
                        .Select(package => new LibraryMutationSessionTarget(package.path, destinationPath: null)));
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// installed-only resource overwrite を一つの operation-scoped install session へ集約します。
    /// chart 追加が 0 件でも resource physical success と package lifecycle facts を保持します。
    /// </summary>
    internal PendingResourceOverwriteExecutionResult ExecuteInstalledOnlyResourceOverwriteForMutationSession(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        bool deletePendingPackageSourceAfterInstall,
        Func<ChartPackage, InstalledOnlyPackageResolutionResult> resolveDestination,
        Func<InstalledOnlyPackageResolutionResult, ChartPackage, string> describeSkipDetail,
        Func<ChartPackage, string, bool> hasResourceOverwriteTargets,
        Func<ChartPackage, string, PackageInstallExecutionResult> installPackage,
        Func<ChartPackage, PackageInstallSessionMoveResult> prepareCleanupOnly,
        Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage,
        LibraryMutationOwner.LibraryMutationSession mutationSession,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        ArgumentNullException.ThrowIfNull(mutationSession);
        var result = new PendingResourceOverwriteExecutionResult();
        List<ChartPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(package => package != null)];
        result.Requested = requestedPackages.Count;

        for (int requestedIndex = 0; requestedIndex < requestedPackages.Count; requestedIndex++)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }

            ChartPackage requestedPackage = requestedPackages[requestedIndex];
            ChartPackage pendingPackage = pendingPackages.FirstOrDefault(package =>
                ReferenceEquals(package, requestedPackage)
                || (!string.IsNullOrWhiteSpace(package.path)
                    && !string.IsNullOrWhiteSpace(requestedPackage.path)
                    && package.path.Equals(requestedPackage.path, StringComparison.OrdinalIgnoreCase)));
            if (pendingPackage == null)
            {
                result.SkippedNotPending++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_resource_overwrite skip_not_pending path=" + requestedPackage.path);
                onEachProcessed?.Invoke();
                continue;
            }

            try
            {
                InstalledOnlyPackageResolutionResult resolution = resolveDestination?.Invoke(pendingPackage)
                    ?? new InstalledOnlyPackageResolutionResult();
                if (!resolution.Success)
                {
                    if (resolution.Reason == InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories
                        || resolution.Reason == InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories)
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

                string destinationDirectory = resolution.DestinationDirectory;
                logInfo?.Invoke(
                    "advanced_pending_resource_overwrite resolve_selected path="
                    + pendingPackage.path
                    + " dst="
                    + destinationDirectory
                    + " charts="
                    + pendingPackage.ChartEntries.Count);

                if (!hasResourceOverwriteTargets(pendingPackage, destinationDirectory))
                {
                    if (!deletePendingPackageSourceAfterInstall)
                    {
                        result.SkippedNoComponentTarget++;
                        result.Processed++;
                        logInfo?.Invoke("advanced_pending_resource_overwrite skip_no_component_target path=" + pendingPackage.path);
                        onEachProcessed?.Invoke();
                        continue;
                    }

                    CleanupSourceKind sourceKind = ClassifyCleanupSource(pendingPackage);
                    PackageInstallSessionMoveResult cleanupResult = prepareCleanupOnly?.Invoke(pendingPackage);
                    if (cleanupResult?.Succeeded == true)
                    {
                        mutationSession.AppendInstalledPackageChange(
                            cleanupResult.ExecutionResult,
                            cleanupResult.PhysicalMutation);
                        result.SucceededCleanupOnly++;
                        result.PendingPackagesToRemove.Add(pendingPackage);
                        result.PackagesToClearInstallDestinations.Add(pendingPackage);
                        if (!string.IsNullOrWhiteSpace(pendingPackage.path))
                        {
                            result.InstallRowsToDelete.Add(pendingPackage.path);
                        }
                        logInfo?.Invoke(
                            "advanced_pending_resource_overwrite cleanup_only_success path="
                            + pendingPackage.path
                            + " kind="
                            + sourceKind.ToString().ToLowerInvariant());
                    }
                    else
                    {
                        result.Failed++;
                        FileDbMutationReceipt failureReceipt = cleanupResult?.FailureReceipt;
                        mutationSession.AppendPackagePhysicalFailure(failureReceipt);
                        logInfo?.Invoke("advanced_pending_resource_overwrite cleanup_only_failed path=" + pendingPackage.path);
                        if (failureReceipt == null || failureReceipt.DestinationTypeConflicts.Count == 0)
                        {
                            mutationSession.RecordStoppedSuffix(
                                failureReceipt?.SourcePaths.FirstOrDefault() ?? pendingPackage.path,
                                failureReceipt?.DestinationPaths.FirstOrDefault(),
                                failureReceipt?.Failure ?? new IOException("Package cleanup physical mutation failed."),
                                requestedPackages
                                    .Skip(requestedIndex + 1)
                                    .Select(package => new LibraryMutationSessionTarget(package.path, destinationPath: null)));
                            result.Processed++;
                            onEachProcessed?.Invoke();
                            break;
                        }
                    }
                    result.Processed++;
                    onEachProcessed?.Invoke();
                    continue;
                }

                PackageInstallExecutionResult installResult = installPackage?.Invoke(
                    pendingPackage,
                    destinationDirectory);
                bool succeeded = installResult != null
                    && installResult.FailedPackages.Count == 0
                    && !installResult.StoppedByPhysicalFailure;
                if (succeeded)
                {
                    result.SucceededInstall++;
                    result.PendingPackagesToRemove.Add(pendingPackage);
                    result.PackagesToClearInstallDestinations.Add(pendingPackage);
                    if (!string.IsNullOrWhiteSpace(pendingPackage.path))
                    {
                        result.InstallRowsToDelete.Add(pendingPackage.path);
                    }
                    if (createInstalledDisplayPackage != null)
                    {
                        mutationSession.AppendRequiredDurableFinalizer(() =>
                        {
                            ChartPackage installedDisplayPackage = createInstalledDisplayPackage(
                                pendingPackage,
                                destinationDirectory);
                            if (installedDisplayPackage != null
                                && installedDisplayPackage.ChartEntries.Count > 0)
                            {
                                result.DeferredInstalledPackages.Add(installedDisplayPackage);
                            }
                        });
                    }
                    logInfo?.Invoke(
                        "advanced_pending_resource_overwrite install_success path="
                        + pendingPackage.path
                        + " dst="
                        + destinationDirectory);
                }
                else
                {
                    result.Failed++;
                    logInfo?.Invoke(
                        "advanced_pending_resource_overwrite install_failed path="
                        + pendingPackage.path
                        + " dst="
                        + destinationDirectory);
                }

                result.Processed++;
                onEachProcessed?.Invoke();
                if (installResult?.StoppedByPhysicalFailure == true)
                {
                    FileDbMutationReceipt failureReceipt = installResult.PhysicalFailureReceipt;
                    mutationSession.RecordStoppedSuffix(
                        failureReceipt?.SourcePaths.FirstOrDefault() ?? pendingPackage.path,
                        failureReceipt?.DestinationPaths.FirstOrDefault() ?? destinationDirectory,
                        failureReceipt?.Failure ?? new IOException("Package resource-overwrite physical mutation failed."),
                        requestedPackages
                            .Skip(requestedIndex + 1)
                            .Select(package => new LibraryMutationSessionTarget(package.path, destinationPath: null)));
                    break;
                }
            }
            catch (Exception exception)
            {
                result.Failed++;
                result.Processed++;
                onEachProcessed?.Invoke();
                RecordUnexpectedInstallSessionFailure(
                    mutationSession,
                    pendingPackage.path,
                    destinationPath: null,
                    exception,
                    requestedPackages
                        .Skip(requestedIndex + 1)
                        .Select(package => new LibraryMutationSessionTarget(package.path, destinationPath: null)));
                break;
            }
        }

        return result;
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
