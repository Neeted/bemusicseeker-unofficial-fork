using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
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
        string relativePath = PackageInstallDestinationMap.GetRelativeChartPath(sourceRootPath, chart?.Path);
        return string.IsNullOrWhiteSpace(relativePath) ? string.Empty : Path.Combine(destinationDirectory, relativePath);
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

    /// <summary>通常候補と実譜面先を一度捕捉して型検査し、物理計画にも同じ値を渡します。新規directory一括移動は従来どおり個別component計画を作りません。</summary>
    private ComponentMovePlanBuildResult ValidatePackageDestinationTypes(
        string sourcePath,
        bool isAutoNaming,
        string destinationDirectory,
        IEnumerable<string> installComponentFiles,
        IEnumerable<PackageChartEntry> installTargetEntries,
        ISet<string> excludedComponentPaths,
        out List<KeyValuePair<string, string>> chartDestinationPaths)
    {
        chartDestinationPaths = [];
        ComponentMovePlanBuildResult componentPlan = null;
        FileDbMutationDestinationTypeGuard.ValidatePath(
            sourcePath,
            destinationDirectory,
            expectedIsDirectory: true);

        if (!isAutoNaming)
        {
            componentPlan = BuildComponentMovePlan(
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

        var chartReservations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ComponentMovePlanItem component in componentPlan?.PlanItems ?? [])
        {
            AddPlannedDestinationAndParents(chartReservations, component.DestinationPath, destinationDirectory);
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
                    chart,
                    chartReservations);
            if (string.IsNullOrWhiteSpace(chart?.Path))
            {
                continue;
            }
            chartDestinationPaths.Add(new KeyValuePair<string, string>(chart.Path, destinationChartPath));
            if (string.IsNullOrWhiteSpace(destinationChartPath)) { continue; }
            FileDbMutationDestinationTypeGuard.ValidatePath(
                chart.Path,
                destinationChartPath,
                expectedIsDirectory: false);
            AddPlannedDestinationAndParents(chartReservations, destinationChartPath, destinationDirectory);
        }
        return componentPlan;
    }

    /// <summary>同じ型検査を通した捕捉済み候補・実譜面先と固定optionsから、一計画の最終component先と消費元を決めます。通常先・必要directory・譜面先・生成先を空き名に使わず、実在同内容の省略は保持します。参照探索や候補の再列挙は行いません。</summary>
    internal (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) BuildFinalComponentMovePlan(
        IReadOnlyList<ComponentMovePlanItem> componentCandidates,
        IReadOnlyList<KeyValuePair<string, string>> chartDestinationPaths,
        string destinationDirectory,
        BmsLibraryOptionsSnapshot options,
        Action<string> logInstallPerformance = null)
    {
        ArgumentNullException.ThrowIfNull(componentCandidates);
        ArgumentNullException.ThrowIfNull(chartDestinationPaths);
        var unavailableDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finalDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ComponentMovePlanItem component in componentCandidates)
        {
            if (component != null) { AddPlannedDestinationAndParents(unavailableDestinations, component.DestinationPath, destinationDirectory); }
        }
        foreach (KeyValuePair<string, string> chart in chartDestinationPaths)
        {
            AddPlannedDestinationAndParents(unavailableDestinations, chart.Value, destinationDirectory);
            if (!string.IsNullOrWhiteSpace(chart.Value)) { EnsureUniqueDestinationPath(finalDestinations, chart.Value); }
        }

        var result = new List<ComponentMovePlanItem>();
        var sourceCleanupFiles = new List<string>();
        foreach (ComponentMovePlanItem component in componentCandidates)
        {
            if (string.IsNullOrWhiteSpace(component?.SourcePath)) { continue; }
            if (!LongPathFileSystem.FileExists(component.SourcePath))
            {
                throw new FileNotFoundException(Resources.Error_FileNotFound, component.SourcePath);
            }
            string finalPath = component.DestinationPath;
            if (options?.EnableSmartComponentOverwrite == true)
            {
                bool samePath = IsSamePath(component.SourcePath, finalPath);
                ComponentMoveDecision decision = samePath ? ComponentMoveDecision.SkipSame : DecideComponentMove(component.SourcePath, finalPath);
                bool keepByRename = options.KeepSmartOverwriteProtectedFilesByRenaming
                    && LongPathFileSystem.FileExists(finalPath)
                    && IsSmartOverwriteProtectedExtension(component.SourcePath)
                    && decision != ComponentMoveDecision.SkipSame;
                if (keepByRename)
                {
                    FileCollisionResolutionResult resolution = libraryFileOperationsService.ResolveFileCollisionWithSuffix(
                        component.SourcePath, finalPath, null, "smart_overwrite", logInstallPerformance, unavailableDestinations.Contains);
                    if (resolution.DuplicateMatched)
                    {
                        // 同じ固定保護設定の後続componentも、実在する同内容先を上書きしません。
                        // 消費元だけを確定後のcleanupへ渡し、現在のhash一致で先に削除しません。
                        sourceCleanupFiles.Add(component.SourcePath);
                        continue;
                    }
                    finalPath = resolution.FinalPath;
                }
                else if (decision == ComponentMoveDecision.SkipSame || decision == ComponentMoveDecision.SkipOlderOrEqual)
                {
                    if (!samePath) { sourceCleanupFiles.Add(component.SourcePath); }
                    continue;
                }
            }
            sourceCleanupFiles.Add(component.SourcePath);
            EnsureMutationDestinationIsDistinct(component.SourcePath, finalPath);
            EnsureUniqueDestinationPath(finalDestinations, finalPath);
            unavailableDestinations.Add(finalPath);
            result.Add(new ComponentMovePlanItem { SourcePath = component.SourcePath, DestinationPath = finalPath });
        }
        return (result, sourceCleanupFiles);
    }

    /// <summary>既知の配置pathから必要directoryをrootまで辿り、未存在の親をfile採番で奪わないようにします。FS列挙・存在確認は行いません。</summary>
    private void AddPlannedDestinationAndParents(ISet<string> destinations, string path, string destinationDirectory)
    {
        for (string current = path; !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
        {
            destinations.Add(current);
            if (IsSamePath(current, destinationDirectory)) { break; }
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

    /// <summary>削除対象フォルダを再走査して全譜面が所持済みと確認できたパッケージと、所持済みの単体入力を返します。</summary>
    public List<ChartPackage> GetPendingPackagesContainingOnlyInstalledCharts(IEnumerable<ChartPackage> pendingPackages, Func<ChartFile, bool> isInstalledChart)
    {
        List<ChartPackage> result = [];
        foreach (ChartPackage package in pendingPackages ?? [])
        {
            if (package == null || string.IsNullOrWhiteSpace(package.path))
            {
                continue;
            }
            List<PackageChartEntry> entries;
            if (LongPathFileSystem.DirectoryExists(package.path))
            {
                PackageChartDiscoverySnapshot discovery = PackageInstallEstimationSnapshotBuilder.BuildPackageChartDiscoverySnapshot(package.path);
                if (!discovery.IsComplete)
                {
                    continue;
                }
                entries = discovery.ChartEntries;
            }
            else
            {
                entries = package.ChartEntries;
                // フォルダの存在を確認できない場合に、配下の部分一覧を単体入力へ読み替えない。
                if (entries.Any(entry => !string.Equals(package.path, entry.Chart.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
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
            deduplicated.Add(chart);
        }
        return deduplicated;
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
    /// ディレクトリ探索で見つかった譜面パッケージの一覧を返します。
    /// 直下譜面を単体化した場合も子を探索し、ディレクトリ全体を一パッケージとした場合は包含済みの子を重複発見しません。
    /// </summary>
    internal ChartPackageDiscoveryResult SearchChartPackagesRecursivelyWithMetadata(string dirfullpath, bool recursive = false)
        => DiscoverDirectory(dirfullpath, recursive);

    /// <summary>一度の探索でscope・完全性・既知entryを共有し、親の実在資源、子作品、明示一式の順に分類します。</summary>
    /// <remarks>親一式の収集先を借用する子は既読entryだけを追加し、完全性の確認後に親だけがpackageを生成します。</remarks>
    private ChartPackageDiscoveryResult DiscoverDirectory(string directory, bool recursive, IReadOnlyList<PackageChartEntry> knownDirectCharts = null, string[] knownEntries = null, CancellationToken token = default, List<PackageChartEntry> enclosingWholeCharts = null)
    {
        var result = new ChartPackageDiscoveryResult();
        if (token.IsCancellationRequested) { result.IsComplete = false; result.IncompleteScopes.Add(directory); return result; }
        string[] entries;
        try { entries = knownEntries ?? [.. LongPathFileSystem.EnumerateFileSystemEntries(directory, "*", System.IO.SearchOption.TopDirectoryOnly)]; }
        catch (Exception exception) { result.IsComplete = false; result.IncompleteScopes.Add(directory); result.Failures.Add(exception); return result; }
        var files = new List<string>();
        var directories = new List<string>();
        foreach (string path in entries)
        {
            if (LongPathFileSystem.FileExists(path)) { files.Add(path); }
            else if (LongPathFileSystem.DirectoryExists(path)) { directories.Add(path); }
            else { result.IsComplete = false; result.Failures.Add(new FileNotFoundException(null, path)); }
        }
        var directCharts = new List<PackageChartEntry>();
        var knownByPath = knownDirectCharts?.ToDictionary(entry => entry.Chart.Path, StringComparer.OrdinalIgnoreCase);
        bool hasResources = knownDirectCharts != null || HasAdjacentPreviewAudio(files);
        foreach (string path in files.Where(ChartFileKindResolver.IsSupportedChartFilePath))
        {
            if (token.IsCancellationRequested) { result.IsComplete = false; result.IncompleteScopes.Add(directory); return result; }
            string preview = string.Empty;
            PackageChartEntry entry;
            if (knownByPath == null || !knownByPath.TryGetValue(path, out entry))
            {
                entry = PackageChartEntry.FromPath(path, out preview, out Exception failure);
                if (failure != null) { result.Failures.Add(failure); }
            }
            if (entry == null) { result.IsComplete = false; continue; }
            directCharts.Add(entry);
            hasResources = hasResources || HasExistingPackageChartResources(entry) || HasExistingPreviewResource(entry, preview);
        }
        result.ContainsCharts = files.Any(ChartFileKindResolver.IsSupportedChartFilePath);
        // 親資源で一式が決まる枝だけ収集先を共有し、各祖先への子孫entryの平坦コピーと子package生成を避けます。
        List<PackageChartEntry> wholeCharts = enclosingWholeCharts;
        if (wholeCharts != null) { wholeCharts.AddRange(directCharts); }
        else if (directCharts.Count > 0 && hasResources) { wholeCharts = [.. directCharts]; }
        bool directComplete = result.IsComplete;
        var children = new List<ChartPackageDiscoveryResult>();
        foreach (string child in directories)
        {
            ChartPackageDiscoveryResult discovered = DiscoverDirectory(child, recursive: true, token: token, enclosingWholeCharts: wholeCharts);
            children.Add(discovered);
            result.IsComplete &= discovered.IsComplete;
            result.ContainsCharts |= discovered.ContainsCharts;
            result.IncompleteScopes.AddRange(discovered.IncompleteScopes);
            result.Failures.AddRange(discovered.Failures);
        }
        bool hasKnownChildCharts = children.Any(child => child.ContainsCharts);
        if (!directComplete || (directCharts.Count > 0 && !result.IsComplete && (hasResources || !hasKnownChildCharts)))
        {
            result.IncompleteScopes.Add(directory);
            return result;
        }
        if (enclosingWholeCharts != null) { return result; }
        if (directCharts.Count > 0)
        {
            if (hasResources || !hasKnownChildCharts)
            {
                result.Packages.Add(CreatePackageWithKnownCharts(directory, deleteParent: false, wholeCharts ?? directCharts));
                return result;
            }
            result.Packages.AddRange(directCharts.Select(entry => CreatePackageWithKnownCharts(entry.Chart.Path, recursive, [entry])));
        }
        foreach (ChartPackageDiscoveryResult child in children) { result.Packages.AddRange(child.Packages); }
        return result;
    }

    /// <summary>既取得の直下ファイルだけから未参照のpreview慣例を確認します。</summary>
    private static bool HasAdjacentPreviewAudio(IEnumerable<string> files)
        => files.Any(path => Path.GetFileName(path).StartsWith("preview", StringComparison.OrdinalIgnoreCase)
            && ChartResourceExtensions.IsAudioExtension(Path.GetExtension(path)));

    /// <summary>発見専用PREVIEWの通常相対参照だけを、既存の音声拡張子互換で解決します。</summary>
    private static bool HasExistingPreviewResource(PackageChartEntry entry, string preview)
    {
        if (string.IsNullOrWhiteSpace(preview) || Path.IsPathRooted(preview.Trim())) { return false; }
        var reference = ChartResourceReference.Parse(preview, ChartResourceKind.Audio);
        return reference.Status == ChartResourcePathNormalizationStatus.Valid && !string.IsNullOrWhiteSpace(reference.LookupKey)
            && ChartResourceExistence.ExistsWithCompatibleExtensions(Path.GetDirectoryName(entry.Chart.Path), reference.LookupKey, ChartResourceExtensions.AudioExtensions);
    }

    private static bool HasExistingPackageChartResources(PackageChartEntry entry)
    {
        if (entry?.Chart == null) { return false; }
        ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(entry.Chart, includeUnused: true);
        return health?.WavFilesExisting > 0 || health?.BgaFilesExisting > 0 || health?.MovieFilesExisting > 0
            || health?.StagefileExisting == true || health?.BackbmpExisting == true || health?.BannerExisting == true;
    }

    private static ResourceHealthMaintenanceSnapshot BuildPendingResourceHealthMaintenanceInfo(PackageChartEntry entry, ChartResourcePlacement placement)
    {
        if (entry?.Chart == null)
        {
            return null;
        }

        ChartFile chart = entry.Chart;
        ResourceHealthMaintenanceSnapshot result = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, placement: placement);
        ResourceHealthMaintenanceSnapshot captured = BmsLibraryMaintenanceService.GetResourceHealthSnapshot(chart);
        if (result != null && captured != null)
        {
            result = result with { Encoding = captured.Encoding, EncodingFixed = captured.EncodingFixed };
        }
        // 全定義を消費した時点の計算済みLR2診断は、配置の存在確認が未判定でも引き継ぎます。DB由来の古い診断は再利用しません。
        // 所持譜面の手動再スキャンは別入口で本文を再解析し、全定義から再評価します。
        return result != null && captured?.Origin == MaintenanceInfoOrigin.Calculated && captured.Lr2WarningFlags.HasValue && chart.Kind == ChartFileKind.Bms
            ? result with { Lr2WarningFlags = captured.Lr2WarningFlags, Lr2ResourceMaxRelativeCp932Bytes = captured.Lr2ResourceMaxRelativeCp932Bytes, Lr2ResourceHasParentTraversal = captured.Lr2ResourceHasParentTraversal }
            : result;
    }

    /// <summary>同じ取得結果から件数・警告・LR2診断を項目へ投影し、消費済みの未使用定義を解放します。</summary>
    internal static IReadOnlyList<ChartWarning> ApplyPendingResourceHealthProjection(PackageChartEntry entry)
        => ApplyPendingResourceHealthProjection(entry, out _, entry?.ResourcePlacement);

    private static IReadOnlyList<ChartWarning> ApplyPendingResourceHealthProjection(PackageChartEntry entry, out ResourceHealthMaintenanceSnapshot maintenanceInfo, ChartResourcePlacement placement = null)
    {
        entry?.AcquireResources();
        maintenanceInfo = BuildPendingResourceHealthMaintenanceInfo(entry, placement);
        if (maintenanceInfo == null)
        {
            entry?.ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
            return [];
        }
        // 発見・グループ化が終わった後は、全定義を保持せず、LR2診断を確定値へ引き継ぎます。
        using IDisposable deferredNotification = entry.DeferPropertyChangedNotifications();
        ChartFile chart = entry.Chart;
        entry.ApplyCurrentChart(chart with { Resources = chart.Resources.Where(reference => reference.IsUsed || reference.Usage == ChartResourceUsage.InputDiagnostic).ToImmutableList() });
        return entry.ApplyCalculatedResourceHealth(ChartFileProjection.WithMaintenance(entry.Chart, maintenanceInfo), placement);
    }

    /// <summary>登録入口の既存評価で元配置の結果を確定し、その警告を返します。</summary>
    internal static IReadOnlyList<ChartWarning> InitializePendingResourceHealth(PackageChartEntry entry)
    {
        IReadOnlyList<ChartWarning> warnings = ApplyPendingResourceHealthProjection(entry, out ResourceHealthMaintenanceSnapshot health);
        entry.InitializeOriginalResourceHealth(health);
        return warnings;
    }

    /// <summary>保留の導入先・候補・推定警告を消去し、登録時の健康度12値を復元します。現在の検査属性、同梱文脈と譜面相対配置を保ち、物理評価は行いません。</summary>
    internal static void ClearPendingInstallDestination(PackageChartEntry entry)
    {
        ResourceHealthMaintenanceSnapshot original = entry.GetOriginalResourceHealth();
        ChartFile chart = entry.Chart;
        ResourceHealthMaintenanceSnapshot current = BmsLibraryMaintenanceService.GetResourceHealthSnapshot(chart)
            ?? throw new InvalidOperationException("Current resource health has not been initialized.");
        ResourceHealthMaintenanceSnapshot restored = current with
        {
            WavFilesDefined = original.WavFilesDefined,
            WavFilesExisting = original.WavFilesExisting,
            BgaFilesDefined = original.BgaFilesDefined,
            BgaFilesExisting = original.BgaFilesExisting,
            MovieFilesDefined = original.MovieFilesDefined,
            MovieFilesExisting = original.MovieFilesExisting,
            StagefileDefined = original.StagefileDefined,
            StagefileExisting = original.StagefileExisting,
            BannerDefined = original.BannerDefined,
            BannerExisting = original.BannerExisting,
            BackbmpDefined = original.BackbmpDefined,
            BackbmpExisting = original.BackbmpExisting
        };
        ChartResourcePlacement prior = entry.ResourcePlacement;
        var placement = new ChartResourcePlacement(ChartResourcePlacementBasis.Current, Path.GetDirectoryName(chart.Path),
            prior?.BundledResources, bundledComplete: prior?.BundledResourcesComplete ?? true, relativeDirectory: prior?.RelativeDirectory ?? string.Empty);
        using IDisposable deferredNotification = entry.DeferPropertyChangedNotifications();
        entry.ClearInstallDestination();
        entry.ApplyCalculatedResourceHealth(ChartFileProjection.WithMaintenance(entry.Chart, restored), placement);
    }

    /// <summary>同一操作の対象entryに対応する相対位置だけを捕捉し、編集時の源側評価と後続健康度で共有します。参照や同梱は取得せず、packageなしでは既存の配置文脈を保持します。</summary>
    internal static IReadOnlyDictionary<PackageChartEntry, string> CapturePendingRelativeDirectories(IEnumerable<PackageChartEntry> entries, ChartPackage package)
    {
        List<PackageChartEntry> targets = [.. (entries ?? []).Where(entry => entry?.Chart != null)];
        bool isDirectoryPackage = targets.Count > 0 && package != null && !string.IsNullOrWhiteSpace(package.path) && LongPathFileSystem.DirectoryExists(package.path);
        return targets.Distinct().ToDictionary(entry => entry, entry => package == null ? entry.ResourcePlacement?.RelativeDirectory ?? string.Empty
            : Path.GetDirectoryName(PackageInstallDestinationMap.GetRelativeChartPath(package.path, entry.Chart.Path, isDirectoryPackage)) ?? string.Empty);
    }

    /// <summary>一操作の同梱・候補索引を共有し、実導入と同じ譜面相対配置で健康度を更新します。捕捉済み位置は同一対象entryに対応させ、既知空mapを再取得しません。packageなしでは捕捉済みの配置文脈を引き継ぎます。</summary>
    internal static void RefreshPendingResourceHealth(IEnumerable<PackageChartEntry> entries, ChartPackage package, DirectoryResourceLookupCache cache, DirectoryResourceLookupCache.Entry capturedBundled = null, bool capturedBundledComplete = true,
        IReadOnlyDictionary<PackageChartEntry, string> relativeDirectories = null)
    {
        List<PackageChartEntry> targets = [.. (entries ?? []).Where(entry => entry?.Chart != null)];
        bool hasDestination = targets.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart.InstallDestination) || entry.Chart.InstallDestinationSuggestions.Count > 0);
        PackageInstallSurfaceSnapshot surface = capturedBundled == null ? package?.CaptureInstallEstimationSurfaceSnapshot() : null;
        if (surface == null && hasDestination && capturedBundled == null)
        { surface = package?.GetOrBuildInstallEstimationSurfaceSnapshot(out _); }
        // 通常失敗の観測済み資源は利用し、未観測の参照だけを未確認として扱います。
        bool bundledComplete = capturedBundled != null ? capturedBundledComplete : package == null || surface?.IsComplete == true;
        DirectoryResourceLookupCache.Entry bundled = capturedBundled ?? surface?.BundledResources;
        IReadOnlyDictionary<PackageChartEntry, string> capturedRelativeDirectories = relativeDirectories ?? CapturePendingRelativeDirectories(targets, package);
        foreach (PackageChartEntry entry in targets)
        {
            string relativeDirectory = capturedRelativeDirectories[entry];
            var placement = ChartResourcePlacement.Select(entry.Chart, bundled ?? entry.ResourcePlacement?.BundledResources, cache,
                bundled != null ? bundledComplete : entry.ResourcePlacement?.BundledResourcesComplete ?? bundledComplete, relativeDirectory);
            ApplyPendingResourceHealthProjection(entry, out _, placement);
        }
    }

    /// <summary>各項目の警告件数を評価します。起動時読込みでは元配置結果も明示的に確定します。</summary>
    internal static int ApplyPendingResourceHealthProjectionToEntries(IEnumerable<PackageChartEntry> entries, bool initializeOriginal = false)
    {
        int warningEntryCount = 0;
        foreach (PackageChartEntry entry in entries ?? [])
        {
            if (entry?.Chart == null)
            {
                continue;
            }

            IReadOnlyList<ChartWarning> warnings = initializeOriginal ? InitializePendingResourceHealth(entry) : ApplyPendingResourceHealthProjection(entry);
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

    /// <summary>発見・復元した元packageの全定義を一回消費し、資源を持つ親子の禁止条件を確定します。通常資源・健康度は変更しません。</summary>
    internal static void CaptureResourceBearingNestedCharts(ChartPackage package)
    {
        List<PackageChartEntry> entries = package.ChartEntries;
        try
        {
            if (!package.HasCompleteChartDiscovery) { return; }
            var chartDirectories = new HashSet<string>(entries.Select(entry => Path.GetDirectoryName(entry.Chart.Path)), StringComparer.OrdinalIgnoreCase);
            if (chartDirectories.Count < 2) { return; }
            var resourceOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 子の資源を親へ計上しないため、全有効chartdirを固定してから最寄りの所有者と照合します。
            foreach (PackageChartEntry entry in entries)
            {
                string directory = Path.GetDirectoryName(entry.Chart.Path);
                if (resourceOwners.Contains(directory)) { continue; }
                foreach (ChartResourceReference reference in entry.AcquirePackageLayoutDefinitions())
                {
                    if (reference.Usage != ChartResourceUsage.Normal || reference.Status != ChartResourcePathNormalizationStatus.Valid) { continue; }
                    string[] extensions = reference.Kind switch
                    {
                        ChartResourceKind.Audio => ChartResourceExtensions.AudioExtensions,
                        ChartResourceKind.Image => ChartResourceExtensions.ImageExtensions,
                        ChartResourceKind.Movie => ChartResourceExtensions.MovieExtensions,
                        _ => []
                    };
                    if (extensions.Length == 0) { continue; }
                    // 入口で相対pathの「.」「..」を処理済みです。存在照合前の追加FullPath検査で従来の入力受理範囲を狭めません。
                    string owner = Path.GetDirectoryName(Path.Combine(directory, reference.NormalizedPath));
                    while (!string.IsNullOrEmpty(owner) && !chartDirectories.Contains(owner)) { owner = Path.GetDirectoryName(owner); }
                    if (string.Equals(owner, directory, StringComparison.OrdinalIgnoreCase)
                        && ChartResourceExistence.ExistsWithCompatibleExtensions(directory, reference.LookupKey, extensions))
                    {
                        resourceOwners.Add(directory);
                        break;
                    }
                }
            }
            package.HasResourceBearingNestedCharts = resourceOwners.Any(directory =>
            {
                for (string parent = Path.GetDirectoryName(directory); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                {
                    if (resourceOwners.Contains(parent)) { return true; }
                }
                return false;
            });
        }
        finally
        {
            foreach (PackageChartEntry entry in entries) { entry.ReleasePackageLayoutDefinitions(); }
        }
    }

    /// <summary>元packageの確定条件だけを確認します。DSTや警告文字列から条件を再構成せず、物理scopeの準備でも項目通知を発行しません。</summary>
    internal static bool IsInstallBlockedByResourceBearingNestedCharts(ChartPackage package)
        => package?.HasResourceBearingNestedCharts == true;

    /// <summary>
    /// パッケージ直下以外にある譜面へ一般警告を付与し、元package全体の導入禁止警告も維持します。
    /// </summary>
    /// <param name="package">判定対象の保留パッケージ。</param>
    /// <returns>入れ子譜面の警告を付与または維持した場合は true。</returns>
    internal static bool ApplyNestedChartFileWarnings(ChartPackage package)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.path) || !LongPathFileSystem.DirectoryExists(package.path))
        {
            return false;
        }

        bool blocked = IsInstallBlockedByResourceBearingNestedCharts(package);
        bool hasNestedChart = false;
        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            if (!IsNestedChartFileInPackage(package.path, entry?.Chart?.Path))
            {
                if (blocked) { entry.SetWarning(ChartWarningKind.ResourceBearingNestedCharts, Resources.Warning_ResourceBearingNestedCharts); }
                continue;
            }

            var nestedWarning = ChartWarning.Create(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
            // 一般警告の従来の分類置換と、重要警告の維持を同じ項目更新で確定します。
            entry.ReplaceWarningsByCategory(ChartWarningCategory.PackageLayout, blocked
                ? [nestedWarning, ChartWarning.Create(ChartWarningKind.ResourceBearingNestedCharts, Resources.Warning_ResourceBearingNestedCharts)]
                : [nestedWarning]);
            hasNestedChart = true;
        }

        return blocked || hasNestedChart;
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

    /// <summary>不採用のprivate展開先だけを回収し、通常の残留診断で独立入力の処理を止めません。</summary>
    private static void DeleteExtractedTemporaryDirectory(string tempDirectoryPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions, Action<string> logInfo)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !LongPathFileSystem.DirectoryExists(tempDirectoryPath))
            {
                return;
            }

            if (fileMutationService != null)
            {
                fileMutationService.DeleteDirectoryDirect(tempDirectoryPath, recursive: true, targetOnlyFileMutationOptions);
            }
            else
            {
                LongPathFileSystem.DeleteDirectory(tempDirectoryPath, recursive: true);
            }
        }
        catch (OutOfMemoryException exception)
        {
            Environment.FailFast("不採用アーカイブの回収中にメモリが不足しました。", exception);
            throw;
        }
        catch (Exception cleanupException)
        {
            logInfo?.Invoke("auto_install cleanup_failed path=" + tempDirectoryPath + " error=" + cleanupException.Message);
        }
    }

    /// <summary>
    /// 実在ディレクトリは名前によらず通常入力とし、対応拡張子の書庫を管理一時領域へ展開します。
    /// 書庫だけの進捗番号・総数を通知し、正常な管理元アーカイブだけを消費します。
    /// 展開失敗と必須日時復元失敗は対象を除外して診断し、独立した入力の処理を続けます。
    /// 診断先の通常故障は採否を変えず、捕捉したOOMは即時終了します。
    /// </summary>
    /// <param name="observeCreatedInput">作成直後・展開開始前に管理展開先を渡します。外側が準備途中の取消・例外でもその寿命を所有します。</param>
    internal List<string> ExpandInstallSourcesWithProgress(
        IEnumerable<string> installPaths,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInfo,
        IBmsLibraryDialogService dialogService,
        IPackageInstallProgressWriter progressWriter,
        CancellationToken token,
        Action<Action> deferPostCommitEffect,
        Action<string> observeCreatedInput = null)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        // 純粋な診断だけを局所化し、正常入力の採否や元の準備失敗を変えません。
        Action<string> logPreparation = message =>
        {
            try { logInfo?.Invoke(message); }
            catch (OutOfMemoryException exception)
            {
                Environment.FailFast("アーカイブ準備の診断中にメモリが不足しました。", exception);
                throw;
            }
            catch (Exception) { }
        };
        string[] archiveExtensions = [".zip", ".7z", ".rar", ".lzh"];
        // 書庫名の通常ディレクトリを除き、進捗の総数と各入力の処理に同じ分類を使います。
        (string Path, bool IsArchive)[] installSources = [.. (installPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => (path, archiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                && !LongPathFileSystem.DirectoryExists(path)))];
        int archiveSourceTotal = installSources.Count(source => source.IsArchive);
        int archiveSourceIndex = 0;
        List<string> expandedPaths = [];
        for (int i = 0; i < installSources.Length; i++)
        {
            (string installPath, bool isArchivePath) = installSources[i];
            if (token.IsCancellationRequested)
            {
                break;
            }
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
                    observeCreatedInput?.Invoke(extractedTempDirectoryPath);
                    logPreparation("auto_install extract_start path=" + installPath + " destination=" + extractedTempDirectoryPath);
                    var extractStopwatch = Stopwatch.StartNew();
                    List<ArchiveEntryMetadata> archiveEntries = SevenZipArchiveExtractor.ExtractArchiveEntries(installPath, extractedTempDirectoryPath);
                    extractStopwatch.Stop();
                    logPreparation("auto_install extract_done path=" + installPath + " destination=" + extractedTempDirectoryPath + " entries=" + archiveEntries.Count + " elapsedMs=" + extractStopwatch.ElapsedMilliseconds);
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
                                logPreparation("auto_install metadata_restore_required_failed archive=" + installPath + " path=" + fullPath + " reason=last_write_time_missing");
                                throw new RequiredArchiveMetadataRestoreException(fullPath, Resources.Warn_ArchiveLastWriteTimeMissingDetail);
                            }

                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, false, null, entry.LastWriteTime, targetOnlyFileMutationOptions);
                            }
                            catch (OutOfMemoryException exception)
                            {
                                Environment.FailFast("アーカイブの必須更新日時復元中にメモリが不足しました。", exception);
                                throw;
                            }
                            catch (Exception lastWriteTimeRestoreException)
                            {
                                logPreparation("auto_install metadata_restore_required_failed archive=" + installPath + " path=" + fullPath + " error=" + lastWriteTimeRestoreException.Message);
                                throw new RequiredArchiveMetadataRestoreException(fullPath, lastWriteTimeRestoreException.Message);
                            }
                        }
                        else if (isDirectory && entry.LastWriteTime > DateTime.MinValue)
                        {
                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, true, null, entry.LastWriteTime, targetOnlyFileMutationOptions);
                            }
                            catch (OutOfMemoryException exception)
                            {
                                Environment.FailFast("アーカイブのフォルダ更新日時復元中にメモリが不足しました。", exception);
                                throw;
                            }
                            catch (Exception directoryLastWriteRestoreException)
                            {
                                logPreparation("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=directory_last_write_time error=" + directoryLastWriteRestoreException.Message);
                            }
                        }

                        DateTime? creationTimeToRestore = entry.CreationTime > DateTime.MinValue ? entry.CreationTime : (DateTime?)null;
                        if (creationTimeToRestore.HasValue)
                        {
                            try
                            {
                                fileMutationService.SetTimestamps(fullPath, isDirectory, creationTimeToRestore, null, targetOnlyFileMutationOptions);
                            }
                            catch (OutOfMemoryException exception)
                            {
                                Environment.FailFast("アーカイブの作成日時復元中にメモリが不足しました。", exception);
                                throw;
                            }
                            catch (Exception creationTimeRestoreException)
                            {
                                logPreparation("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=creation_time error=" + creationTimeRestoreException.Message);
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
                            catch (OutOfMemoryException exception)
                            {
                                Environment.FailFast("アーカイブのアクセス日時復元中にメモリが不足しました。", exception);
                                throw;
                            }
                            catch (Exception lastAccessTimeRestoreException)
                            {
                                logPreparation("auto_install metadata_restore_optional_failed archive=" + installPath + " path=" + fullPath + " target=last_access_time error=" + lastAccessTimeRestoreException.Message);
                            }
                        }
                    }
                    expandedPaths.Add(extractedTempDirectoryPath);
                    extractedTempDirectoryPath = null;
                    TempDirectoryPublisher.TryDeleteManagedPath(
                        installPath,
                        logPreparation,
                        (path, cleanupException) =>
                        {
                            if (cleanupException is OutOfMemoryException)
                            {
                                Environment.FailFast("管理元アーカイブの回収中にメモリが不足しました。", cleanupException);
                            }
                            logPreparation("auto_install cleanup_failed path=" + path + " error=" + cleanupException.Message);
                        });
                }
            }
            catch (OutOfMemoryException exception)
            {
                Environment.FailFast("アーカイブ入力の準備中にメモリが不足しました。", exception);
                throw;
            }
            catch (RequiredArchiveMetadataRestoreException metadataRestoreException)
            {
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logPreparation);
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
                DeleteExtractedTemporaryDirectory(extractedTempDirectoryPath, fileMutationService, targetOnlyFileMutationOptions, logPreparation);
                sourceStopwatch.Stop();
                logPreparation("auto_install extract_failed path=" + installPath + " elapsedMs=" + sourceStopwatch.ElapsedMilliseconds + " error=" + ex.Message);
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
    /// 開始時の別保留入力を渡した場合は追加残存整理だけから保護します。
    /// 宛先未指定の単体譜面も新規フォルダへ導入します。単体入力の物理計画はそのファイルだけを含み、親や兄弟を変更しません。
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
        Action<Action> enqueueDiagnosticEffect = null,
        PendingInstallInputProtection pendingInputProtection = null)
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
            isPreflightRefusal: out _,
            pendingInputProtection: pendingInputProtection);
    }

    /// <summary>単体譜面・フォルダの読取り入力から新規導入フォルダを固定します。明示宛先は維持し、未指定時は同一要求の予約と既存実体を避けます。入力の物理的な範囲は変更しません。</summary>
    internal string PrepareInstallDestination(ChartPackage package, string installationDirectory,
        BmsLibraryOptionsSnapshot options, Func<IEnumerable<ChartFile>, string, string> createFolderPath,
        ISet<string> reservedDestinations)
    {
        if (!string.IsNullOrWhiteSpace(installationDirectory))
        { return installationDirectory; }
        bool isSingleFile = LongPathFileSystem.FileExists(package.path);
        if (!isSingleFile && !LongPathFileSystem.DirectoryExists(package.path))
        { return installationDirectory; }
        List<PackageChartEntry> entries = SelectInstallTargetEntries(package,
            new HashSet<string>([package.path], StringComparer.OrdinalIgnoreCase), package.path, isSingleFile);
        return ResolveNewInstallDestination(entries.Select(entry => entry.Chart), options, createFolderPath, reservedDestinations);
    }

    /// <summary>新規導入の命名・衝突回避を事前準備と直接実行で共用し、要求内の予約があれば同時に登録します。</summary>
    private static string ResolveNewInstallDestination(IEnumerable<ChartFile> charts,
        BmsLibraryOptionsSnapshot options, Func<IEnumerable<ChartFile>, string, string> createFolderPath,
        ISet<string> reservedDestinations = null)
    {
        string baseDirectory = createFolderPath?.Invoke(charts, options?.BMSInstallDir);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new IOException("The package destination could not be determined.");
        }
        string destination = baseDirectory;
        int suffix = 1;
        while (LongPathFileSystem.EntryExists(destination) || reservedDestinations?.Contains(destination) == true)
        { destination = baseDirectory + "(" + ++suffix + ")"; }
        reservedDestinations?.Add(destination);
        return destination;
    }

    /// <summary>
    /// 導入・フォルダ統合の session 向けに filesystem promotion までを実行します。
    /// canonical durable apply と source cleanup は outer session が所有し、ここでは実行しません。
    /// 開始時の別保留入力を渡した場合は追加残存整理だけから保護します。
    /// 新規宛先の準備は入力種別で共用しますが、単体譜面はそのファイルだけを新規フォルダへ配置します。
    /// </summary>
    /// <param name="onMutationScopePrepared">読み取りpreflightで実src/dstを確定した後、最初の物理副作用前に条件付きPを取得します。falseはBusyの継続可能な未処理、trueはcleanup終端まで保持する受理を表します。</param>
    /// <param name="preparedDestinationDirectory">同じ受理操作で停止前に固定した実宛先。指定時は名前計算を繰り返しません。</param>
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
        Action<Action> enqueueDiagnosticEffect = null,
        Func<string, string, bool> onMutationScopePrepared = null,
        string preparedDestinationDirectory = null,
        PendingInstallInputProtection pendingInputProtection = null)
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
            isPreflightRefusal: out bool isPreflightRefusal,
            onMutationScopePrepared: onMutationScopePrepared,
            preparedDestinationDirectory: preparedDestinationDirectory,
            pendingInputProtection: pendingInputProtection);
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
        out bool isPreflightRefusal,
        Func<string, string, bool> onMutationScopePrepared = null,
        string preparedDestinationDirectory = null,
        PendingInstallInputProtection pendingInputProtection = null)
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

            bool isAutoNaming = string.IsNullOrWhiteSpace(installationDirectory);
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
                destinationDirectory = preparedDestinationDirectory ?? ResolveNewInstallDestination(
                    installTargetEntries.Select(entry => entry.Chart), options, createFolderPath);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    throw new IOException("The package destination could not be determined.");
                }
                if (preparedDestinationDirectory != null && LongPathFileSystem.EntryExists(destinationDirectory))
                {
                    throw new IOException(string.Format(Resources.Warn_MoveDestAlreadyExists, sourcePath, destinationDirectory));
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
            ComponentMovePlanBuildResult componentPlan;
            List<KeyValuePair<string, string>> chartDestinationPaths;
            try
            {
                componentPlan = ValidatePackageDestinationTypes(
                    sourcePath,
                    isAutoNaming,
                    destinationDirectory,
                    installComponentFiles,
                    installTargetEntries,
                    componentExclusionPaths,
                    out chartDestinationPaths);
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

            if (onMutationScopePrepared != null && !onMutationScopePrepared(sourcePath, destinationDirectory))
            {
                isPreflightRefusal = true;
                return new FileDbMutationReceipt(emptyPlan.OperationId, FileDbMutationTerminalState.Failed,
                    false, 0, 0, [sourcePath], [destinationDirectory], [], [], [],
                    new InvalidOperationException(Resources.Warn_LibraryOperationBusy));
            }

            var mutationPaths = new List<FileDbMutationPathPlan>();
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

            if (isAutoNaming && isDirectory)
            {
                mutationPaths.Add(CreateMutationPathPlan(
                    sourcePath,
                    destinationDirectory,
                    isDirectory: true,
                    destinationExists: false,
                    reservedTemporaryPaths));
                foreach (KeyValuePair<string, string> chart in chartDestinationPaths)
                {
                    if (string.IsNullOrWhiteSpace(chart.Value))
                    {
                        throw new InvalidOperationException(
                            "The package-install preflight did not produce a destination for chart path: " + chart.Key);
                    }
                }
                sourceCleanupDirectories.Add(new FileDbMutationCleanupPathPlan(sourcePath, recursive: true));
            }
            else
            {
                componentPlan ??= BuildComponentMovePlan(installComponentFiles, destinationDirectory, componentExclusionPaths);
                (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) finalComponents = BuildFinalComponentMovePlan(componentPlan.PlanItems, chartDestinationPaths, destinationDirectory, options, logInstallPerformance);
                sourceCleanupFiles.AddRange(finalComponents.SourceCleanupFiles);
                foreach (ComponentMovePlanItem component in finalComponents.PlanItems)
                {
                    mutationPaths.Add(CreateMutationPathPlan(
                        component.SourcePath,
                        component.DestinationPath,
                        isDirectory: false,
                        destinationExists: LongPathFileSystem.EntryExists(component.DestinationPath),
                        reservedTemporaryPaths));
                }

                foreach (KeyValuePair<string, string> chart in chartDestinationPaths)
                {
                    if (string.IsNullOrWhiteSpace(chart.Value)) { continue; }
                    if (!LongPathFileSystem.FileExists(chart.Key))
                    {
                        throw new FileNotFoundException(Resources.Error_FileNotFound, chart.Key);
                    }
                    sourceCleanupFiles.Add(chart.Key);
                    EnsureMutationDestinationIsDistinct(chart.Key, chart.Value);
                    mutationPaths.Add(CreateMutationPathPlan(
                        chart.Key,
                        chart.Value,
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
                if (package.delete_parent && !isAutoNaming)
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
                        pendingInputProtection,
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

            // 導入先の共通値はdetached項目で準備します。DB確定前はlive entryの
            // 現在値と導入状態を変更せず、既存の変更sessionの確定後に反映します。
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
                            committedCharts => ApplyLivePackageInstallState(
                                package,
                                destinationMap,
                                liveInstallEntrySnapshots,
                                committedCharts),
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
                // preflightの投影は維持し、単独実行も物理配置の成功後に保存用Txtを確定します。
                IReadOnlyList<ChartFile> installedCharts =
                    Lr2TextGroupResolver.ResolveCurrentChartFlags(detachedPackageResult.AddedCharts);
                detachedPackageResult.AddedCharts.Clear();
                detachedPackageResult.AddedCharts.AddRange(installedCharts);
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
                                liveInstallEntrySnapshots,
                                detachedPackageResult.AddedCharts.ToDictionary(chart => (chart.Kind, chart.Path)));
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
        IEnumerable<(PackageChartEntry Entry, string SourcePath)> installTargetEntrySnapshots,
        IReadOnlyDictionary<(ChartFileKind Kind, string Path), ChartFile> committedCharts)
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
            string destinationPath = destinationMap.GetRequiredDestinationPath(entry.SourcePath);
            entry.Entry.ApplyCurrentChart(committedCharts[(entry.Entry.Chart.Kind, destinationPath)]);
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

        return ChartFileProjection.WithInstalledPath(source, installedPath);
    }

    private static bool TryBuildVerifiedResidualCleanupFiles(
        IEnumerable<string> sourceFilesBeforeMutation,
        IEnumerable<string> plannedSourceCleanupFiles,
        IInstalledChartLookupIndex independentlyOwnedLookup,
        IPrimaryHashLookup plannedDestinationHashes,
        IEnumerable<string> cleanupBoundaryDirectories,
        PackageSourceCleanupPolicy sourceCleanupPolicy,
        PendingInstallInputProtection pendingInputProtection,
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

        if (residualFiles.Any(path => pendingInputProtection?.IsReferencedByOtherPackage(path) == true))
        {
            reason = "residual_referenced_by_other_pending_package";
            return false;
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
                ? ChartLookupKey.GetPrimaryHash(BmsonChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(remainingFilePath)))
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
    /// 登録済みBMSルートと配下を除外して入力を発見し、捕捉した完全性設定で自動新規導入と保留へ分類します。
    /// 全定義による発見・グループ化を終え、同一または捕捉済みの親に包含されるパッケージをまとめてから、使用資源の件数と警告を確定します。
    /// </summary>
    /// <param name="fullySelectedDirectories">元入力で直下全項目を選択した不変集合です。nullだけが未捕捉で、既知空は確保先から再判定しません。</param>
    /// <param name="requireCompleteUsedResourcesForAutoInstall">音声に加え、使用画像・動画・補助画像も必須にする場合はtrue。今回の操作で捕捉済みの値を渡します。</param>
    public AutoInstallWorkflowResult PrepareAutoInstallWorkflow(
        IEnumerable<string> installPaths,
        IEnumerable<ChartPackage> currentPendingPackages,
        IEnumerable<string> registeredBmsRoots,
        Func<ChartFile, bool> isInstalledChart,
        IPrimaryHashLookup installedChartLookup = null,
        CancellationToken token = default, bool requireCompleteUsedResourcesForAutoInstall = false, IReadOnlySet<string> fullySelectedDirectories = null)
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
        fullySelectedDirectories ??= PackageInstallInputSelection.Capture(normalizedInstallPaths);
        var discoveries = new List<ChartPackageDiscoveryResult>();
        foreach (IGrouping<string, string> group in normalizedInstallPaths.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                // drive/share rootの明示directoryは、全項目選択の親がなくてもそれ自身を探索します。
                foreach (string directory in group.Where(LongPathFileSystem.DirectoryExists))
                {
                    discoveries.Add(DiscoverDirectory(directory, recursive: false, token: token));
                }
                continue;
            }
            if (fullySelectedDirectories.Contains(group.Key))
            {
                discoveries.Add(DiscoverDirectory(group.Key, recursive: false, token: token));
                continue;
            }
            List<string> chartPaths = [.. group.Where(path => LongPathFileSystem.FileExists(path) && ChartFileKindResolver.IsSupportedChartFilePath(path))];
            var direct = new ChartPackageDiscoveryResult();
            var directCharts = new List<PackageChartEntry>();
            bool promote = false;
            string[] parentEntries = null;
            foreach (string path in chartPaths)
            {
                var entry = PackageChartEntry.FromPath(path, out string preview, out Exception failure);
                if (failure != null) { direct.Failures.Add(failure); }
                if (entry == null) { direct.IsComplete = false; continue; }
                directCharts.Add(entry);
                promote = promote || HasExistingPackageChartResources(entry) || HasExistingPreviewResource(entry, preview);
            }
            if (direct.IsComplete && chartPaths.Count > 0)
            {
                try
                {
                    parentEntries = [.. LongPathFileSystem.EnumerateFileSystemEntries(group.Key, "*", System.IO.SearchOption.TopDirectoryOnly)];
                    promote |= HasAdjacentPreviewAudio(parentEntries.Where(LongPathFileSystem.FileExists));
                }
                catch (Exception exception) { direct.IsComplete = false; direct.Failures.Add(exception); }
            }
            if (promote && direct.IsComplete)
            {
                discoveries.Add(DiscoverDirectory(group.Key, recursive: false, directCharts, parentEntries, token));
            }
            else
            {
                if (direct.IsComplete)
                {
                    direct.Packages.AddRange(directCharts.Select(entry => CreatePackageWithKnownCharts(entry.Chart.Path, deleteParent: false, [entry])));
                }
                if (!direct.IsComplete) { direct.IncompleteScopes.Add(group.Key); }
                discoveries.Add(direct);
                if (direct.IsComplete)
                {
                    foreach (string directory in group.Where(LongPathFileSystem.DirectoryExists))
                    {
                        discoveries.Add(DiscoverDirectory(directory, recursive: false, token: token));
                    }
                }
            }
        }
        List<ChartPackage> discoveredPackages = [.. discoveries.SelectMany(discovery => discovery.Packages)];
        result.DiscoveryFailures.AddRange(discoveries.SelectMany(discovery => discovery.Failures));
        discoveryStopwatch.Stop();
        result.DiscoveryMs = discoveryStopwatch.ElapsedMilliseconds;

        List<string> libraryDirectories = [.. (registeredBmsRoots ?? []).Where(dir => !string.IsNullOrWhiteSpace(dir))];
        List<ChartPackage> pendingPackages = [.. (currentPendingPackages ?? []).Where(pkg => pkg != null)];
        // 親フォルダと配下譜面の両入力が同じパッケージを発見するため、作成時に正規化した物理パスで一回にまとめる。
        // 先行導入後の所有判定へ二重に渡すと、既に消費した入力を保留として登録してしまう。
        discoveredPackages = [.. discoveredPackages
            .Where(pkg => pkg != null)
            .DistinctBy(pkg => pkg.path, StringComparer.OrdinalIgnoreCase)
            .Where(pkg => !libraryDirectories.Any(dir =>
                LongPathFileSystem.IsSameOrDescendantDirectoryPath(pkg.path, dir)))
            .Where(newPkg => !pendingPackages.Any(oldPkg => !string.IsNullOrWhiteSpace(oldPkg.path) && newPkg.path.Equals(oldPkg.path, StringComparison.OrdinalIgnoreCase)))
            .Where(newPkg => !pendingPackages.Any(oldPkg => !string.IsNullOrWhiteSpace(oldPkg.path) && LongPathFileSystem.DirectoryExists(oldPkg.path) && newPkg.path.StartsWith(oldPkg.path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))];
        RemovePackagesContainedInDiscoveredDirectories(discoveredPackages);
        result.DiscoveredPackages.AddRange(discoveredPackages);
        if (discoveredPackages.Count == 0)
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }

        List<string> incompleteScopes = [.. discoveries.SelectMany(discovery => discovery.IncompleteScopes)];
        foreach (ChartPackage directory in discoveredPackages.Where(package => LongPathFileSystem.DirectoryExists(package.path)))
        {
            List<ChartPackage> replaced = [.. pendingPackages.Where(old => IsSameOrWithinPath(old.path, directory.path)
                && !string.Equals(old.path, directory.path, StringComparison.OrdinalIgnoreCase))];
            if (replaced.Count > 0) { result.PendingPackageReplacements.Add(directory, replaced); }
        }
        if (discoveredPackages.Any(package => LongPathFileSystem.DirectoryExists(package.path)))
        {
            result.PendingPackagesToRemove.AddRange(pendingPackages.Where(package => !LongPathFileSystem.EntryExists(package.path)
                && !incompleteScopes.Any(scope => IsSameOrWithinPath(package.path, scope))
                && !result.PendingPackageReplacements.Values.Any(group => group.Contains(package))));
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
        var replacedEntriesByPath = new Dictionary<string, PackageChartEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageChartEntry entry in result.PendingPackageReplacements.Values.SelectMany(packages => packages)
            .SelectMany(package => package.ChartEntries))
        {
            if (!string.IsNullOrWhiteSpace(entry.Chart.Path)) { replacedEntriesByPath.TryAdd(entry.Chart.Path, entry); }
        }
        foreach (ChartPackage pkg in discoveredPackages)
        {
            bool isSingleFilePackage = !LongPathFileSystem.DirectoryExists(pkg.path);
            CaptureResourceBearingNestedCharts(pkg);
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
            foreach (PackageChartEntry entry in pkg.ChartEntries)
            {
                ApplyPendingResourceHealthProjection(entry, out ResourceHealthMaintenanceSnapshot health);
                entry.InitializeOriginalResourceHealth(health);
                if (replacedEntriesByPath.TryGetValue(entry.Chart.Path, out PackageChartEntry previous)
                    && !string.IsNullOrWhiteSpace(entry.Chart.Sha256)
                    && string.Equals(previous.Chart.Sha256, entry.Chart.Sha256, StringComparison.OrdinalIgnoreCase)
                    && previous.Chart.ResourceHealthMaintenanceSnapshot?.EncodingFixed == true)
                {
                    entry.ApplyCommittedEncoding(previous.Chart);
                }
                if (!HasCompleteUsedAudio(health) || (requireCompleteUsedResourcesForAutoInstall && !HasCompleteUsedResources(health)))
                {
                    pendingByPackage[pkg] = true;
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

    /// <summary>同じ物理scopeと要素境界付き配下だけを照合します。</summary>
    private static bool IsSameOrWithinPath(string path, string directory)
        => !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(directory)
            && (string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>既存保留・登録ルートの除外後、実際に全譜面を捕捉した親がある子だけを同じ要求から除きます。入力順に依存せず、親の通常分類を維持します。</summary>
    private static void RemovePackagesContainedInDiscoveredDirectories(List<ChartPackage> packages)
    {
        if (packages.Count < 2) { return; }
        var directoryPackages = packages
            .Where(package => LongPathFileSystem.DirectoryExists(package.path))
            .ToDictionary(package => package.path, StringComparer.OrdinalIgnoreCase);
        Dictionary<ChartPackage, HashSet<string>> chartPathsByAncestor = [];
        packages.RemoveAll(IsContained);

        bool IsContained(ChartPackage package)
        {
            List<PackageChartEntry> entries = null;
            // 全packageの組合せを比較せず、実際に重なる祖先だけの譜面集合を操作内で共有する。
            for (string ancestor = Path.GetDirectoryName(package.path); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
            {
                if (!directoryPackages.TryGetValue(ancestor, out ChartPackage parent)) { continue; }
                entries ??= package.ChartEntries;
                if (entries.Count == 0) { return false; }
                if (!chartPathsByAncestor.TryGetValue(parent, out HashSet<string> chartPaths))
                {
                    chartPaths = new HashSet<string>(parent.ChartEntries.Select(entry => entry?.Chart?.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
                    chartPathsByAncestor.Add(parent, chartPaths);
                }
                if (entries.All(entry => !string.IsNullOrWhiteSpace(entry?.Chart?.Path) && chartPaths.Contains(entry.Chart.Path)))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>取得済みの使用音源件数で完全性を判断し、要求0件も充足として扱います。</summary>
    internal static bool HasCompleteUsedAudio(ResourceHealthMaintenanceSnapshot health)
    {
        return health?.WavFilesDefined.HasValue == true && health.WavFilesExisting == health.WavFilesDefined;
    }

    private static bool HasCompleteUsedResources(ResourceHealthMaintenanceSnapshot health)
    {
        return HasCompleteUsedAudio(health) && health.BgaFilesDefined.HasValue && health.BgaFilesExisting == health.BgaFilesDefined
            && health.MovieFilesDefined.HasValue && health.MovieFilesExisting == health.MovieFilesDefined
            && health.GetStagefileHealth() == true && health.GetBannerHealth() == true && health.GetBackbmpHealth() == true;
    }

    private static bool HasAlreadyInstalledWarning(PackageChartEntry entry)
    {
        return (entry?.Chart?.Warnings ?? []).Any(warning => warning?.Kind == ChartWarningKind.AlreadyInstalled);
    }

    /// <summary>
    /// auto-install 候補を一つの operation-scoped mutation session へ逐次追加します。
    /// 後続候補の重複判定には、この session で実際に physical success した chart のみを使用します。
    /// 推定候補は初期分類で保留となった対象に限定し、新規導入候補の保留登録では広げません。
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

        foreach ((ChartPackage replacement, List<ChartPackage> sources) in workflow.PendingPackageReplacements)
        {
            bool physicallyInstalled = result.AutoInstalledPackages.Contains(replacement) && !workflow.BusyPackages.Contains(replacement);
            bool acceptedPending = !token.IsCancellationRequested && pendingPackagesToAdd.Contains(replacement)
                && (workflow.PendingPackagesToAdd.Contains(replacement) || keepInstallablePackagesPending || !canAutoInstallImmediately)
                && !workflow.BusyPackages.Contains(replacement);
            if (physicallyInstalled || acceptedPending)
            {
                result.PendingPackagesToRemove.AddRange(sources);
                result.InstallRowsToDelete.AddRange(sources.Select(package => package.path));
            }
            else
            {
                pendingPackagesToAdd.Remove(replacement);
                result.AutoInstalledPackages.Remove(replacement);
            }
        }
        var applyStopwatch = Stopwatch.StartNew();
        result.PendingPackagesToAdd.AddRange(pendingPackagesToAdd);
        result.InstallRowsToUpsert.AddRange(pendingPackagesToAdd.Where(package => !string.IsNullOrWhiteSpace(package.path)));
        result.EstimateTargets.AddRange(workflow.PendingPackagesToAdd.Where(package => package != null && pendingPackagesToAdd.Contains(package)));
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
    /// 現在の保留照合は要求対象だけの索引で一回行います。入力順重複排除、全 DST 空候補の除外は短い
    /// model guard の保持中に行い、package 分類と component 列挙は
    /// package ごとの実行段階まで遅延します。
    /// </summary>
    /// <param name="requestedPackages">ユーザーが指定した pending package の順序付き一覧です。</param>
    /// <param name="currentPendingPackages">一回列挙して要求照合と元入力範囲の捕捉に共用する全保留です。選択外・後続も追加残存整理から保護します。</param>
    /// <param name="installedChartLookup">開始時に所持している譜面 hash の lookup です。</param>
    public PendingInstallBatchPlan BuildEstimatedInstallBatchPlan(
        IEnumerable<ChartPackage> requestedPackages,
        IEnumerable<ChartPackage> currentPendingPackages,
        IPrimaryHashLookup installedChartLookup)
    {
        var planStopwatch = Stopwatch.StartNew();
        var plan = new PendingInstallBatchPlan();
        var filterStopwatch = Stopwatch.StartNew();
        List<ChartPackage> requested = DeduplicatePackagesByPathOrReference(requestedPackages);
        var pendingByRequest = new Dictionary<ChartPackage, ChartPackage>(ReferenceEqualityComparer.Instance);
        var requestedByPath = new Dictionary<string, ChartPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage package in requested)
        {
            pendingByRequest.Add(package, null);
            if (!string.IsNullOrWhiteSpace(package.path)) { requestedByPath.Add(package.path, package); }
        }
        List<PendingInstallInputReference> inputReferences = [];
        foreach (ChartPackage package in currentPendingPackages ?? [])
        {
            if (package == null) { continue; }
            if (!string.IsNullOrWhiteSpace(package.path))
            {
                inputReferences.Add(new PendingInstallInputReference(
                    package, package.path, LongPathFileSystem.DirectoryExists(package.path)));
            }
            // 参照かパスで最初に一致した保留を保持します。後続の参照一致で先行パス一致を置換しません。
            if (pendingByRequest.TryGetValue(package, out ChartPackage referenceMatch) && referenceMatch == null)
            { pendingByRequest[package] = package; }
            if (!string.IsNullOrWhiteSpace(package.path)
                && requestedByPath.TryGetValue(package.path, out ChartPackage pathRequest)
                && pendingByRequest[pathRequest] == null)
            { pendingByRequest[pathRequest] = package; }
        }
        plan.InputReferences = inputReferences.ToArray();
        var selectedReferences = new HashSet<ChartPackage>();
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage requestedPackage in requested)
        {
            ChartPackage pendingPackage = pendingByRequest[requestedPackage];
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

    /// <summary>実行時と同じ分類で、変更不要な候補を除いた物理scopeを副作用なしで準備します。先行成功による所持変化は実行時に再分類します。</summary>
    internal IReadOnlyDictionary<ChartPackage, IReadOnlyList<string>> PrepareEstimatedInstallPhysicalScopes(
        PendingInstallBatchPlan plan, bool deleteSource, Func<ChartPackage, string, ISet<string>, int> countComponents)
    {
        Dictionary<ChartPackage, IReadOnlyList<string>> scopes = [];
        foreach (ChartPackage package in plan.SelectedPendingPackages)
        {
            PendingInstallBatchItem item = PrepareEstimatedInstallBatchItem(package, plan.IndependentOwnershipLookup, applyWarnings: false);
            if (item == null || (item.IsResourceOnlyInstall && !deleteSource
                && countComponents(item.InstallWorkPackage, item.DestinationDirectory, item.ExcludedComponentPaths) == 0)) { continue; }
            scopes.Add(package, package.delete_parent
                ? [package.path, item.DestinationDirectory, Path.GetDirectoryName(package.path)]
                : [package.path, item.DestinationDirectory]);
        }
        return scopes;
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
        Func<ChartPackage, string, ISet<string>, int> countComponentMoveTargets = null,
        CancellationToken token = default)
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
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                result.UnprocessedPackages.AddRange(plan.SelectedPendingPackages.Skip(packageIndex).Where(package => package != null));
                break;
            }
            ChartPackage originalPackage = plan.SelectedPendingPackages[packageIndex];
            if (originalPackage == null)
            {
                continue;
            }
            if (IsInstallBlockedByResourceBearingNestedCharts(originalPackage))
            {
                result.FailedPackages.Add(originalPackage);
                logInfo?.Invoke("estimated_install blocked_resource_bearing_nested path=" + originalPackage.path);
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
        IPrimaryHashLookup installedHashes, bool applyWarnings = true)
    {
        if (IsInstallBlockedByResourceBearingNestedCharts(originalPackage)) { return null; }
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
        if (applyWarnings) { ApplyAlreadyInstalledWarning(installedInLibraryEntries); }

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
            if (IsInstallBlockedByResourceBearingNestedCharts(package))
            {
                result.FailedPackages.Add(package);
                continue;
            }
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
    /// 新規としての導入を承認された package を一つの operation-scoped install session へ追加します。
    /// 確認コールバックは導入先の有無にかかわらず評価し、拒否された対象は変更しません。
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

            if (IsInstallBlockedByResourceBearingNestedCharts(pendingPackage))
            {
                result.Processed++;
                result.Failed++;
                logInfo?.Invoke("force_install_batch blocked_resource_bearing_nested path=" + pendingPackage.path);
                continue;
            }

            // 導入先未設定でも利用者が新規導入を拒否できるため、全対象で承認結果を守ります。
            if (confirmNormalInstallOverride != null
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

            if (IsInstallBlockedByResourceBearingNestedCharts(pendingPackage))
            {
                result.Failed++;
                result.Processed++;
                logInfo?.Invoke("advanced_pending_resource_overwrite blocked_resource_bearing_nested path=" + pendingPackage.path);
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
        Func<ChartFile, string, RenameInvalidExtensionOutcome> processRename,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        var result = new PendingZeroNoteRenameResult();
        List<ChartFile> files = DeduplicateBmsFormatChartsByPathOrBmsReference(targetCharts);
        result.Total = files.Count;
        foreach (ChartFile file in files)
        {
            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }
            string extension = Path.GetExtension(file.Path);
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
                logInfo?.Invoke("advanced_pending_zero_note_rename skip_unsupported_ext path=" + file.Path + " ext=" + extension);
                onEachProcessed?.Invoke();
                continue;
            }
            bool isZeroNote;
            try
            {
                isZeroNote = BmsChartFileParser.IsZeroNote(file.Path);
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
                logInfo?.Invoke("advanced_pending_zero_note_rename skip_not_zero path=" + file.Path);
                onEachProcessed?.Invoke();
                continue;
            }
            result.ZeroNote++;
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.Path), Path.GetFileNameWithoutExtension(file.Path) + targetExtension);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.Path);
                    result.Renamed++;
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.Path);
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
        Func<ChartFile, string, RenameInvalidExtensionOutcome> processRename)
    {
        var result = new PendingExtensionRenameResult();
        var stopwatch = Stopwatch.StartNew();
        List<ChartFile> files = [.. DeduplicateBmsFormatChartsByPathOrBmsReference(targetCharts).Where(file => LongPathFileSystem.FileExists(file.Path))];
        result.Total = files.Count;
        foreach (ChartFile file in files)
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.Path), Path.GetFileNameWithoutExtension(file.Path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    result.Renamed++;
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.Path);
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    result.DuplicateDeleted++;
                    AddChartPathToRemove(result.ChartPathsToRemove, file?.Path);
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
    /// 選択した保留譜面を削除します。フォルダ削除指定時は対象を再走査し、全譜面の読取り成功と全選択を確認できたフォルダだけを資源ごと削除します。
    /// 同じ確認済み集合を削除結果へ返し、フォルダ削除失敗時は個別削除へ進みません。単体入力や確認できないフォルダでは選択ファイルだけを削除します。
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
            // 選択元の祖先だけを一度集め、無関係な保留フォルダの再走査と選択数×パッケージ数の照合を避ける。
            // 文字列操作に限定し、無効な選択パスも後段の個別検査へ渡す。
            var selectedAncestorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string selectedPath in selectedPaths)
            {
                string ancestorPath = selectedPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                int separatorIndex = ancestorPath.LastIndexOf(Path.DirectorySeparatorChar);
                while (separatorIndex >= 0)
                {
                    ancestorPath = ancestorPath[..separatorIndex];
                    if (!selectedAncestorPaths.Add(ancestorPath))
                    {
                        break;
                    }
                    separatorIndex = ancestorPath.LastIndexOf(Path.DirectorySeparatorChar);
                }
            }
            foreach (ChartPackage package in (pendingPackages ?? []).Where(package => package != null))
            {
                if (string.IsNullOrWhiteSpace(package.path)
                    || !selectedAncestorPaths.Contains(package.path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar))
                    || !LongPathFileSystem.DirectoryExists(package.path))
                {
                    continue;
                }
                PackageChartDiscoverySnapshot discovery = PackageInstallEstimationSnapshotBuilder.BuildPackageChartDiscoverySnapshot(package.path);
                if (!discovery.IsComplete)
                {
                    continue;
                }
                List<string> packageChartPaths = [.. discovery.ChartEntries
                    .Select(entry => entry.Chart.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                if (packageChartPaths.Count == 0 || !packageChartPaths.All(selectedPaths.Contains))
                {
                    continue;
                }
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
