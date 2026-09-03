using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Describes a diagnostic fact collected while an automatic folder-rename
/// batch owns its mutation lease.  The fact contains no callback; the command
/// owner publishes it after releasing the lease.
/// </summary>
internal enum AutoRenameBatchDiagnosticKind
{
    PerformanceLog,
    DriveRootSkipped,
    RenamePlanFailed,
    SourceMissing,
    DestinationAlreadyExists,
    MoveFailed,
    ProgressReportFailed
}

/// <summary>
/// Immutable, feature-local diagnostic data for an automatic folder-rename
/// batch.
/// </summary>
internal sealed class AutoRenameBatchDiagnostic
{
    /// <summary>
    /// Creates a diagnostic fact without retaining an executable callback.
    /// </summary>
    internal AutoRenameBatchDiagnostic(
        AutoRenameBatchDiagnosticKind kind,
        string message = null,
        string sourceDirectory = null,
        string destinationDirectory = null,
        Exception failure = null,
        int total = 0,
        int processed = 0)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        SourceDirectory = sourceDirectory ?? string.Empty;
        DestinationDirectory = destinationDirectory ?? string.Empty;
        Failure = failure;
        Total = total;
        Processed = processed;
    }

    /// <summary>
    /// Gets the diagnostic category.
    /// </summary>
    internal AutoRenameBatchDiagnosticKind Kind { get; }

    /// <summary>
    /// Gets the immutable log text, when the category is a performance log.
    /// </summary>
    internal string Message { get; }

    /// <summary>
    /// Gets the source path associated with the fact.
    /// </summary>
    internal string SourceDirectory { get; }

    /// <summary>
    /// Gets the destination path associated with the fact.
    /// </summary>
    internal string DestinationDirectory { get; }

    /// <summary>
    /// Gets the original failure associated with the fact, when any.
    /// </summary>
    internal Exception Failure { get; }

    /// <summary>
    /// Gets the progress total associated with a progress-report failure.
    /// </summary>
    internal int Total { get; }

    /// <summary>
    /// Gets the progress count associated with a progress-report failure.
    /// </summary>
    internal int Processed { get; }
}

/// <summary>
/// Immutable terminal facts for an automatic folder-rename batch.
/// </summary>
internal sealed class AutoRenameBatchResult
{
    /// <summary>
    /// Creates immutable terminal facts for a folder auto-rename command.
    /// </summary>
    internal AutoRenameBatchResult(
        bool hasActionablePlan,
        int appliedPlanCount,
        FileDbMutationBatchReceipt mutationReceipt,
        IEnumerable<Lr2NormalFolderPathChange> lr2NormalFolderPathChanges = null,
        IEnumerable<AutoRenameBatchDiagnostic> diagnostics = null,
        ExceptionDispatchInfo primaryFailure = null)
    {
        HasActionablePlan = hasActionablePlan;
        AppliedPlanCount = appliedPlanCount;
        MutationReceipt = mutationReceipt ?? new FileDbMutationBatchReceipt([]);
        Lr2NormalFolderPathChanges = Array.AsReadOnly([.. (lr2NormalFolderPathChanges ?? [])
            .Where(change => change != null)]);
        Diagnostics = Array.AsReadOnly([.. (diagnostics ?? [])
            .Where(diagnostic => diagnostic != null)]);
        PrimaryFailure = primaryFailure;
    }

    internal bool HasActionablePlan { get; }

    internal int AppliedPlanCount { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    /// <summary>
    /// Gets immutable BMS path facts synchronized once while the batch's
    /// original capability is live, or recorded as incomplete after release
    /// when the batch already has a primary failure.
    /// </summary>
    internal IReadOnlyList<Lr2NormalFolderPathChange> Lr2NormalFolderPathChanges { get; }

    /// <summary>
    /// Gets immutable diagnostics that are safe to publish after lease release.
    /// </summary>
    internal IReadOnlyList<AutoRenameBatchDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the primary operation failure while preserving its original
    /// exception identity and throw frame.
    /// </summary>
    internal ExceptionDispatchInfo PrimaryFailure { get; }

    /// <summary>
    /// Gets whether the operation failed after producing this partial result.
    /// </summary>
    internal bool HasOperationFailure => PrimaryFailure != null;

    /// <summary>
    /// Gets whether at least one filesystem/catalog mutation reached the
    /// durable commit point.
    /// </summary>
    internal bool HasDurableCommit => MutationReceipt.HasDurableCommit;

    /// <summary>
    /// Gets whether an individual mutation or the batch finalizer failed after
    /// the filesystem and catalog had become durable.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt.HasDurableFinalizationFailure;

    internal bool ManualRecoveryRequired => MutationReceipt.ManualRecoveryRequired;

    internal bool CompletedWithCleanupFailure => MutationReceipt.CompletedWithCleanupFailure;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt.RecoveryPaths;

    /// <summary>
    /// Returns immutable facts for a batch-level post-commit finalizer
    /// failure, preserving the failure's original exception identity.
    /// </summary>
    internal AutoRenameBatchResult WithDurableFinalizationFailure(ExceptionDispatchInfo failure)
    {
        if (failure == null || MutationReceipt.FinalizationFailure != null)
        {
            return this;
        }
        return new AutoRenameBatchResult(
            HasActionablePlan,
            AppliedPlanCount,
            MutationReceipt.WithFinalizationFailure(failure.SourceException),
            Lr2NormalFolderPathChanges,
            Diagnostics,
            PrimaryFailure ?? failure);
    }
}
