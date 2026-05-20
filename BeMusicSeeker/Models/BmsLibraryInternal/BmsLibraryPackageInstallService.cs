using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
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

    public ComponentMoveDecision DecideComponentMove(string srcFilePath, string dstFilePath)
    {
        if (!File.Exists(dstFilePath))
        {
            return ComponentMoveDecision.Move;
        }
        var sourceInfo = new FileInfo(srcFilePath);
        var destinationInfo = new FileInfo(dstFilePath);
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
            foreach (string path in Directory.EnumerateFiles(installComponentFile, "*", System.IO.SearchOption.AllDirectories))
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
        if (!string.IsNullOrWhiteSpace(sourceRootPath) && Directory.Exists(sourceRootPath))
        {
            string normalizedRootDirectory = sourceRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relativePath = chartPath.StartsWith(normalizedRootDirectory, StringComparison.OrdinalIgnoreCase)
                ? chartPath.Substring(normalizedRootDirectory.Length)
                : Path.GetFileName(chartPath);
            return Path.Combine(destinationDirectory, relativePath);
        }
        return Path.Combine(destinationDirectory, Path.GetFileName(chartPath));
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

    public List<BMSFile> DeduplicateFilesByPathOrReference(IEnumerable<BMSFile> files)
    {
        List<BMSFile> deduplicated = [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> references = [];
        foreach (BMSFile file in files ?? [])
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
            && BMSFile.bmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBmsFormatChartEntry(PackageChartEntry entry)
    {
        ChartFile chart = entry?.Chart;
        string extension = Path.GetExtension(chart?.Path);
        return chart?.Kind == ChartFileKind.Bms
            && !string.IsNullOrWhiteSpace(extension)
            && BMSFile.bmsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public List<BMSFile> GetPendingBmsFormatChartFilesSnapshot(IEnumerable<ChartPackage> pendingPackages)
    {
        return DeduplicateFilesByPathOrReference((pendingPackages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Where(IsBmsFormatChartEntry)
            .Select(entry => entry?.Chart?.BmsFile)
            .Where(IsBmsFormatChartFile));
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
            fileSystemEntries = [.. Directory.EnumerateFileSystemEntries(dirfullpath, "*", System.IO.SearchOption.TopDirectoryOnly)];
        }
        catch
        {
            return result;
        }
        if (fileSystemEntries == null || !fileSystemEntries.Any())
        {
            return result;
        }
        List<string> files = [.. fileSystemEntries.Where(File.Exists)];
        List<string> directories = [.. fileSystemEntries.Where(Directory.Exists)];
        List<string> chartFiles = [.. files.Where(file => ChartFileKindResolver.IsSupportedChartFilePath(file) && File.Exists(file))];
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
            if (entry == null)
            {
                return null;
            }
            if (ChartFileKindResolver.IsBmsChartFile(entry))
            {
                entry.SetHealthStatus(forceUpdate: false, memClear: false);
            }
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
        return PackageChartEntry.FromBmsFile(CreateBmsChartForDiscovery(filePath));
    }

    private static bool HasExistingPackageChartResources(PackageChartEntry entry)
    {
        if (entry?.Chart == null)
        {
            return false;
        }
        BMSFile file = entry.Chart.BmsFile;
        if (file != null)
        {
            return file.maintenanceInfo.wav_files_existing > 0
                || file.maintenanceInfo.bga_files_existing > 0
                || file.maintenanceInfo.movie_files_existing > 0;
        }
        string chartDirectory = DirectoryExt.GetDirectoryNameSimple(entry.Chart.Path);
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return false;
        }
        ChartResourceSnapshot resources = entry.ResourceSnapshot;
        return HasExistingResourceFile(chartDirectory, resources.AudioReferences, BMSFile.wavExtensions)
            || HasExistingResourceFile(chartDirectory, resources.VisualReferences, BMSFile.bgaImageExtensions)
            || HasExistingResourceFile(chartDirectory, resources.MovieReferences, BMSFile.bgaMovieExtensions);
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

    internal static IReadOnlyList<ChartWarning> BuildPendingResourceHealthWarnings(PackageChartEntry entry, Func<BMSFile, bool> requiresPendingWarning)
    {
        if (entry?.Chart == null || entry.ResourceSnapshot.TotalReferenceCount <= 0)
        {
            return [];
        }

        ChartFile chart = entry.Chart;
        if (chart.Kind == ChartFileKind.Bmson)
        {
            BMSFileMaintenanceInfo bmsonMaintenanceInfo = BmsLibraryMaintenanceService.GetResourceHealthMaintenanceInfo(chart);
            return bmsonMaintenanceInfo == null
                ? []
                : BmsLibraryMaintenanceService.BuildResourceHealthWarnings(bmsonMaintenanceInfo);
        }

        BMSFile file = chart.BmsFile;
        if (file != null)
        {
            file.SetHealthStatus(forceUpdate: false, memClear: false);
            if (requiresPendingWarning != null)
            {
                return requiresPendingWarning(file)
                    ? [.. file.Warnings.ToStructuredList().Where(warning => warning?.Category == ChartWarningCategory.ResourceHealth)]
                    : [];
            }
            return BmsLibraryMaintenanceService.BuildResourceHealthWarnings(file.maintenanceInfo);
        }
        BMSFileMaintenanceInfo maintenanceInfo = BmsLibraryMaintenanceService.GetResourceHealthMaintenanceInfo(chart);
        if (maintenanceInfo == null)
        {
            return [];
        }
        return BmsLibraryMaintenanceService.BuildResourceHealthWarnings(maintenanceInfo);
    }

    private static bool HasExistingResourceFile(string chartDirectory, IEnumerable<ChartResourceSnapshot.ResourceReference> references, IEnumerable<string> extensions)
    {
        foreach (ChartResourceSnapshot.ResourceReference reference in references ?? [])
        {
            if (string.IsNullOrWhiteSpace(reference.NormalizedPath))
            {
                continue;
            }
            if (File.Exists(Path.Combine(chartDirectory, reference.NormalizedPath)))
            {
                return true;
            }
            foreach (string extension in extensions ?? [])
            {
                if (!string.IsNullOrWhiteSpace(extension)
                    && File.Exists(Path.Combine(chartDirectory, Path.ChangeExtension(reference.NormalizedPath, extension))))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static ChartPackage CreatePackageWithKnownCharts(string packagePath, bool deleteParent, IEnumerable<PackageChartEntry> knownChartEntries)
    {
        List<PackageChartEntry> knownEntries = [.. (knownChartEntries ?? [])
            .Where(entry => entry?.Chart != null)];
        if (Directory.Exists(packagePath))
        {
            List<PackageChartEntry> recursiveEntries = [.. PackageInstallEstimationSnapshotBuilder
                .BuildPackageChartDiscoverySnapshot(packagePath, useEverythingForPendingPackageSourceScan: false)
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
                path = packagePath,
                delete_parent = deleteParent
            };
        }

        ChartPackage package = ChartPackage.FromChartEntries(knownEntries);
        package.path = packagePath;
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
        if (package == null || string.IsNullOrWhiteSpace(package.path) || !Directory.Exists(package.path))
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
        if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !Directory.Exists(tempDirectoryPath))
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
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
        catch (Exception cleanupException)
        {
            logInfo?.Invoke("auto_install cleanup_failed path=" + tempDirectoryPath + " error=" + cleanupException.Message);
        }
    }

    public List<string> ExpandInstallSources(
        IEnumerable<string> installPaths,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInfo = null,
        IBmsLibraryDialogService dialogService = null,
        Action onEachSourceProcessed = null,
        CancellationToken token = default)
    {
        string[] archiveExtensions = [".zip", ".7z", ".rar", ".lzh"];
        List<string> expandedPaths = [];
        foreach (string installPath in installPaths ?? [])
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            string extractedTempDirectoryPath = null;
            try
            {
                if (!archiveExtensions.Any(ext => installPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    expandedPaths.Add(installPath);
                }
                else
                {
                    extractedTempDirectoryPath = TempDirectoryPublisher.Get();
                    foreach (ArchiveEntryMetadata entry in SevenZipArchiveExtractor.ExtractArchiveEntries(installPath, extractedTempDirectoryPath))
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

                        bool isFile = !entry.IsFolder && File.Exists(fullPath);
                        bool isDirectory = entry.IsFolder && Directory.Exists(fullPath);
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
                                    File.SetLastAccessTime(fullPath, entry.LastAccessTime);
                                }
                                else
                                {
                                    Directory.SetLastAccessTime(fullPath, entry.LastAccessTime);
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
                }
            }
            catch (RequiredArchiveMetadataRestoreException metadataRestoreException)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logInfo);
                dialogService?.Show(
                    string.Format(Resources.Warn_ArchiveTimestampRestoreFailed, installPath, metadataRestoreException.RestoredPath, metadataRestoreException.Message),
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            catch (Exception ex)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logInfo);
                logInfo?.Invoke("auto_install extract_failed path=" + installPath + " error=" + ex.Message);
                dialogService?.Show(
                    string.Format(Resources.Warn_ArchiveExtractFailed, installPath, ex.Message),
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            finally
            {
                onEachSourceProcessed?.Invoke();
            }
        }
        return expandedPaths;
    }

    public bool MovePackageFiles(
        ChartPackage package,
        string installationDirectory,
        BmsLibraryOptionsSnapshot options,
        Func<IEnumerable<ChartFile>, string, string, string> createFolderPath,
        Func<Exception, string> getDisplayedExceptionMessage,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        Action<string> logInstallPerformance,
        bool showMessageBoxOnInstallFail = true,
        bool deleteAllContents = false,
        HashSet<string> existingHashes = null,
        ISet<string> excludedComponentPaths = null)
    {
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        string sourcePath = package.path;
        string destinationDirectory = string.Empty;
        bool isSingleFile = false;
        bool isAutoNaming = false;
        List<string> installComponentFiles = [];
        if (File.Exists(sourcePath))
        {
            installComponentFiles.Add(sourcePath);
            isSingleFile = true;
        }
        else
        {
            if (!Directory.Exists(sourcePath))
            {
                return false;
            }
            installComponentFiles = [.. Directory.EnumerateFileSystemEntries(sourcePath)];
            isAutoNaming = string.IsNullOrWhiteSpace(installationDirectory);
        }

        var installComponentPathSet = new HashSet<string>(installComponentFiles, StringComparer.OrdinalIgnoreCase);
        List<PackageChartEntry> installTargetEntries = SelectInstallTargetEntries(package, installComponentPathSet, sourcePath, isSingleFile);
        var installChartPathSet = new HashSet<string>(installTargetEntries.Select(entry => entry?.Chart?.Path).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        installComponentFiles = [.. installComponentFiles.Where(path => !installChartPathSet.Contains(path))];
        ISet<string> componentExclusionPaths = BuildComponentExclusionSet(excludedComponentPaths, installTargetEntries);
        if (componentExclusionPaths.Count > 0)
        {
            installComponentFiles = [.. installComponentFiles.Where(path => !componentExclusionPaths.Contains(path))];
        }

        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            HashSet<string> hashSnapshot = existingHashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PackageChartEntry> skippedEntries = [.. installTargetEntries
                .Where(delegate (PackageChartEntry entry)
                {
                    string lookupKey = entry?.Chart?.PrimaryLookupHash;
                    return !string.IsNullOrWhiteSpace(lookupKey) && hashSnapshot.Contains(lookupKey);
                })];
            if (skippedEntries.Count > 0)
            {
                var skipPathSet = new HashSet<string>(skippedEntries.Select(entry => entry?.Chart?.Path).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
                installTargetEntries = [.. installTargetEntries.Where(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path) && !skipPathSet.Contains(entry.Chart.Path))];
                package.RemoveChartEntries(entry =>
                    !string.IsNullOrWhiteSpace(entry?.Chart?.Path)
                    && skipPathSet.Contains(entry.Chart.Path));
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                destinationDirectory = createFolderPath?.Invoke(
                    installTargetEntries.Select(entry => entry.Chart),
                    options?.BMSInstallDir,
                    installComponentFiles.Select(Path.GetFileName).OrderByDescending(fileName => fileName.Length).FirstOrDefault() ?? string.Empty);
            }
            else
            {
                destinationDirectory = installationDirectory;
            }

            if (string.IsNullOrWhiteSpace(installationDirectory))
            {
                int dirSuffix = 1;
                string baseDirName = destinationDirectory;
                while (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                {
                    dirSuffix++;
                    destinationDirectory = baseDirName + "(" + dirSuffix + ")";
                }
                if (!isAutoNaming)
                {
                    fileMutationService.EnsureDirectory(destinationDirectory, targetOnlyFileMutationOptions);
                }
            }

            if (isAutoNaming)
            {
                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException(string.Format(Resources.Error_RenameDestDirNotFound, sourcePath));
                }
                fileMutationService.MoveDirectory(sourcePath, destinationDirectory, overwrite: true, recursiveDirectoryTreeFileMutationOptions);
            }
            else
            {
                if (options?.EnableSmartComponentOverwrite == true)
                {
                    MovePackageComponentsSmart(
                        package,
                        installComponentFiles,
                        destinationDirectory,
                        componentExclusionPaths,
                        options.KeepSmartOverwriteProtectedFilesByRenaming,
                        fileMutationService,
                        targetOnlyFileMutationOptions,
                        logInstallPerformance);
                }
                else
                {
                    ComponentMovePlanBuildResult movePlanResult = BuildComponentMovePlan(installComponentFiles, destinationDirectory, componentExclusionPaths);
                    movePlanResult.PlanItems.AsParallel().ForAll(delegate (ComponentMovePlanItem planItem)
                    {
                        string componentDestinationDirectory = Path.GetDirectoryName(planItem.DestinationPath);
                        if (!string.IsNullOrWhiteSpace(componentDestinationDirectory))
                        {
                            fileMutationService.EnsureDirectory(componentDestinationDirectory, targetOnlyFileMutationOptions);
                        }
                        fileMutationService.MoveFile(planItem.SourcePath, planItem.DestinationPath, overwrite: true, targetOnlyFileMutationOptions);
                    });
                    CleanupEmptyComponentDirectories(installComponentFiles, fileMutationService, targetOnlyFileMutationOptions);
                }

                foreach (PackageChartEntry entry in installTargetEntries)
                {
                    ChartFile chart = entry?.Chart;
                    string destinationBmsPath = BuildDestinationChartPath(sourcePath, destinationDirectory, chart);
                    string destinationBmsDirectory = Path.GetDirectoryName(destinationBmsPath);
                    if (!string.IsNullOrWhiteSpace(destinationBmsDirectory))
                    {
                        fileMutationService.EnsureDirectory(destinationBmsDirectory, targetOnlyFileMutationOptions);
                    }
                    while (File.Exists(destinationBmsPath) || Directory.Exists(destinationBmsPath))
                    {
                        string renamedFileName = Path.GetFileNameWithoutExtension(destinationBmsPath) + "_" + Path.GetExtension(destinationBmsPath);
                        destinationBmsPath = Path.Combine(Path.GetDirectoryName(destinationBmsPath) ?? destinationDirectory, Path.GetFileName(renamedFileName));
                    }
                    if (!File.Exists(chart?.Path))
                    {
                        throw new FileNotFoundException(Resources.Error_FileNotFound, chart?.Path);
                    }
                    fileMutationService.MoveFile(chart.Path, destinationBmsPath, overwrite: true, targetOnlyFileMutationOptions);
                    entry.ApplyInstalledPath(destinationBmsPath);
                }
                CleanupEmptyComponentDirectories(installComponentFiles, fileMutationService, targetOnlyFileMutationOptions);
            }
        }
        catch (Exception ex)
        {
            if (!showMessageBoxOnInstallFail)
            {
                return false;
            }
            dialogService?.Show(
                string.Format(Resources.Error_InstallFailed, package.path, destinationDirectory, getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message),
                Resources.MessageBoxTitle_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK);
            return false;
        }

        if (isSingleFile)
        {
            package.ApplySingleFileInstallDestination(destinationDirectory, installTargetEntries);
            package.path = Path.Combine(destinationDirectory, Path.GetFileName(package.path));
            package.ReplaceChartEntries(installTargetEntries);
        }
        else
        {
            if (!(Directory.Exists(sourcePath) || isAutoNaming))
            {
                return false;
            }
            package.ApplyDirectoryInstallDestination(sourcePath, destinationDirectory, installTargetEntries);
            package.path = destinationDirectory;
            package.ReplaceChartEntries(installTargetEntries);
        }

        string directoryToDelete = string.Empty;
        if (package.delete_parent)
        {
            directoryToDelete = Path.GetDirectoryName(sourcePath);
        }
        else if (Directory.Exists(sourcePath))
        {
            directoryToDelete = sourcePath;
        }
        if (!string.IsNullOrWhiteSpace(directoryToDelete) && Directory.Exists(directoryToDelete))
        {
            string folderDeletionDecisionReason = deleteAllContents
                ? string.Empty
                : "not_empty deleteAllContents=False";
            if (deleteAllContents)
            {
                if (!TryBuildInstalledHashSnapshotForSafeCleanup(existingHashes, package.ChartEntries, out HashSet<string> installedHashesForCleanup, out folderDeletionDecisionReason))
                {
                    logInstallPerformance?.Invoke("Folder deletion skipped: path=" + directoryToDelete + " reason=" + folderDeletionDecisionReason);
                    return true;
                }

                // NOTE:
                // delete_parent は探索時の親候補フラグに過ぎないため、通常インストールでは
                // 実際に残ったファイルを見て「空」または「既所持譜面のみ」の場合にだけ再帰削除します。
                if (!CanDeleteDirectoryAfterInstall(directoryToDelete, installedHashesForCleanup, out folderDeletionDecisionReason))
                {
                    logInstallPerformance?.Invoke("Folder deletion skipped: path=" + directoryToDelete + " reason=" + folderDeletionDecisionReason);
                    return true;
                }
            }
            else if (HasRemainingDirectoryEntries(directoryToDelete))
            {
                logInstallPerformance?.Invoke("Folder deletion skipped: path=" + directoryToDelete + " reason=" + folderDeletionDecisionReason);
                return true;
            }

            FileMutationOptions deleteOptions = deleteAllContents ? recursiveDirectoryTreeFileMutationOptions : targetOnlyFileMutationOptions;
            try
            {
                fileMutationService.DeleteDirectoryDirect(directoryToDelete, deleteAllContents, deleteOptions);
                logInstallPerformance?.Invoke("Folder deletion success: path=" + directoryToDelete + " deleteAllContents=" + deleteAllContents + " reason=" + folderDeletionDecisionReason);
            }
            catch (FileMutationException ex)
            {
                if (!deleteAllContents && HasRemainingDirectoryEntries(directoryToDelete))
                {
                    logInstallPerformance?.Invoke("Folder deletion skipped after failure: path=" + directoryToDelete + " reason=not_empty_after_failure deleteAllContents=False attempts=" + ex.AttemptCount);
                    return true;
                }

                logInstallPerformance?.Invoke(string.Format(
                    "Folder deletion failed: path={0} attempts={1} normalizedReadOnly={2} win32={3} errorType={4} error={5}",
                    directoryToDelete,
                    ex.AttemptCount,
                    ex.NormalizedReadOnlyCount,
                    ex.Win32ErrorCode,
                    ex.RootCause.GetType().FullName,
                    getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message));
                dialogService?.Show(
                    string.Format(Resources.Error_FolderDeleteFailed, directoryToDelete, getDisplayedExceptionMessage?.Invoke(ex) ?? ex.Message),
                    Resources.MessageBoxTitle_Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK);
            }
        }
        return true;
    }

    private static bool TryBuildInstalledHashSnapshotForSafeCleanup(HashSet<string> existingHashes, IEnumerable<PackageChartEntry> installedPackageEntries, out HashSet<string> installedHashes, out string reason)
    {
        installedHashes = existingHashes != null
            ? new HashSet<string>(existingHashes, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry installedPackageEntry in installedPackageEntries ?? [])
        {
            if (installedPackageEntry?.Chart == null)
            {
                continue;
            }
            string lookupKey = installedPackageEntry.Chart.PrimaryLookupHash;
            if (string.IsNullOrWhiteSpace(lookupKey))
            {
                reason = "current_package_hash_unavailable path=" + installedPackageEntry.Chart.Path;
                return false;
            }
            installedHashes.Add(lookupKey);
        }
        reason = "safe_cleanup_allowed pending_installed_hashes=" + installedHashes.Count;
        return true;
    }

    private static bool CanDeleteDirectoryAfterInstall(string directoryPath, ISet<string> installedHashes, out string reason)
    {
        List<string> remainingFiles;
        try
        {
            remainingFiles = [.. Directory.EnumerateFiles(directoryPath, "*", System.IO.SearchOption.AllDirectories)];
        }
        catch (Exception ex)
        {
            reason = "remaining_scan_failed path=" + directoryPath + " errorType=" + ex.GetType().FullName + " error=" + ex.Message;
            return false;
        }

        if (remainingFiles.Count == 0)
        {
            reason = "safe_cleanup_allowed no_remaining_files";
            return true;
        }

        foreach (string remainingFilePath in remainingFiles)
        {
            if (!IsSupportedChartFilePath(remainingFilePath))
            {
                reason = "remaining_non_chart_file path=" + remainingFilePath;
                return false;
            }

            if (!TryGetRemainingChartLookupKey(remainingFilePath, out string remainingLookupKey, out reason))
            {
                return false;
            }

            if (installedHashes == null || !installedHashes.Contains(remainingLookupKey))
            {
                reason = "remaining_chart_not_installed path=" + remainingFilePath + " hash=" + remainingLookupKey;
                return false;
            }
        }

        reason = "safe_cleanup_allowed remaining_files_all_installed_charts count=" + remainingFiles.Count;
        return true;
    }

    private static bool TryGetRemainingChartLookupKey(string remainingFilePath, out string lookupKey, out string reason)
    {
        lookupKey = null;
        reason = null;
        try
        {
            lookupKey = ChartFileKindResolver.IsBmsonFilePath(remainingFilePath)
                ? ChartLookupKey.GetPrimaryHash(BmsonSongParser.Parse(remainingFilePath))
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

    public AutoInstallWorkflowResult PrepareAutoInstallWorkflow(
        IEnumerable<string> installPaths,
        IEnumerable<ChartPackage> currentPendingPackages,
        IEnumerable<string> knownChartDirectories,
        Func<ChartFile, bool> isInstalledChart,
        double dupRateThreshInOnePkg,
        Func<BMSFile, bool> requiresPendingWarning,
        CancellationToken token = default)
    {
        var result = new AutoInstallWorkflowResult();
        var totalStopwatch = Stopwatch.StartNew();
        var discoveryStopwatch = Stopwatch.StartNew();
        List<string> normalizedInstallPaths = [.. (installPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
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
            if (string.IsNullOrWhiteSpace(paths.Key) || !Directory.Exists(paths.Key))
            {
                continue;
            }
            List<string> files = [.. paths.Where(File.Exists)];
            List<string> directories = [.. paths.Where(Directory.Exists)];
            List<string> chartFiles = [.. files.Where(file => ChartFileKindResolver.IsSupportedChartFilePath(file) && File.Exists(file))];
            string[] topEntries = Directory.GetFileSystemEntries(paths.Key, "*", System.IO.SearchOption.TopDirectoryOnly);
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

        List<string> libraryDirectories = [.. (knownChartDirectories ?? []).Where(dir => !string.IsNullOrWhiteSpace(dir))];
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        discoveredPackages = [.. discoveredPackages
            .Where(pkg => pkg != null && !libraryDirectories.Any(dir => pkg.path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(newPkg => !pendingPackages.Any(oldPkg => !string.IsNullOrWhiteSpace(oldPkg.path) && newPkg.path.Equals(oldPkg.path, StringComparison.OrdinalIgnoreCase)))
            .Where(newPkg => !pendingPackages.Any(oldPkg => !string.IsNullOrWhiteSpace(oldPkg.path) && Directory.Exists(oldPkg.path) && newPkg.path.StartsWith(oldPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))];
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

        if (discoveredPackages.Any(newPkg => !string.IsNullOrWhiteSpace(newPkg.path) && Directory.Exists(newPkg.path)))
        {
            List<ChartPackage> pendingPackagesToRemove = [.. pendingPackages.Where(delegate (ChartPackage oldPkg)
            {
                IEnumerable<ChartPackage> sourcePackages = discoveredPackages.Where(newPkg => !string.IsNullOrWhiteSpace(newPkg.path) && Directory.Exists(newPkg.path));
                string dir;
                if (Directory.Exists(oldPkg.path))
                {
                    dir = oldPkg.path + Path.DirectorySeparatorChar;
                }
                else
                {
                    if (!File.Exists(oldPkg.path))
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
        foreach (ChartPackage pkg in discoveredPackages)
        {
            bool hasInstalledChart = false;
            foreach (PackageChartEntry entry in pkg.ChartEntries)
            {
                if (isInstalledChart != null && isInstalledChart(entry?.Chart))
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
            if (pendingByPackage[pkg])
            {
                continue;
            }
            bool isSingleFilePackage = !Directory.Exists(pkg.path);
            foreach (PackageChartEntry entry in pkg.ChartEntries)
            {
                if (isSingleFilePackage)
                {
                    bool isBmson = entry.Chart?.Kind == ChartFileKind.Bmson;
                    entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                    entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
                    pendingByPackage[pkg] = true;
                    break;
                }
                bool hasDefinedResources = entry?.ResourceSnapshot.TotalReferenceCount > 0;
                if (!hasDefinedResources)
                {
                    continue;
                }
                IReadOnlyList<ChartWarning> resourceWarnings = BuildPendingResourceHealthWarnings(entry, requiresPendingWarning);
                if (resourceWarnings.Count > 0)
                {
                    entry.ReplaceWarningsByCategory(ChartWarningCategory.ResourceHealth, resourceWarnings);
                    pendingByPackage[pkg] = true;
                    break;
                }
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

    public AutoInstallApplyResult ApplyAutoInstallWorkflow(
        AutoInstallWorkflowResult workflow,
        bool keepInstallablePackagesPending,
        bool canAutoInstallImmediately,
        Func<IEnumerable<ChartPackage>, List<ChartPackage>> installPackages,
        CancellationToken token = default)
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
                List<ChartPackage> failedPackages = installPackages?.Invoke(workflow.AutoInstallCandidates) ?? [];
                var failedSet = new HashSet<ChartPackage>(failedPackages);
                result.AutoInstallFailures.AddRange(failedPackages.Where(pkg => pkg != null));
                result.AutoInstalledPackages.AddRange(workflow.AutoInstallCandidates.Where(pkg => pkg != null && !failedSet.Contains(pkg)));
                pendingPackagesToAdd = [.. pendingPackagesToAdd, .. failedPackages];
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
            delta.InstallPathsToDelete = [.. currentPending.Where(pkg => !string.IsNullOrWhiteSpace(pkg.path)).Select(pkg => pkg.path).Distinct(StringComparer.OrdinalIgnoreCase)];
            return delta;
        }
        var removedPackages = new HashSet<ChartPackage>((packagesToRemove ?? []).Where(pkg => pkg != null));
        var removedPackagePaths = new HashSet<string>((packagesToRemove ?? []).Where(pkg => pkg != null && !string.IsNullOrWhiteSpace(pkg.path)).Select(pkg => pkg.path), StringComparer.OrdinalIgnoreCase);
        var removedPaths = new HashSet<string>((chartPathsToRemove ?? []).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        bool removeFiles = removedPaths.Count > 0;
        var installPathsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            package.ReplaceChartEntries(remainingEntries);
            delta.RemainingPackages.Add(package);
        }
        delta.InstallPathsToDelete = [.. installPathsToDelete];
        return delta;
    }

    public PendingInstallBatchPlan BuildEstimatedInstallBatchPlan(IEnumerable<ChartPackage> requestedPackages, IEnumerable<ChartPackage> currentPendingPackages, IEnumerable<BMSFile> installedFiles, IEnumerable<LR2SongDBExtended.bmson_song> installedBmsonSongs, bool deletePendingPackageSourceAfterInstall, Func<ChartPackage, string, ISet<string>, int> countComponentMoveTargets)
    {
        var planStopwatch = Stopwatch.StartNew();
        var plan = new PendingInstallBatchPlan();
        var filterStopwatch = Stopwatch.StartNew();
        List<ChartPackage> pendingSnapshot = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        var pendingPackageSet = new HashSet<ChartPackage>(pendingSnapshot);
        plan.SelectedPendingPackages.AddRange(DeduplicatePackagesByPathOrReference(requestedPackages).Where(pkg => pendingPackageSet.Contains(pkg)));
        filterStopwatch.Stop();
        plan.FilterMs = filterStopwatch.ElapsedMilliseconds;
        plan.SelectedPendingCount = plan.SelectedPendingPackages.Count;

        var groupBuildStopwatch = Stopwatch.StartNew();
        var installedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile installedFile in installedFiles ?? [])
        {
            string key = ChartLookupKey.GetPrimaryHash(installedFile);
            if (!string.IsNullOrWhiteSpace(key))
            {
                installedHashes.Add(key);
            }
        }
        foreach (LR2SongDBExtended.bmson_song installedBmsonSong in installedBmsonSongs ?? [])
        {
            string key2 = ChartLookupKey.GetPrimaryHash(installedBmsonSong);
            if (!string.IsNullOrWhiteSpace(key2))
            {
                installedHashes.Add(key2);
            }
        }
        var moveGuardHashes = new HashSet<string>(installedHashes, StringComparer.OrdinalIgnoreCase);
        var reservedHashes = new HashSet<string>(moveGuardHashes, StringComparer.OrdinalIgnoreCase);
        var groupsByDestination = new Dictionary<string, PendingInstallBatchGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage originalPackage in plan.SelectedPendingPackages)
        {
            List<PackageChartEntry> packageEntries = originalPackage.ChartEntries;
            if (packageEntries.Count == 0)
            {
                continue;
            }
            if (originalPackage.DeferredEstimateReason != PendingEstimateDeferredReason.None
                && packageEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination)))
            {
                plan.DeferredManualHoldCount++;
                continue;
            }
            List<PackageChartEntry> installedInLibraryEntries = [];
            List<PackageChartEntry> installTargetPackageEntries = [];
            List<PackageChartEntry> duplicateInBatchEntries = [];
            foreach (PackageChartEntry packageEntry in packageEntries)
            {
                string lookupKey = packageEntry.Chart?.PrimaryLookupHash;
                if (string.IsNullOrWhiteSpace(lookupKey))
                {
                    installTargetPackageEntries.Add(packageEntry);
                }
                else if (installedHashes.Contains(lookupKey))
                {
                    installedInLibraryEntries.Add(packageEntry);
                }
                else if (reservedHashes.Contains(lookupKey))
                {
                    duplicateInBatchEntries.Add(packageEntry);
                }
                else
                {
                    reservedHashes.Add(lookupKey);
                    installTargetPackageEntries.Add(packageEntry);
                }
            }
            ApplyAlreadyInstalledWarning(installedInLibraryEntries);
            ApplyAlreadyInstalledWarning(duplicateInBatchEntries);
            List<PackageChartEntry> installWorkPackageEntries = installTargetPackageEntries;
            bool isResourceOnlyInstall = false;
            string destinationDirectory = null;
            if (installTargetPackageEntries.Count == 0)
            {
                if (installedInLibraryEntries.Count == 0)
                {
                    continue;
                }
                List<string> destinations = [.. packageEntries
                    .Select(entry => entry.Chart?.InstallDestination)
                    .Where(dst => !string.IsNullOrWhiteSpace(dst))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                if (destinations.Count != 1)
                {
                    continue;
                }
                destinationDirectory = destinations[0];
                isResourceOnlyInstall = true;
                installWorkPackageEntries = packageEntries;
            }
            else
            {
                if (installTargetPackageEntries.Any(entry => string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination)))
                {
                    continue;
                }
                destinationDirectory = installTargetPackageEntries.Select(entry => entry.Chart?.InstallDestination).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(destinationDirectory) || installTargetPackageEntries.Any(entry => !string.Equals(entry.Chart?.InstallDestination, destinationDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            ChartPackage installWorkPackage = ChartPackage.FromChartEntries(installWorkPackageEntries);
            installWorkPackage.path = originalPackage.path;
            installWorkPackage.delete_parent = originalPackage.delete_parent;
            HashSet<string> excludedPaths = null;
            if (installedInLibraryEntries.Count > 0 || duplicateInBatchEntries.Count > 0 || isResourceOnlyInstall)
            {
                excludedPaths = new HashSet<string>(installedInLibraryEntries.Select(entry => entry.Chart?.Path).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
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
                InstallTargetFileCount = installTargetPackageEntries.Count
            });
            plan.GroupedPackageCount++;
            plan.InstallTargetFileCount += installTargetPackageEntries.Count;
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
        Func<IEnumerable<ChartPackage>, string, List<BMSFile>, List<LR2SongDBExtended.bmson_song>, List<ChartPackage>, Dictionary<ChartPackage, HashSet<string>>, HashSet<string>, bool, bool, List<ChartPackage>> installPackages,
        Func<ChartPackage, string, ChartPackage> createInstalledDisplayPackage,
        Func<ChartPackage, (bool Success, CleanupSourceKind SourceKind)> cleanupPendingPackageSource,
        Action<string> logInfo = null)
    {
        var result = new PendingInstallBatchResult();
        if (plan == null)
        {
            return result;
        }
        foreach (PendingInstallBatchGroup groupEntry in plan.Groups)
        {
            var groupStopwatch = Stopwatch.StartNew();
            List<ChartPackage> destinationPackages = [.. groupEntry.Items.Select(item => item.OriginalPackage).Where(pkg => pkg != null)];
            List<ChartPackage> installWorkPackages = [.. groupEntry.Items.Select(item => item.InstallWorkPackage).Where(pkg => pkg != null)];
            var excludedComponentPathsByWorkPackage = groupEntry.Items
                .Where(item => item.ExcludedComponentPaths != null && item.InstallWorkPackage != null)
                .ToDictionary(item => item.InstallWorkPackage, item => item.ExcludedComponentPaths);
            var installStopwatch = Stopwatch.StartNew();
            List<ChartPackage> failedInstallWorkPackages = installPackages?.Invoke(
                installWorkPackages,
                groupEntry.DestinationDirectory,
                result.DeferredBmsMaintenanceTargets,
                result.DeferredBmsonMaintenanceSongs,
                result.DeferredInstalledPackages,
                excludedComponentPathsByWorkPackage,
                plan.MoveGuardHashes,
                true,
                deletePendingPackageSourceAfterInstall) ?? [];
            installStopwatch.Stop();
            var itemByInstallWorkPackage = groupEntry.Items
                .Where(item => item.InstallWorkPackage != null)
                .ToDictionary(item => item.InstallWorkPackage);
            var failedOriginalPackages = new HashSet<ChartPackage>(
                failedInstallWorkPackages.Where(workPkg => itemByInstallWorkPackage.ContainsKey(workPkg))
                    .Select(workPkg => itemByInstallWorkPackage[workPkg].OriginalPackage));
            foreach (ChartPackage failedOriginalPackage in failedOriginalPackages)
            {
                if (failedOriginalPackage != null)
                {
                    result.FailedPackages.Add(failedOriginalPackage);
                }
            }
            var installDbStopwatch = Stopwatch.StartNew();
            List<string> installRowsToDelete = [];
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
                    ChartPackage installedDisplayPackage = createInstalledDisplayPackage?.Invoke(item.OriginalPackage, groupEntry.DestinationDirectory);
                    if (installedDisplayPackage != null && installedDisplayPackage.ChartEntries.Count > 0)
                    {
                        result.DeferredInstalledPackages.Add(installedDisplayPackage);
                    }
                }
                ClearPackageInstallDestinations(item.OriginalPackage);
            }
            installDbStopwatch.Stop();
            var pendingMarkStopwatch = Stopwatch.StartNew();
            int pendingCountBeforeRemove = plan.SelectedPendingPackages.Count;
            int removedPendingCount = 0;
            foreach (ChartPackage pendingPackage in destinationPackages)
            {
                if (pendingPackage != null && result.PendingPackagesToRemove.Add(pendingPackage))
                {
                    removedPendingCount++;
                }
            }
            pendingMarkStopwatch.Stop();
            groupStopwatch.Stop();
            logInfo?.Invoke(
                "install_pending_packages_to_estimated_destinations group dst=" + groupEntry.DestinationDirectory +
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
            foreach (ChartPackage cleanupOnlyPackage in plan.CleanupOnlyCandidates.Distinct())
            {
                if (cleanupOnlyPackage == null)
                {
                    continue;
                }
                (bool Success, CleanupSourceKind SourceKind) = cleanupPendingPackageSource != null
                    ? cleanupPendingPackageSource(cleanupOnlyPackage)
                    : (false, CleanupSourceKind.MissingSource);
                if (Success)
                {
                    result.CleanupOnlySucceeded++;
                    if (SourceKind == CleanupSourceKind.MissingSource)
                    {
                        result.CleanupOnlyMissingSource++;
                    }
                    if (!string.IsNullOrWhiteSpace(cleanupOnlyPackage.path))
                    {
                        result.InstallRowsToDelete.Add(cleanupOnlyPackage.path);
                    }
                    result.PendingPackagesToRemove.Add(cleanupOnlyPackage);
                    ClearPackageInstallDestinations(cleanupOnlyPackage);
                    logInfo?.Invoke("estimated_install_cleanup_only_success package=" + cleanupOnlyPackage.path + " kind=" + SourceKind.ToString().ToLowerInvariant());
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

    public PackageInstallExecutionResult InstallPackages(
        IEnumerable<ChartPackage> chartPackagesInstall,
        string installationDirectory,
        Func<ChartPackage, string, bool, HashSet<string>, ISet<string>, bool> movePackageFiles,
        Action<IEnumerable<BMSFile>> upsertSongs,
        Action<IEnumerable<BMSFile>> updateMaintenance,
        Action<IEnumerable<BMSFile>> updateZeroNote,
        Action<IEnumerable<BMSFile>> applyScores,
        Action<IEnumerable<BMSFile>> applyState,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null,
        HashSet<string> existingHashes = null,
        bool skipInstalledPackageWhenNoBms = false,
        bool deleteSourceContentsAfterSuccessfulInstall = false)
    {
        var result = new PackageInstallExecutionResult();
        List<ChartPackage> packages = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        var totalStopwatch = Stopwatch.StartNew();
        var moveStopwatch = Stopwatch.StartNew();
        foreach (ChartPackage package in packages)
        {
            HashSet<string> excludedComponentPaths = null;
            excludedComponentPathsByPackage?.TryGetValue(package, out excludedComponentPaths);
            if (movePackageFiles != null && movePackageFiles(package, installationDirectory, deleteSourceContentsAfterSuccessfulInstall, existingHashes, excludedComponentPaths))
            {
                List<PackageChartEntry> packageEntries = package.ChartEntries;
                result.AddedEntries.AddRange(packageEntries);
                result.AddedCharts.AddRange(packageEntries
                    .Select(entry => entry?.Chart)
                    .Where(chart => chart != null));
                result.AddedBmsFiles.AddRange(packageEntries
                    .Select(entry => entry?.Chart?.BmsFile)
                    .Where(ChartFileKindResolver.IsBmsChartFile));
                result.AddedBmsonSongs.AddRange(packageEntries
                    .Select(entry => entry?.Chart?.BmsonSong)
                    .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path)));
                if (existingHashes != null)
                {
                    foreach (PackageChartEntry entry in packageEntries)
                    {
                        string lookupKey = entry?.Chart?.PrimaryLookupHash;
                        if (!string.IsNullOrWhiteSpace(lookupKey))
                        {
                            existingHashes.Add(lookupKey);
                        }
                    }
                }
                bool shouldSkipInstalledPackageRegistration = skipInstalledPackageWhenNoBms && packageEntries.Count == 0;
                if (!shouldSkipInstalledPackageRegistration)
                {
                    result.InstalledPackagesToRegister.Add(package);
                }
            }
            else if (!string.IsNullOrWhiteSpace(package.path) && (File.Exists(package.path) || Directory.Exists(package.path)))
            {
                result.FailedPackages.Add(package);
            }
        }
        moveStopwatch.Stop();
        result.MoveMs = moveStopwatch.ElapsedMilliseconds;

        foreach (PackageChartEntry addedEntry in result.AddedEntries.Where(entry => entry?.Chart != null))
        {
            addedEntry.ClearPostInstallState();
        }

        var songDbStopwatch = Stopwatch.StartNew();
        upsertSongs?.Invoke(result.AddedBmsFiles);
        songDbStopwatch.Stop();
        result.SongDbMs = songDbStopwatch.ElapsedMilliseconds;

        var maintenanceStopwatch = Stopwatch.StartNew();
        updateMaintenance?.Invoke(result.AddedBmsFiles);
        maintenanceStopwatch.Stop();
        result.MaintenanceMs = maintenanceStopwatch.ElapsedMilliseconds;

        if (updateZeroNote != null)
        {
            var zeroNoteStopwatch = Stopwatch.StartNew();
            updateZeroNote(result.AddedBmsFiles);
            zeroNoteStopwatch.Stop();
            result.ZeroNoteMs = zeroNoteStopwatch.ElapsedMilliseconds;
        }

        var scoreStopwatch = Stopwatch.StartNew();
        applyScores?.Invoke(result.AddedBmsFiles);
        scoreStopwatch.Stop();
        result.ScoreMs = scoreStopwatch.ElapsedMilliseconds;

        var applyStopwatch = Stopwatch.StartNew();
        applyState?.Invoke(result.AddedBmsFiles);
        applyStopwatch.Stop();
        result.ApplyMs = applyStopwatch.ElapsedMilliseconds;

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
        var result = new ForceInstallBatchResult();
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
            List<ChartPackage> failedPackages = installPackages?.Invoke([pendingPackage], deferredInstalledPackages) ?? [];
            result.Processed++;
            if (failedPackages.Count == 0)
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
        }
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
        Action<string> logInfo = null)
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
                if (deletePendingPackageSourceAfterInstall)
                {
                    (bool Success, CleanupSourceKind SourceKind) = cleanupPendingPackageSource != null
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
                }
                else
                {
                    result.SkippedNoComponentTarget++;
                    logInfo?.Invoke("advanced_pending_resource_overwrite skip_no_component_target path=" + pendingPackage.path);
                }
                result.Processed++;
                onEachProcessed?.Invoke();
                continue;
            }
            List<PackageChartEntry> packageEntries = [.. pendingPackage.ChartEntries.Where(entry => entry != null)];
            var installDestinations = packageEntries.ToDictionary(entry => entry, entry => entry.CaptureInstallDestinationState());
            foreach (PackageChartEntry entry in packageEntries)
            {
                entry.SetInstallDestinationPathOnly(destinationDir);
            }
            bool installSucceeded = false;
            try
            {
                installSucceeded = installPackageToEstimatedDestination != null && installPackageToEstimatedDestination(pendingPackage, destinationDir);
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
        }
        return result;
    }

    public PendingZeroNoteRenameResult RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
        IEnumerable<BMSFile> targetFiles,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        var result = new PendingZeroNoteRenameResult();
        List<BMSFile> files = DeduplicateFilesByPathOrReference((targetFiles ?? []).Where(IsBmsFormatChartFile));
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

    public PendingExtensionRenameResult RenamePendingBmsFormatChartFileExtensions(
        IEnumerable<BMSFile> targetFiles,
        string newExt,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        var result = new PendingExtensionRenameResult();
        var stopwatch = Stopwatch.StartNew();
        List<BMSFile> files = DeduplicateFilesByPathOrReference(
            (targetFiles ?? []).Where(file => IsBmsFormatChartFile(file) && File.Exists(file.path)));
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

    public PendingPackageSourceDeletionResult DeletePendingPackageSources(
        IEnumerable<ChartPackage> packages,
        IEnumerable<ChartPackage> currentPendingPackages,
        bool sendToRecycleBin,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        var result = new PendingPackageSourceDeletionResult();
        List<ChartPackage> requestedPackages = DeduplicatePackagesByPathOrReference(packages);
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        result.Requested = requestedPackages.Count;
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
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
                result.Skipped++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_cleanup skipped_not_pending path=" + requestedPackage.path);
                onEachProcessed?.Invoke();
                continue;
            }
            bool isDirectory = Directory.Exists(pendingPackage.path);
            bool isFile = !isDirectory && File.Exists(pendingPackage.path);
            try
            {
                if (isDirectory)
                {
                    if (sendToRecycleBin)
                    {
                        fileMutationService.DeleteDirectoryShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    }
                    else
                    {
                        fileMutationService.DeleteDirectoryDirect(pendingPackage.path, recursive: true, recursiveDirectoryTreeFileMutationOptions);
                    }
                    logInfo?.Invoke("advanced_pending_cleanup deleted path=" + pendingPackage.path + " kind=directory");
                }
                else if (isFile)
                {
                    if (sendToRecycleBin)
                    {
                        fileMutationService.DeleteFileShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                    }
                    else
                    {
                        fileMutationService.DeleteFileDirect(pendingPackage.path, targetOnlyFileMutationOptions);
                    }
                    logInfo?.Invoke("advanced_pending_cleanup deleted path=" + pendingPackage.path + " kind=file");
                }
                else
                {
                    logInfo?.Invoke("advanced_pending_cleanup missing_source_removed path=" + pendingPackage.path);
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
        var handledByFolderDeletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedByFailedFolderDeletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;

        if (deleteContainingPackageFoldersWhenNoBms)
        {
            foreach (ChartPackage pendingPackage in libraryFileOperationsService.GetPendingPackagesFullyCoveredBySelection(pendingPackages, selectedPaths))
            {
                if (!Directory.Exists(pendingPackage.path))
                {
                    continue;
                }
                List<PackageChartEntry> packageEntries = pendingPackage.ChartEntries;
                List<string> packageChartPaths = GetPackageChartPaths(packageEntries);
                try
                {
                    fileMutationService.DeleteDirectoryShell(pendingPackage.path, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    result.ChartPathsToRemove.AddRange(packageChartPaths);
                    foreach (string packageChartPath in packageChartPaths)
                    {
                        handledByFolderDeletePaths.Add(packageChartPath);
                    }
                    result.Processed += packageChartPaths.Count;
                    result.Removed += packageChartPaths.Count;
                }
                catch (Exception ex)
                {
                    foreach (string packageChartPath in packageChartPaths)
                    {
                        blockedByFailedFolderDeletePaths.Add(packageChartPath);
                    }
                    result.Processed += packageChartPaths.Count;
                    result.Failed += packageChartPaths.Count;
                    result.Failures.Add(new PendingFileDeletionFailure
                    {
                        Path = pendingPackage.path,
                        Exception = ex,
                        IsDirectory = true
                    });
                }
            }
        }

        foreach (PendingChartDeletionTarget pendingChart in selectedTargets)
        {
            string pendingChartPath = pendingChart.Path;
            if (!string.IsNullOrWhiteSpace(pendingChartPath) && (handledByFolderDeletePaths.Contains(pendingChartPath) || blockedByFailedFolderDeletePaths.Contains(pendingChartPath)))
            {
                continue;
            }

            result.Processed++;
            try
            {
                if (File.Exists(pendingChartPath))
                {
                    fileMutationService.DeleteFileShell(pendingChartPath, UIOption.OnlyErrorDialogs, recycleOption, targetOnlyFileMutationOptions);
                    result.ChartPathsToRemove.Add(pendingChartPath);
                    result.Removed++;
                }
                else
                {
                    result.Skipped++;
                }
            }
            catch (Exception ex2)
            {
                result.Failed++;
                result.Failures.Add(new PendingFileDeletionFailure
                {
                    Path = pendingChartPath,
                    Exception = ex2,
                    IsDirectory = false
                });
            }
        }

        return result;
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

    private static List<string> GetPackageChartPaths(IEnumerable<PackageChartEntry> entries)
    {
        return [.. (entries ?? [])
            .Select(entry => entry?.Chart?.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static void ApplyAlreadyInstalledWarning(IEnumerable<PackageChartEntry> entries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            entry?.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
            entry?.SetWarning(ChartWarningKind.AlreadyInstalled, Properties.Resources.Warning_AlreadyInstalled);
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

    private static bool IsBmsHashAvailable(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash);
    }

    private void MovePackageComponentsSmart(
        ChartPackage package,
        List<string> installComponentFiles,
        string destinationDirectory,
        ISet<string> excludedComponentPaths,
        bool keepProtectedFilesByRenaming,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInstallPerformance)
    {
        var componentMoveSummary = new ComponentMoveSummary();
        ComponentMovePlanBuildResult movePlanResult = BuildComponentMovePlan(installComponentFiles, destinationDirectory, excludedComponentPaths);
        List<ComponentMovePlanItem> movePlanItems = movePlanResult.PlanItems;
        componentMoveSummary.Total = movePlanItems.Count;
        componentMoveSummary.SkippedByExclusion = movePlanResult.SkippedByExclusion;

        foreach (ComponentMovePlanItem planItem in movePlanItems)
        {
            string srcFilePath = planItem.SourcePath;
            string dstFilePath = planItem.DestinationPath;
            if (!File.Exists(srcFilePath))
            {
                throw new FileNotFoundException(Resources.Error_FileNotFound, srcFilePath);
            }
            if (IsSamePath(srcFilePath, dstFilePath))
            {
                componentMoveSummary.SkippedSame++;
                componentMoveSummary.SkippedSamePath++;
                continue;
            }
            string dstDirPath = Path.GetDirectoryName(dstFilePath);
            if (!string.IsNullOrWhiteSpace(dstDirPath))
            {
                fileMutationService.EnsureDirectory(dstDirPath, targetOnlyFileMutationOptions);
            }

            ComponentMoveDecision moveDecision = DecideComponentMove(srcFilePath, dstFilePath);
            bool keepByRename = keepProtectedFilesByRenaming && File.Exists(dstFilePath) && IsSmartOverwriteProtectedExtension(srcFilePath) && moveDecision != ComponentMoveDecision.SkipSame;
            if (keepByRename)
            {
                FileCollisionResolutionResult resolution = libraryFileOperationsService.ResolveFileCollisionWithSuffix(srcFilePath, dstFilePath, null, "smart_overwrite", logInstallPerformance);
                if (resolution.EncounteredFileCollision)
                {
                    componentMoveSummary.HashChecked++;
                }
                if (resolution.DuplicateMatched)
                {
                    fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                    componentMoveSummary.SkippedSame++;
                    componentMoveSummary.DeletedAfterSkip++;
                    componentMoveSummary.HashSameSkip++;
                    continue;
                }
                fileMutationService.MoveFile(srcFilePath, resolution.FinalPath, overwrite: false, targetOnlyFileMutationOptions);
                componentMoveSummary.Moved++;
                componentMoveSummary.RenamedKeep++;
                if (resolution.AnyHashUnavailable)
                {
                    componentMoveSummary.HashUnavailableRenamed++;
                }
                else
                {
                    componentMoveSummary.HashDiffRenamed++;
                }
                if (moveDecision == ComponentMoveDecision.Overwrite)
                {
                    componentMoveSummary.RenamedFromOverwrite++;
                }
                else
                {
                    componentMoveSummary.RenamedFromSkipOlder++;
                }
                continue;
            }

            switch (moveDecision)
            {
                case ComponentMoveDecision.Move:
                    fileMutationService.MoveFile(srcFilePath, dstFilePath, overwrite: true, targetOnlyFileMutationOptions);
                    componentMoveSummary.Moved++;
                    break;
                case ComponentMoveDecision.Overwrite:
                    fileMutationService.MoveFile(srcFilePath, dstFilePath, overwrite: true, targetOnlyFileMutationOptions);
                    componentMoveSummary.Moved++;
                    componentMoveSummary.Overwritten++;
                    break;
                case ComponentMoveDecision.SkipSame:
                    fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                    componentMoveSummary.SkippedSame++;
                    componentMoveSummary.DeletedAfterSkip++;
                    break;
                default:
                    fileMutationService.DeleteFileDirect(srcFilePath, targetOnlyFileMutationOptions);
                    componentMoveSummary.SkippedOlder++;
                    componentMoveSummary.DeletedAfterSkip++;
                    break;
            }
        }
        CleanupEmptyComponentDirectories(installComponentFiles, fileMutationService, targetOnlyFileMutationOptions);
        int movedNewCount = Math.Max(0, componentMoveSummary.Moved - componentMoveSummary.Overwritten);
        logInstallPerformance?.Invoke("component_move_summary package=" + package.path + " total=" + componentMoveSummary.Total + " moved=" + componentMoveSummary.Moved + " moved_new=" + movedNewCount + " overwritten=" + componentMoveSummary.Overwritten + " skipped_same=" + componentMoveSummary.SkippedSame + " skipped_same_path=" + componentMoveSummary.SkippedSamePath + " skipped_older=" + componentMoveSummary.SkippedOlder + " skipped_by_exclusion=" + componentMoveSummary.SkippedByExclusion + " deleted_after_skip=" + componentMoveSummary.DeletedAfterSkip + " renamed_keep=" + componentMoveSummary.RenamedKeep + " renamed_from_overwrite=" + componentMoveSummary.RenamedFromOverwrite + " renamed_from_skip_older=" + componentMoveSummary.RenamedFromSkipOlder + " hash_checked=" + componentMoveSummary.HashChecked + " hash_same_skip=" + componentMoveSummary.HashSameSkip + " hash_diff_renamed=" + componentMoveSummary.HashDiffRenamed + " hash_unavailable_renamed=" + componentMoveSummary.HashUnavailableRenamed + " failed=" + componentMoveSummary.Failed);
    }

    private static void CleanupEmptyComponentDirectories(IEnumerable<string> installComponentDirectories, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions)
    {
        foreach (string installComponentDirectory in installComponentDirectories.Where(path => Directory.Exists(path)).OrderByDescending(path => path.Length))
        {
            TryDeleteEmptyDirectoryTree(installComponentDirectory, fileMutationService, targetOnlyFileMutationOptions);
        }
    }

    private static void TryDeleteEmptyDirectoryTree(string rootDirectoryPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions)
    {
        try
        {
            if (!Directory.Exists(rootDirectoryPath))
            {
                return;
            }
            foreach (string childDirectoryPath in Directory.EnumerateDirectories(rootDirectoryPath).ToList())
            {
                TryDeleteEmptyDirectoryTree(childDirectoryPath, fileMutationService, targetOnlyFileMutationOptions);
            }
            if (!Directory.EnumerateFileSystemEntries(rootDirectoryPath).Any())
            {
                fileMutationService.DeleteDirectoryDirect(rootDirectoryPath, recursive: false, targetOnlyFileMutationOptions);
            }
        }
        catch
        {
        }
    }

    private static bool HasRemainingDirectoryEntries(string directoryPath)
    {
        try
        {
            return Directory.Exists(directoryPath) && Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }
        catch
        {
            return false;
        }
    }
}
