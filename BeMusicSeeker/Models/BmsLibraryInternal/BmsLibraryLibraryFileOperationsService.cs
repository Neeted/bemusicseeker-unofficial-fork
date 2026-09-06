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

internal sealed class MovedFolderReferenceUpdateResult
{
    public DirectoryResourceLookupCache.ReverseLookupMutationResult MutationResult { get; set; } = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;

    public int MoveCount { get; set; }

    public int LookupKeyCount { get; set; }

    public int MatchedKeyCount { get; set; }

    public long ElapsedMs { get; set; }
}

/// <summary>
/// Captures the identity needed by a legacy chart-file command while keeping
/// the storage owner available only to the command owner for the later catalog
/// delta.  The filesystem executor receives only the immutable projection.
/// </summary>
internal sealed class LibraryFileOperationTargetSnapshot
{
    private LibraryFileOperationTargetSnapshot(
        ChartFile chartSnapshot,
        BMSFile bmsOwner,
        LR2SongDBExtended.bmson_song bmsonOwner,
        string sourcePath,
        string md5,
        string sha256,
        bool sourceFileExisted,
        bool sourceFileIsReparsePoint,
        bool sourceFileSafetyFactsAvailable)
    {
        ChartSnapshot = chartSnapshot;
        BmsOwner = bmsOwner;
        BmsonOwner = bmsonOwner;
        SourcePath = sourcePath;
        Md5 = md5;
        Sha256 = sha256;
        SourceFileExisted = sourceFileExisted;
        SourceFileIsReparsePoint = sourceFileIsReparsePoint;
        SourceFileSafetyFactsAvailable = sourceFileSafetyFactsAvailable;
    }

    internal ChartFile ChartSnapshot { get; }

    internal BMSFile BmsOwner { get; }

    internal LR2SongDBExtended.bmson_song BmsonOwner { get; }

    internal string SourcePath { get; }

    internal string Md5 { get; }

    internal string Sha256 { get; }

    internal bool SourceFileExisted { get; }

    internal bool SourceFileIsReparsePoint { get; }

    internal bool SourceFileSafetyFactsAvailable { get; }

    internal ChartFileKind Kind => ChartSnapshot?.Kind ?? (BmsonOwner != null ? ChartFileKind.Bmson : ChartFileKind.Bms);

    internal string PrimaryHash => !string.IsNullOrWhiteSpace(Md5) ? Md5 : Sha256;

    internal static LibraryFileOperationTargetSnapshot FromChart(ChartFile chart, bool captureSourceFileExistence = true)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        bool sourceFileExisted = !captureSourceFileExistence || LongPathFileSystem.FileExists(chart.Path);
        bool sourceFileIsReparsePoint = false;
        bool sourceFileSafetyFactsAvailable = !captureSourceFileExistence || !sourceFileExisted;
        if (captureSourceFileExistence && sourceFileExisted)
        {
            try
            {
                sourceFileIsReparsePoint = (LongPathFileSystem.GetAttributes(chart.Path) & FileAttributes.ReparsePoint) != 0;
                sourceFileSafetyFactsAvailable = true;
            }
            catch (Exception ex) when (ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException
                || ex is SecurityException)
            {
                sourceFileSafetyFactsAvailable = false;
            }
        }
        return new LibraryFileOperationTargetSnapshot(
            ChartFileProjection.ToImmutableSnapshot(chart),
            bmsOwner,
            bmsonOwner,
            chart.Path,
            chart.Md5,
            chart.Sha256,
            sourceFileExisted,
            sourceFileIsReparsePoint,
            sourceFileSafetyFactsAvailable);
    }

    internal bool HasSameLiveIdentity(ChartFile chart)
    {
        if (chart == null || Kind != chart.Kind)
        {
            return false;
        }

        BMSFile currentBmsOwner = chart.GetBmsStorageOwner();
        if (BmsOwner != null || currentBmsOwner != null)
        {
            if (!ReferenceEquals(BmsOwner, currentBmsOwner))
            {
                return false;
            }
        }
        LR2SongDBExtended.bmson_song currentBmsonOwner = chart.GetBmsonStorageOwner();
        if (BmsonOwner != null || currentBmsonOwner != null)
        {
            if (!ReferenceEquals(BmsonOwner, currentBmsonOwner))
            {
                return false;
            }
        }

        string currentPath = currentBmsOwner?.path ?? currentBmsonOwner?.path ?? chart.Path;
        string currentMd5 = currentBmsOwner?.hash ?? currentBmsonOwner?.md5 ?? chart.Md5;
        string currentSha256 = currentBmsOwner?.sha256 ?? currentBmsonOwner?.sha256 ?? chart.Sha256;
        return string.Equals(SourcePath, currentPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Md5 ?? string.Empty, currentMd5 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Sha256 ?? string.Empty, currentSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal bool TryValidateCurrentSource(out Exception failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            failure = new InvalidOperationException("Pending chart source path is empty.");
            return false;
        }
        if (!LongPathFileSystem.FileExists(SourcePath))
        {
            failure = new FileNotFoundException("Pending chart source file was not found.", SourcePath);
            return false;
        }
        try
        {
            FileAttributes attributes = LongPathFileSystem.GetAttributes(SourcePath);
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

    internal static bool HasSameIdentity(
        LibraryFileOperationTargetSnapshot expected,
        LibraryFileOperationTargetSnapshot actual)
    {
        if (expected == null || actual == null
            || expected.Kind != actual.Kind
            || !string.Equals(expected.SourcePath, actual.SourcePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Md5 ?? string.Empty, actual.Md5 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Sha256 ?? string.Empty, actual.Sha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (expected.BmsOwner != null || actual.BmsOwner != null)
        {
            return ReferenceEquals(expected.BmsOwner, actual.BmsOwner);
        }
        if (expected.BmsonOwner != null || actual.BmsonOwner != null)
        {
            return ReferenceEquals(expected.BmsonOwner, actual.BmsonOwner);
        }
        return true;
    }
}

/// <summary>
/// Immutable source/path facts consumed by the invalid-extension executor.
/// No chart, package, collection, or storage-owner reference is retained.
/// </summary>
internal sealed class LegacyInvalidExtensionRenamePlan
{
    internal LegacyInvalidExtensionRenamePlan(IEnumerable<LegacyInvalidExtensionRenamePlanItem> items)
    {
        Items = Array.AsReadOnly([.. (items ?? []).Where(item => item != null)]);
    }

    internal IReadOnlyList<LegacyInvalidExtensionRenamePlanItem> Items { get; }
}

internal sealed class LegacyInvalidExtensionRenamePlanItem
{
    internal LegacyInvalidExtensionRenamePlanItem(
        string sourcePath,
        string requestedPath,
        string sourceHashHint,
        bool rejectUnsafeSource)
    {
        SourcePath = sourcePath;
        RequestedPath = requestedPath;
        SourceHashHint = sourceHashHint;
        RejectUnsafeSource = rejectUnsafeSource;
    }

    internal string SourcePath { get; }

    internal string RequestedPath { get; }

    internal string SourceHashHint { get; }

    internal bool RejectUnsafeSource { get; }
}

internal sealed class LegacyInvalidExtensionRenameExecutionResult
{
    internal List<LegacyInvalidExtensionRenameExecutionItem> Items { get; } = [];

    internal int RenamedCount { get; set; }

    internal int DuplicateDeletedCount { get; set; }

    internal int SkippedCount { get; set; }

    internal int FailedCount { get; set; }

    internal long TotalMs { get; set; }
}

internal sealed class LegacyInvalidExtensionRenameExecutionItem
{
    internal LegacyInvalidExtensionRenameExecutionItem(
        LegacyInvalidExtensionRenamePlanItem planItem,
        RenameInvalidExtensionOutcome outcome)
    {
        PlanItem = planItem;
        Outcome = outcome;
    }

    internal LegacyInvalidExtensionRenamePlanItem PlanItem { get; }

    internal RenameInvalidExtensionOutcome Outcome { get; }
}

/// <summary>
/// Immutable legacy library-removal plan.  The executor consumes path/kind
/// facts only; the owner binds successful indexes back to current storage
/// owners after the filesystem phase.
/// </summary>
internal sealed class LibraryChartRemovalPlan
{
    internal IReadOnlyList<LibraryChartRemovalPlanTarget> Targets { get; init; } = [];

    internal IReadOnlyList<LibraryChartRemovalPlanFolder> Folders { get; init; } = [];

    internal IReadOnlyList<LibraryChartRemovalInstallDestinationTarget> InstallDestinationTargets { get; init; } = [];
}

internal sealed class LibraryChartRemovalPlanTarget
{
    internal int Index { get; init; }

    internal LibraryChartKind Kind { get; init; }

    internal string Path { get; init; }

    internal string Md5 { get; init; }

    internal string Sha256 { get; init; }
}

internal sealed class LibraryChartRemovalPlanFolder
{
    internal string Path { get; init; }

    internal bool DeleteWholeFolder { get; init; }

    internal IReadOnlyList<int> TargetIndexes { get; init; } = [];
}

internal sealed class LibraryChartRemovalInstallDestinationTarget
{
    internal string FolderPath { get; init; }

    internal LibraryChartKind Kind { get; init; }

    internal string Path { get; init; }

    internal string Md5 { get; init; }

    internal string Sha256 { get; init; }

    internal bool IsPendingPackageEntry { get; init; }
}

internal sealed class LibraryChartRemovalExecutionResult
{
    /// <summary>Filesystem facts recorded at the existing API call sites, without additional probes.</summary>
    internal List<LibraryChartRemovalTarget> Targets { get; } = [];

    internal List<int> RemovedTargetIndexes { get; } = [];

    internal List<string> DeletedFolderPaths { get; } = [];

    internal List<LibraryDeleteFailure> Failures { get; } = [];

    internal int FolderDeleteCount { get; set; }

    internal int FileDeleteCount { get; set; }
}

/// <summary>
/// Builds and executes file-system mutations against snapshots owned by BMSLibrary.
/// The facade must acquire the required locks before invoking this service.
/// </summary>
internal sealed class BmsLibraryLibraryFileOperationsService
{
    public void MoveFolder(
        string srcDir,
        string dstDir,
        IFileMutationService fileMutationService,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        fileMutationService.MoveDirectory(srcDir, dstDir, overwrite: false, recursiveDirectoryTreeFileMutationOptions);
    }

    /// <summary>
    /// destination filesystem 内で完結する folder move の immutable preflight を作成します。
    /// </summary>
    public FileDbMutationPlan BuildFolderMoveMutationPlan(
        string srcDir,
        string dstDir)
    {
        if (string.IsNullOrWhiteSpace(srcDir))
        {
            throw new ArgumentException("A source directory is required.", nameof(srcDir));
        }
        if (string.IsNullOrWhiteSpace(dstDir))
        {
            throw new ArgumentException("A destination directory is required.", nameof(dstDir));
        }
        if (LongPathFileSystem.EntryExists(dstDir))
        {
            throw new IOException("Destination directory already exists.");
        }
        string stagingPath = LongPathFileSystem.CreateMutationSiblingPath(dstDir, "stage");
        return new FileDbMutationPlan(
            Guid.NewGuid(),
            [new FileDbMutationPathPlan(srcDir, dstDir, stagingPath, string.Empty, isDirectory: true)],
            [],
            [new FileDbMutationCleanupPathPlan(srcDir, recursive: true)],
            recursiveSourceCleanup: true);
    }

    /// <summary>
    /// Builds the path-only portion of a legacy library removal.  Canonical
    /// owner binding is intentionally left to the command owner so this plan
    /// can be executed after every model snapshot guard has been released.
    /// </summary>
    internal LibraryChartRemovalPlan BuildLibraryChartRemovalPlan(
        IEnumerable<LibraryChartRef> canonicalCharts,
        ILibraryChartCanonicalLookup libraryChartLookup,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        libraryChartLookup ??= LibraryChartRefIndexSnapshot.Empty;
        List<LibraryChartRef> targets = [.. (canonicalCharts ?? [])
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        var planTargets = new List<LibraryChartRemovalPlanTarget>(targets.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            LibraryChartRef chart = targets[index];
            planTargets.Add(new LibraryChartRemovalPlanTarget
            {
                Index = index,
                Kind = chart.Kind,
                Path = chart.Path,
                Md5 = chart.Md5,
                Sha256 = chart.Sha256
            });
        }

        var approvedPaths = new HashSet<string>(
            (approvedWholeFolderDeletePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderPlans = new List<LibraryChartRemovalPlanFolder>();
        var installDestinationTargets = new List<LibraryChartRemovalInstallDestinationTarget>();
        foreach (IGrouping<string, LibraryChartRef> folderGroup in from groupedFiles in targets
                                                                   .GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
                                                                   orderby groupedFiles.Key.Length descending
                                                                   select groupedFiles)
        {
            bool canDeleteWholeFolder = libraryChartLookup.CountChartRefsUnderRealPath(folderGroup.Key, selectedPaths) == folderGroup.Count();
            bool deleteWholeFolder = canDeleteWholeFolder && approvedPaths.Contains(folderGroup.Key);
            // Resolve each target by its path and kind so the executor never
            // needs to retain the canonical lookup or a live owner reference.
            IReadOnlyList<int> targetIndexes = Array.AsReadOnly(folderGroup
                .Select(chart => targets.FindIndex(candidate => ReferenceEquals(candidate, chart)
                    || (candidate.Kind == chart.Kind
                        && string.Equals(candidate.Path, chart.Path, StringComparison.OrdinalIgnoreCase))))
                .Where(index => index >= 0)
                .ToArray());
            folderPlans.Add(new LibraryChartRemovalPlanFolder
            {
                Path = folderGroup.Key,
                DeleteWholeFolder = deleteWholeFolder,
                TargetIndexes = targetIndexes
            });
            foreach (LibraryChartRef chart in folderGroup)
            {
                selectedPaths.Add(chart.Path);
            }

            if (!deleteWholeFolder)
            {
                continue;
            }

            foreach (LibraryInstallDestinationChange target in EnumerateInstallDestinationTargetsUnderFolder(
                pendingPackages,
                installDestinationOverlayCharts,
                folderGroup.Key))
            {
                ChartFile chart = target.Entry?.Chart ?? target.Chart;
                if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
                {
                    continue;
                }
                installDestinationTargets.Add(new LibraryChartRemovalInstallDestinationTarget
                {
                    FolderPath = folderGroup.Key,
                    Kind = chart.Kind == ChartFileKind.Bmson ? LibraryChartKind.Bmson : LibraryChartKind.Bms,
                    Path = chart.Path,
                    Md5 = chart.Md5,
                    Sha256 = chart.Sha256,
                    IsPendingPackageEntry = target.Entry != null
                });
            }
        }

        return new LibraryChartRemovalPlan
        {
            Targets = new List<LibraryChartRemovalPlanTarget>(planTargets).AsReadOnly(),
            Folders = new List<LibraryChartRemovalPlanFolder>(folderPlans).AsReadOnly(),
            InstallDestinationTargets = new List<LibraryChartRemovalInstallDestinationTarget>(installDestinationTargets).AsReadOnly()
        };
    }

    /// <summary>
    /// Executes a legacy library-removal plan using path facts only.  Catalog
    /// and package deltas are built by the owner after this method returns.
    /// </summary>
    internal LibraryChartRemovalExecutionResult ExecuteLibraryChartRemovalPlan(
        LibraryChartRemovalPlan plan,
        bool sendToRecycleBin,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        var result = new LibraryChartRemovalExecutionResult();
        if (plan == null)
        {
            return result;
        }

        RecycleOption recycleOption = sendToRecycleBin
            ? RecycleOption.SendToRecycleBin
            : RecycleOption.DeletePermanently;
        foreach (LibraryChartRemovalPlanFolder folder in plan.Folders ?? [])
        {
            if (folder == null)
            {
                continue;
            }
            if (folder.DeleteWholeFolder)
            {
                if (!LongPathFileSystem.DirectoryExists(folder.Path))
                {
                    RecordFolderTargets(folder, LibraryChartRemovalState.NotExecuted);
                    continue;
                }
                try
                {
                    fileMutationService.DeleteDirectoryShell(
                        folder.Path,
                        UIOption.OnlyErrorDialogs,
                        recycleOption,
                        recursiveDirectoryTreeFileMutationOptions);
                    result.FolderDeleteCount++;
                    result.DeletedFolderPaths.Add(folder.Path);
                    result.RemovedTargetIndexes.AddRange(folder.TargetIndexes ?? []);
                    RecordFolderTargets(folder, LibraryChartRemovalState.Confirmed);
                }
                catch (Exception exception)
                {
                    RecordFolderTargets(folder, LibraryChartRemovalState.Unconfirmed, exception);
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = folder.Path,
                        Exception = exception,
                        IsDirectory = true
                    });
                }
                continue;
            }

            foreach (int targetIndex in folder.TargetIndexes ?? [])
            {
                LibraryChartRemovalPlanTarget target = targetIndex >= 0 && targetIndex < plan.Targets.Count
                    ? plan.Targets[targetIndex]
                    : null;
                if (target == null)
                {
                    continue;
                }
                try
                {
                    if (LongPathFileSystem.FileExists(target.Path))
                    {
                        fileMutationService.DeleteFileShell(
                            target.Path,
                            UIOption.OnlyErrorDialogs,
                            recycleOption,
                            targetOnlyFileMutationOptions);
                        result.FileDeleteCount++;
                        result.RemovedTargetIndexes.Add(target.Index);
                        result.Targets.Add(new(target.Path, LibraryChartRemovalState.Confirmed));
                    }
                    else
                    {
                        result.Targets.Add(new(target.Path, LibraryChartRemovalState.NotExecuted));
                    }
                }
                catch (Exception exception)
                {
                    result.Targets.Add(new(target.Path, LibraryChartRemovalState.Unconfirmed, exception));
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = target.Path,
                        Exception = exception,
                        IsDirectory = false
                    });
                }
            }
        }
        return result;

        void RecordFolderTargets(LibraryChartRemovalPlanFolder folder, LibraryChartRemovalState state, Exception failure = null)
        {
            foreach (int index in folder.TargetIndexes ?? [])
                result.Targets.Add(new(plan.Targets[index].Path, state, failure));
        }
    }

    /// <summary>
    /// Creates the immutable path plan shared by normal and pending legacy
    /// extension rename routes.
    /// </summary>
    internal LegacyInvalidExtensionRenamePlan BuildInvalidExtensionRenamePlan(
        IEnumerable<LibraryFileOperationTargetSnapshot> targets,
        string newExt,
        bool rejectUnsafeSource = false)
    {
        return new LegacyInvalidExtensionRenamePlan((targets ?? [])
            .Where(target => target != null
                && target.SourceFileExisted
                && !string.IsNullOrWhiteSpace(target.SourcePath))
            .Select(target => new LegacyInvalidExtensionRenamePlanItem(
                target.SourcePath,
                Path.Combine(
                    Path.GetDirectoryName(target.SourcePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(target.SourcePath) + newExt),
                target.PrimaryHash,
                rejectUnsafeSource)));
    }

    /// <summary>
    /// Executes an immutable extension-rename plan without touching live chart
    /// or package state.
    /// </summary>
    internal LegacyInvalidExtensionRenameExecutionResult ExecuteInvalidExtensionRenamePlan(
        LegacyInvalidExtensionRenamePlan plan,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInfo = null,
        Action<Exception, string> logWarn = null)
    {
        var result = new LegacyInvalidExtensionRenameExecutionResult();
        if (plan == null)
        {
            return result;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (LegacyInvalidExtensionRenamePlanItem item in plan.Items)
        {
            RenameInvalidExtensionOutcome outcome = ProcessInvalidExtensionRename(
                item.SourcePath,
                item.RequestedPath,
                item.SourceHashHint,
                fileMutationService,
                targetOnlyFileMutationOptions,
                logInfo,
                logWarn,
                item.RejectUnsafeSource);
            result.Items.Add(new LegacyInvalidExtensionRenameExecutionItem(item, outcome));
            switch (outcome.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    result.RenamedCount++;
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    result.DuplicateDeletedCount++;
                    break;
                default:
                    result.SkippedCount++;
                    if (outcome.FailureException != null)
                    {
                        result.FailedCount++;
                    }
                    break;
            }
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    public LibraryRemovalResult DeleteLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        ILibraryChartCanonicalLookup libraryChartLookup,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        IEnumerable<ChartPackage> pendingPackages,
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
        List<LibraryChartRef> canonicalChartsWithPath = [.. resolveResult.CanonicalCharts.Where(HasPath)];
        List<LibraryChartRef> canonicalChartsWithoutPath = [.. resolveResult.CanonicalCharts.Where(chart => !HasPath(chart))];
        result.CanonicalChartCount = canonicalChartsWithPath.Count;
        result.UnresolvedChartCount = resolveResult.UnresolvedCharts.Count + canonicalChartsWithoutPath.Count;
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
        foreach (LibraryChartRef pathlessChart in canonicalChartsWithoutPath)
        {
            result.Failures.Add(new LibraryDeleteFailure
            {
                Path = FindInputPathForCanonicalChart(inputCharts, pathlessChart),
                Exception = new InvalidOperationException("Library chart no longer has a current path in the catalog."),
                IsDirectory = false,
                Reason = "resolve_failed"
            });
        }
        RecycleOption recycleOption = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        foreach (IGrouping<string, LibraryChartRef> folderGroup in from groupedFiles in canonicalChartsWithPath.GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
                                                                   orderby groupedFiles.Key.Length descending
                                                                   select groupedFiles)
        {
            var removedChartPaths = new HashSet<string>(result.RemovedCharts.Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
            bool shouldDeleteWholeFolder = libraryChartLookup.CountChartRefsUnderRealPath(folderGroup.Key, removedChartPaths) == folderGroup.Count()
                && (confirmDeleteWholeFolder?.Invoke(folderGroup.Key) ?? false);
            if (shouldDeleteWholeFolder)
            {
                if (!LongPathFileSystem.DirectoryExists(folderGroup.Key))
                {
                    continue;
                }
                try
                {
                    fileMutationService.DeleteDirectoryShell(folderGroup.Key, UIOption.OnlyErrorDialogs, recycleOption, recursiveDirectoryTreeFileMutationOptions);
                    result.FolderDeleteCount++;
                    result.DeletedFolderPaths.Add(folderGroup.Key);
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
                    CollectInstallDestinationClearsUnderDeletedFolder(result, folderGroup.Key, pendingPackages, installDestinationOverlayCharts);
                }
                continue;
            }
            foreach (LibraryChartRef selectedChart in folderGroup)
            {
                try
                {
                    if (LongPathFileSystem.FileExists(selectedChart.Path))
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
        ILibraryChartCanonicalLookup libraryChartLookup)
    {
        List<LibraryChartRef> inputCharts = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        libraryChartLookup ??= LibraryChartRefIndexSnapshot.Empty;
        CanonicalChartResolveResult resolveResult = libraryChartLookup.ResolveCanonicalCharts(inputCharts);
        return GetWholeFolderDeleteCandidatePathsForCanonicalCharts(resolveResult.CanonicalCharts.Where(HasPath), libraryChartLookup);
    }

    private static List<string> GetWholeFolderDeleteCandidatePathsForCanonicalCharts(
        IEnumerable<LibraryChartRef> canonicalCharts,
        ILibraryChartCanonicalLookup libraryChartLookup)
    {
        List<string> result = [];
        var selectedChartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, LibraryChartRef> folderGroup in from groupedFiles in (canonicalCharts ?? []).Where(HasPath).GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase)
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

    private static bool HasPath(LibraryChartRef chart)
    {
        return !string.IsNullOrWhiteSpace(chart?.Path);
    }

    private static string FindInputPathForCanonicalChart(IEnumerable<LibraryChartRef> inputCharts, LibraryChartRef canonicalChart)
    {
        if (canonicalChart == null)
        {
            return null;
        }

        foreach (LibraryChartRef inputChart in inputCharts ?? [])
        {
            if (inputChart == null)
            {
                continue;
            }

            BMSFile bmsFile = canonicalChart.GetBmsStorageOwner();
            if (bmsFile != null && ReferenceEquals(inputChart.GetBmsStorageOwner(), bmsFile))
            {
                return inputChart.Path;
            }

            LR2SongDBExtended.bmson_song bmsonSong = canonicalChart.GetBmsonStorageOwner();
            if (bmsonSong != null && ReferenceEquals(inputChart.GetBmsonStorageOwner(), bmsonSong))
            {
                return inputChart.Path;
            }
        }
        return canonicalChart.Path;
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
        ChartFile removedChart = ToChartFile(chart);
        if (removedChart != null)
        {
            OwnedChartRemoveRequest removeRequest = OwnedChartRemoveRequest.FromOwnerReferenceChart(removedChart);
            if (removeRequest != null)
            {
                result.MutationDelta.ChartRemoveRequests.Add(removeRequest);
            }
            result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
            result.MutationDelta.InvalidateParentFolderCache = true;
            result.MutationDelta.ClearDuplicatedCache = true;
        }
    }

    public LibraryMutationDelta BuildFolderMoveDelta(
        string srcDir,
        string dstDir,
        IEnumerable<LibraryChartRef> sourceCharts,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        IEnumerable<ChartPackage> pendingPackages,
        IEnumerable<ChartPackage> installedPackages,
        bool unregister,
        bool notifyStorageRowPathChanges = true)
    {
        var delta = new LibraryMutationDelta();
        List<LibraryChartRef> targetCharts = [.. (sourceCharts ?? [])
            .Where(chart => IsChartUnderFolder(chart, srcDir))];
        if (unregister)
        {
            delta.ChartRemoveRequests.AddRange(targetCharts
                .Select(ToChartFile)
                .Select(OwnedChartRemoveRequest.FromOwnerReferenceChart)
                .Where(request => request != null));
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
                    OldPath = chart.Path,
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
        delta.NotifyStorageRowPathChanges = notifyStorageRowPathChanges && delta.ChartPathChanges.Count > 0;
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
        Func<IEnumerable<ChartFile>, string, string> createFolderPath,
        Func<string, string> normalizeFolderName = null)
    {
        List<string> sourceFolders = [.. (from d in (selectedCharts ?? []).Where(chart => chart != null).Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                                      orderby d.Length
                                      select d)];
        return BuildAutoRenamePlansForSourceFolders(
            sourceFolders,
            rootFolders,
            renameRootFolder,
            createDirectChildSnapshot,
            createFolderPath,
            normalizeFolderName);
    }

    public List<FolderAutoRenamePlan> BuildAutoRenamePlansForSourceFolders(
        IEnumerable<string> sourceFolders,
        IEnumerable<string> rootFolders,
        bool renameRootFolder,
        Func<IReadOnlyCollection<string>, IReadOnlyList<ChartFile>> createDirectChildSnapshot,
        Func<IEnumerable<ChartFile>, string, string> createFolderPath,
        Func<string, string> normalizeFolderName = null)
    {
        List<string> normalizedSourceFolders = [.. (from d in (sourceFolders ?? [])
                                                where !string.IsNullOrWhiteSpace(d)
                                                select d).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length)];
        List<string> effectiveRootFolders = [.. (rootFolders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder))];
        List<string> targetFolders = [];
        var targetFolderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in normalizedSourceFolders.Where(folder => renameRootFolder || !effectiveRootFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
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
        var reservedDestinationFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<FolderAutoRenamePlan> plans = [];
        foreach (string folder in targetFolders)
        {
            var plan = new FolderAutoRenamePlan
            {
                SourceDirectory = folder
            };
            try
            {
                if (!LongPathFileSystem.DirectoryExists(folder) || Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase))
                {
                    plans.Add(plan);
                    continue;
                }
                List<ChartFile> directChildren = directChildrenByDirectory.TryGetValue(folder, out List<ChartFile> children)
                    ? children
                    : [];
                string requestedPath = createFolderPath?.Invoke(directChildren, DirectoryExt.GetDirectoryNameSimple(folder));
                if (!string.IsNullOrWhiteSpace(requestedPath) && !folder.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    plan.DestinationDirectory = ResolveAutoRenameDestinationDirectory(
                        folder,
                        requestedPath,
                        reservedDestinationFolders,
                        normalizeFolderName);
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

    private static string ResolveAutoRenameDestinationDirectory(
        string sourceFolder,
        string requestedPath,
        ISet<string> reservedDestinationFolders,
        Func<string, string> normalizeFolderName)
    {
        string sourceParent = Path.GetDirectoryName(sourceFolder);
        string requestedName = GetLastPathSegment(requestedPath);
        string candidateName = normalizeFolderName?.Invoke(requestedName) ?? requestedName;
        if (string.IsNullOrWhiteSpace(sourceParent) || string.IsNullOrWhiteSpace(candidateName))
        {
            return null;
        }

        string basePath = Path.Combine(sourceParent, candidateName);
        if (basePath.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int suffix = 1;
        string candidate = basePath;
        while (IsAutoRenameDestinationUnavailable(candidate, sourceFolder, reservedDestinationFolders))
        {
            suffix++;
            candidate = basePath + " (" + suffix + ")";
        }
        reservedDestinationFolders?.Add(candidate);
        return candidate;
    }

    private static bool IsAutoRenameDestinationUnavailable(
        string candidate,
        string sourceFolder,
        ISet<string> reservedDestinationFolders)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }
        if (candidate.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return reservedDestinationFolders?.Contains(candidate) == true
            || LongPathFileSystem.EntryExists(candidate);
    }

    private static string GetLastPathSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int lastSeparatorIndex = Math.Max(
            trimmedPath.LastIndexOf(Path.DirectorySeparatorChar),
            trimmedPath.LastIndexOf(Path.AltDirectorySeparatorChar));
        return lastSeparatorIndex >= 0
            ? trimmedPath.Substring(lastSeparatorIndex + 1)
            : trimmedPath;
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
            .Where(folder => !Path.GetPathRoot(folder).Equals(folder, StringComparison.OrdinalIgnoreCase) && LongPathFileSystem.DirectoryExists(folder))
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
        if (!LongPathFileSystem.DirectoryExists(srcDir) || !LongPathFileSystem.DirectoryExists(dstDir) || srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
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
            result.MutationDelta.NotifyStorageRowPathChanges = true;
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
            .Where(file => file != null && LongPathFileSystem.FileExists(file.path)))
        {
            string requestedPath = Path.Combine(Path.GetDirectoryName(file.path), Path.GetFileNameWithoutExtension(file.path) + newExt);
            RenameInvalidExtensionOutcome renameResult = processRename?.Invoke(file, requestedPath) ?? new RenameInvalidExtensionOutcome();
            switch (renameResult.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    delta.RenamedCount++;
                    if (unregister)
                    {
                        delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(file));
                    }
                    else
                    {
                        delta.ChartPathChanges.Add(new LibraryChartPathChange
                        {
                            Chart = ChartFileProjection.FromBmsFile(
                                file,
                                includeWarningSnapshot: true,
                                includeResourceReferences: false),
                            OldPath = file.path,
                            NewPath = renameResult.FinalPath
                        });
                        delta.NotifyStorageRowPathChanges = true;
                    }
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    delta.DuplicateDeletedCount++;
                    delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(file));
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
        delta.InvalidateInstalledDirectoryIndex = delta.ChartPathChanges.Count > 0 || delta.ChartRemoveRequests.Count > 0;
        delta.InvalidateParentFolderCache = delta.ChartPathChanges.Count > 0 || delta.ChartRemoveRequests.Count > 0;
        stopwatch.Stop();
        delta.TotalMs = stopwatch.ElapsedMilliseconds;
        return delta;
    }

    public RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(BMSFile sourceFile, string requestedPath, IFileMutationService fileMutationService, FileMutationOptions targetOnlyFileMutationOptions, Action<string> logInfo = null, Action<Exception, string> logWarn = null)
    {
        return ProcessInvalidExtensionRename(
            sourceFile?.path,
            requestedPath,
            TryGetSourceHashForInvalidExtensionRename(sourceFile),
            fileMutationService,
            targetOnlyFileMutationOptions,
            logInfo,
            logWarn);
    }

    /// <summary>
    /// Executes invalid-extension collision handling from immutable path/hash
    /// facts.  This overload is the only variant used by the post-admission
    /// legacy command executor.
    /// </summary>
    internal RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(
        string sourcePath,
        string requestedPath,
        string sourceHashHint,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Action<string> logInfo = null,
        Action<Exception, string> logWarn = null,
        bool rejectUnsafeSource = false)
    {
        var outcome = new RenameInvalidExtensionOutcome
        {
            Action = RenameInvalidExtensionAction.Skipped,
            FinalPath = requestedPath
        };
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(requestedPath))
        {
            outcome.FailureException = new InvalidOperationException("Invalid-extension rename source or destination path is empty.");
            return outcome;
        }
        if (!LongPathFileSystem.FileExists(sourcePath))
        {
            outcome.FailureException = new FileNotFoundException("Invalid-extension rename source file was not found.", sourcePath);
            return outcome;
        }
        if (rejectUnsafeSource)
        {
            try
            {
                FileAttributes attributes = LongPathFileSystem.GetAttributes(sourcePath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    outcome.FailureException = new IOException("Invalid-extension rename source is not a file.");
                    return outcome;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    outcome.FailureException = new IOException("Invalid-extension rename source reparse points are not supported.");
                    return outcome;
                }
            }
            catch (Exception ex) when (ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException
                || ex is SecurityException)
            {
                outcome.FailureException = ex;
                return outcome;
            }
        }
        if (fileMutationService == null)
        {
            outcome.FailureException = new ArgumentNullException(nameof(fileMutationService));
            return outcome;
        }
        FileCollisionResolutionResult resolution = ResolveFileCollisionWithSuffix(
            sourcePath,
            requestedPath,
            sourceHashHint,
            "invalid_ext_rename",
            logInfo);
        string finalPath = resolution.FinalPath;
        if (resolution.DuplicateMatched)
        {
            try
            {
                fileMutationService.DeleteFileDirect(sourcePath, targetOnlyFileMutationOptions);
                logInfo?.Invoke("invalid_ext_rename duplicate_deleted source=" + sourcePath + " existing=" + resolution.DuplicatePath + " hash=" + (resolution.SourceHash ?? "(null)"));
                outcome.Action = RenameInvalidExtensionAction.DeletedAsDuplicate;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.FailureException = ex;
                outcome.FailedDuringDelete = true;
                logWarn?.Invoke(ex, "invalid_ext_rename delete_failed source=" + sourcePath + " existing=" + resolution.DuplicatePath);
                return outcome;
            }
        }
        if (!string.Equals(finalPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            logInfo?.Invoke("invalid_ext_rename renamed_with_suffix source=" + sourcePath + " requested=" + requestedPath + " resolved=" + finalPath);
        }
        try
        {
            fileMutationService.MoveFile(sourcePath, finalPath, overwrite: false, targetOnlyFileMutationOptions);
            outcome.Action = RenameInvalidExtensionAction.Renamed;
            outcome.FinalPath = finalPath;
            return outcome;
        }
        catch (Exception ex2)
        {
            outcome.FailureException = ex2;
            outcome.FinalPath = finalPath;
            outcome.FailedDuringDelete = false;
            logWarn?.Invoke(ex2, "invalid_ext_rename move_failed source=" + sourcePath + " target=" + finalPath);
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
        while (LongPathFileSystem.EntryExists(candidatePath))
        {
            result.FinalPath = candidatePath;
            if (LongPathFileSystem.DirectoryExists(candidatePath))
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
        while (LongPathFileSystem.EntryExists(candidate))
        {
            candidate = BuildPathWithSuffix(requestedPath, suffix);
            suffix++;
        }
        return candidate;
    }

    public string TryComputeFileMd5ForPath(string filePath, string logCategory = null, Action<string> logInfo = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !LongPathFileSystem.FileExists(filePath))
        {
            return null;
        }
        try
        {
            using var md5 = MD5.Create();
            byte[] hashBytes;
            using (var inputStream = LongPathFileSystem.OpenRead(filePath))
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

    /// <summary>
    /// Returns directory packages whose complete chart set is selected.
    /// A package whose source is the chart itself is a single-file package, not
    /// permission to delete that file's parent, even if all siblings are selected.
    /// The caller supplies the authoritative pending package collection and
    /// validates that each selected package root is still a directory.
    /// </summary>
    public List<ChartPackage> GetPendingPackagesFullyCoveredBySelection(
        IEnumerable<ChartPackage> pendingPackages,
        HashSet<string> selectedPaths)
    {
        List<ChartPackage> result = [];
        foreach (ChartPackage package in (pendingPackages ?? []).Where(pkg => pkg != null))
        {
            List<PackageChartEntry> packageEntries = package.ChartEntries;
            if (packageEntries.Count > 0 && packageEntries.All(entry =>
                entry?.Chart != null
                && !string.IsNullOrWhiteSpace(entry.Chart.Path)
                && !string.Equals(package.path, entry.Chart.Path, StringComparison.OrdinalIgnoreCase)
                && selectedPaths != null
                && selectedPaths.Contains(entry.Chart.Path)))
            {
                result.Add(package);
            }
        }
        return result;
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
