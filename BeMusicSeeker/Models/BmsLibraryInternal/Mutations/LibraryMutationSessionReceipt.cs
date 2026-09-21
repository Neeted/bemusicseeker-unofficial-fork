using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Identifies one source/destination pair owned by an operation-scoped library mutation session.
/// </summary>
internal sealed class LibraryMutationSessionTarget
{
    /// <summary>Creates an immutable operation target.</summary>
    /// <param name="sourcePath">The path observed before the physical mutation.</param>
    /// <param name="destinationPath">The requested destination path.</param>
    internal LibraryMutationSessionTarget(string sourcePath, string destinationPath)
    {
        SourcePath = sourcePath ?? string.Empty;
        DestinationPath = destinationPath ?? string.Empty;
    }

    /// <summary>Gets the source path.</summary>
    internal string SourcePath { get; }

    /// <summary>Gets the destination path.</summary>
    internal string DestinationPath { get; }
}

/// <summary>
/// Identifies a target that was attempted but did not produce a confirmed physical change.
/// The failure is retained for one operation-scoped terminal report without turning it into
/// a synthetic per-item durable receipt.
/// </summary>
internal sealed class LibraryMutationSessionItemFailure
{
    /// <summary>Creates immutable item-failure facts for the session terminal.</summary>
    /// <param name="target">The source/destination candidate that failed.</param>
    /// <param name="failure">The observed physical-operation failure.</param>
    /// <param name="destinationTypeConflicts">Read-only destination type conflicts that caused this item refusal.</param>
    internal LibraryMutationSessionItemFailure(
        LibraryMutationSessionTarget target,
        Exception failure,
        IEnumerable<FileDbMutationDestinationTypeConflict> destinationTypeConflicts = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
        DestinationTypeConflicts = Array.AsReadOnly((destinationTypeConflicts ?? [])
            .Where(conflict => conflict != null)
            .ToArray());
    }

    /// <summary>Gets the failed source/destination candidate.</summary>
    internal LibraryMutationSessionTarget Target { get; }

    /// <summary>Gets the observed physical-operation failure.</summary>
    internal Exception Failure { get; }

    /// <summary>Gets destination type conflicts that made this item a read-only refusal.</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts { get; }

    /// <summary>Gets whether the item was refused before mutation solely because the destination type was incompatible.</summary>
    internal bool IsDestinationTypeConflictRefusal => DestinationTypeConflicts.Count > 0;
}

/// <summary>
/// Immutable terminal facts for one operation-scoped library mutation session.
/// Filesystem changes are represented as confirmed targets rather than synthetic
/// per-item durable receipts.
/// </summary>
internal sealed class LibraryMutationSessionReceipt
{
    /// <summary>Creates immutable terminal facts for one library mutation session.</summary>
    /// <param name="confirmedTargets">Physical changes confirmed before the canonical apply.</param>
    /// <param name="durableCommit">Whether the canonical catalog durable point was reached.</param>
    /// <param name="catalogChartRemovalCount">Confirmed catalog chart removals.</param>
    /// <param name="catalogChartPathChangeCount">Confirmed catalog chart path changes.</param>
    /// <param name="catalogFolderPathChangeCount">Confirmed catalog folder path changes.</param>
    /// <param name="packageInstallDestinationChangeCount">Confirmed package install-destination changes.</param>
    /// <param name="packageInstalledPathChangeCount">Confirmed installed-package path changes.</param>
    /// <param name="folderReferenceMoveCount">Confirmed reverse-lookup folder moves.</param>
    /// <param name="physicalFailure">Unexpected physical failure that stopped the operation.</param>
    /// <param name="failedTarget">Target associated with <paramref name="physicalFailure"/>.</param>
    /// <param name="unprocessedTargets">Suffix left unprocessed after an unexpected failure.</param>
    /// <param name="applyFailure">Canonical DB or required in-memory apply failure.</param>
    /// <param name="finalizationFailure">Required post-commit finalization failure.</param>
    /// <param name="cleanupFailure">Post-commit cleanup failure that does not change durability.</param>
    /// <param name="resourceDirectoryRemovalCount">Successfully deleted directory roots registered for post-commit resource-index removal.</param>
    /// <param name="itemFailures">Attempted targets that did not produce confirmed physical changes.</param>
    /// <param name="recoveryCandidatePaths">Executor-local paths retained for manual confirmation or recovery.</param>
    /// <param name="manualRecoveryRequired">Whether any physical mutation requires manual recovery.</param>
    /// <param name="destinationTypeConflicts">Read-only destination type conflicts observed while preparing items.</param>
    /// <param name="applyCounts">操作内で実際に試行した反映回数と、確定した folder DB 対象行数。</param>
    internal LibraryMutationSessionReceipt(
        IEnumerable<LibraryMutationSessionTarget> confirmedTargets,
        bool durableCommit,
        int catalogChartRemovalCount = 0,
        int catalogChartPathChangeCount = 0,
        int catalogFolderPathChangeCount = 0,
        int packageInstallDestinationChangeCount = 0,
        int packageInstalledPathChangeCount = 0,
        int folderReferenceMoveCount = 0,
        Exception physicalFailure = null,
        LibraryMutationSessionTarget failedTarget = null,
        IEnumerable<LibraryMutationSessionTarget> unprocessedTargets = null,
        Exception applyFailure = null,
        Exception finalizationFailure = null,
        Exception cleanupFailure = null,
        int resourceDirectoryRemovalCount = 0,
        IEnumerable<LibraryMutationSessionItemFailure> itemFailures = null,
        IEnumerable<string> recoveryCandidatePaths = null,
        bool manualRecoveryRequired = false,
        IEnumerable<FileDbMutationDestinationTypeConflict> destinationTypeConflicts = null,
        LibraryMutationSessionApplyCounts applyCounts = default)
    {
        ApplyCounts = applyCounts;
        ConfirmedTargets = FreezeTargets(confirmedTargets);
        DurableCommit = durableCommit;
        CatalogChartRemovalCount = Math.Max(catalogChartRemovalCount, 0);
        CatalogChartPathChangeCount = Math.Max(catalogChartPathChangeCount, 0);
        CatalogFolderPathChangeCount = Math.Max(catalogFolderPathChangeCount, 0);
        PackageInstallDestinationChangeCount = Math.Max(packageInstallDestinationChangeCount, 0);
        PackageInstalledPathChangeCount = Math.Max(packageInstalledPathChangeCount, 0);
        FolderReferenceMoveCount = Math.Max(folderReferenceMoveCount, 0);
        ResourceDirectoryRemovalCount = Math.Max(resourceDirectoryRemovalCount, 0);
        ItemFailures = FreezeItemFailures(itemFailures);
        PhysicalFailure = physicalFailure;
        FailedTarget = failedTarget;
        UnprocessedTargets = FreezeTargets(unprocessedTargets);
        ApplyFailure = applyFailure;
        FinalizationFailure = finalizationFailure;
        CleanupFailure = cleanupFailure;
        RecoveryCandidatePaths = FreezePaths(recoveryCandidatePaths);
        ManualRecoveryRequired = manualRecoveryRequired;
        DestinationTypeConflicts = FreezeDestinationTypeConflicts(destinationTypeConflicts);
    }

    /// <summary>操作内の反映回数。失敗した試行も含み、未到達の段階は 0 のままです。</summary>
    internal LibraryMutationSessionApplyCounts ApplyCounts { get; }

    /// <summary>Gets filesystem changes whose success was confirmed and appended to the session.</summary>
    internal IReadOnlyList<LibraryMutationSessionTarget> ConfirmedTargets { get; }

    /// <summary>Gets the number of confirmed physical changes owned by this operation.</summary>
    internal int ConfirmedChangeCount => ConfirmedTargets.Count;

    /// <summary>Gets whether the session's canonical catalog transaction reached its durable point.</summary>
    internal bool DurableCommit { get; }

    /// <summary>Gets the number of confirmed catalog chart removals.</summary>
    internal int CatalogChartRemovalCount { get; }

    /// <summary>Gets the number of confirmed catalog chart path changes.</summary>
    internal int CatalogChartPathChangeCount { get; }

    /// <summary>Gets the number of confirmed catalog folder path changes.</summary>
    internal int CatalogFolderPathChangeCount { get; }

    /// <summary>Gets the number of confirmed package install-destination changes.</summary>
    internal int PackageInstallDestinationChangeCount { get; }

    /// <summary>Gets the number of confirmed installed-package path changes.</summary>
    internal int PackageInstalledPathChangeCount { get; }

    /// <summary>Gets the number of confirmed folder reverse-lookup moves.</summary>
    internal int FolderReferenceMoveCount { get; }

    /// <summary>Gets the number of successfully deleted directory roots registered for resource-index removal.</summary>
    internal int ResourceDirectoryRemovalCount { get; }

    /// <summary>Gets attempted item failures that were safe to aggregate without stopping the remaining targets.</summary>
    internal IReadOnlyList<LibraryMutationSessionItemFailure> ItemFailures { get; }

    /// <summary>Gets the unexpected filesystem/pre-append failure that stopped the suffix, when any.</summary>
    internal Exception PhysicalFailure { get; }

    /// <summary>Gets the target whose processing could not be completed, when any.</summary>
    internal LibraryMutationSessionTarget FailedTarget { get; }

    /// <summary>Gets targets left unprocessed because an unexpected failure stopped the operation.</summary>
    internal IReadOnlyList<LibraryMutationSessionTarget> UnprocessedTargets { get; }

    /// <summary>
    /// Gets the canonical/internal apply failure. When <see cref="DurableCommit"/> is true,
    /// durable DB state already exists and this failure must not trigger filesystem rollback.
    /// </summary>
    internal Exception ApplyFailure { get; }

    /// <summary>Gets a later required operation finalizer failure, such as maintenance or package collection publication.</summary>
    internal Exception FinalizationFailure { get; }

    /// <summary>Gets a post-commit cleanup failure that does not change durable state.</summary>
    internal Exception CleanupFailure { get; }

    /// <summary>Gets executor-local paths retained for manual confirmation or recovery.</summary>
    internal IReadOnlyList<string> RecoveryCandidatePaths { get; }

    /// <summary>Gets whether any physical mutation requires manual recovery.</summary>
    internal bool ManualRecoveryRequired { get; }

    /// <summary>Gets destination type conflicts observed while preparing package mutations.</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts { get; }

    /// <summary>Gets whether required operation work ended in a failure.</summary>
    internal bool HasRequiredFailure => ManualRecoveryRequired
        || PhysicalFailure != null
        || ApplyFailure != null
        || FinalizationFailure != null
        || ItemFailures.Any(item => !item.IsDestinationTypeConflictRefusal);

    /// <summary>Gets whether required operation finalization failed after the durable point.</summary>
    internal bool HasDurableFinalizationFailure => DurableCommit
        && (ApplyFailure != null || FinalizationFailure != null);

    /// <summary>Gets whether optional cleanup failed after the durable point.</summary>
    internal bool CompletedWithCleanupFailure => DurableCommit
        && !HasDurableFinalizationFailure
        && CleanupFailure != null;

    /// <summary>Gets the first terminal failure retained by the session.</summary>
    internal Exception PrimaryFailure => PhysicalFailure
        ?? ApplyFailure
        ?? FinalizationFailure
        ?? ItemFailures.FirstOrDefault(item => !item.IsDestinationTypeConflictRefusal)?.Failure
        ?? CleanupFailure;

    /// <summary>
    /// Gets terminal confirmation candidates without creating per-item mutation receipts.
    /// Failed and unprocessed targets precede confirmed paths so bounded UI reports retain
    /// the paths that most need manual inspection.
    /// </summary>
    internal IReadOnlyList<string> CandidatePaths => Array.AsReadOnly(
        (FailedTarget == null ? [] : new[] { FailedTarget.SourcePath, FailedTarget.DestinationPath })
        .Concat(RecoveryCandidatePaths)
        .Concat(ItemFailures.SelectMany(item => new[] { item.Target.SourcePath, item.Target.DestinationPath }))
        .Concat(UnprocessedTargets.SelectMany(target => new[] { target.SourcePath, target.DestinationPath }))
        .Concat(ConfirmedTargets.SelectMany(target => new[] { target.SourcePath, target.DestinationPath }))
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray());

    /// <summary>Returns the same immutable session facts with a required finalization failure.</summary>
    /// <param name="failure">The required post-commit finalization failure to retain.</param>
    /// <returns>This receipt when a failure is already retained; otherwise a copy with the supplied failure.</returns>
    internal LibraryMutationSessionReceipt WithFinalizationFailure(Exception failure)
    {
        if (failure == null || FinalizationFailure != null)
        {
            return this;
        }
        return new LibraryMutationSessionReceipt(
            ConfirmedTargets,
            DurableCommit,
            CatalogChartRemovalCount,
            CatalogChartPathChangeCount,
            CatalogFolderPathChangeCount,
            PackageInstallDestinationChangeCount,
            PackageInstalledPathChangeCount,
            FolderReferenceMoveCount,
            PhysicalFailure,
            FailedTarget,
            UnprocessedTargets,
            ApplyFailure,
            failure,
            CleanupFailure,
            ResourceDirectoryRemovalCount,
            ItemFailures,
            RecoveryCandidatePaths,
            ManualRecoveryRequired,
            DestinationTypeConflicts,
            ApplyCounts);
    }

    /// <summary>Gets an immutable empty session receipt.</summary>
    internal static LibraryMutationSessionReceipt Empty { get; } = new([], durableCommit: false);

    private static IReadOnlyList<string> FreezePaths(IEnumerable<string> paths)
    {
        return Array.AsReadOnly((paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static IReadOnlyList<FileDbMutationDestinationTypeConflict> FreezeDestinationTypeConflicts(
        IEnumerable<FileDbMutationDestinationTypeConflict> values)
    {
        return Array.AsReadOnly((values ?? [])
            .Where(value => value != null)
            .GroupBy(value => string.Join("\u001f",
                value.SourcePath,
                value.DestinationPath,
                value.ExpectedIsDirectory,
                value.ExistingIsDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray());
    }

    private static IReadOnlyList<LibraryMutationSessionTarget> FreezeTargets(
        IEnumerable<LibraryMutationSessionTarget> targets)
    {
        return Array.AsReadOnly((targets ?? [])
            .Where(target => target != null)
            .ToArray());
    }

    private static IReadOnlyList<LibraryMutationSessionItemFailure> FreezeItemFailures(
        IEnumerable<LibraryMutationSessionItemFailure> failures)
    {
        return Array.AsReadOnly((failures ?? [])
            .Where(failure => failure != null)
            .ToArray());
    }
}
