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
    /// 自動リネームの session 結果と解放後の診断を保持します。
    /// </summary>
    /// <param name="hasActionablePlan">Whether at least one plan produced a confirmed physical change.</param>
    /// <param name="appliedPlanCount">Number of confirmed folder moves appended to the operation session.</param>
    /// <param name="sessionReceipt">Operation-scoped durable and failure facts.</param>
    /// <param name="diagnostics">Immutable diagnostics safe to publish after lease release.</param>
    /// <param name="primaryFailure">First unexpected operation failure, preserving its throw identity.</param>
    internal AutoRenameBatchResult(
        bool hasActionablePlan,
        int appliedPlanCount,
        LibraryMutationSessionReceipt sessionReceipt,
        IEnumerable<AutoRenameBatchDiagnostic> diagnostics = null,
        ExceptionDispatchInfo primaryFailure = null)
    {
        HasActionablePlan = hasActionablePlan;
        AppliedPlanCount = appliedPlanCount;
        SessionReceipt = sessionReceipt ?? LibraryMutationSessionReceipt.Empty;
        Diagnostics = Array.AsReadOnly([.. (diagnostics ?? [])
            .Where(diagnostic => diagnostic != null)]);
        PrimaryFailure = primaryFailure;
    }

    internal bool HasActionablePlan { get; }

    internal int AppliedPlanCount { get; }

    /// <summary>Gets the operation-scoped mutation session receipt.</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; }

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
    internal bool HasDurableCommit => SessionReceipt.DurableCommit;

    /// <summary>
    /// Gets whether required session apply/finalization failed after the
    /// confirmed filesystem prefix reached the catalog durable point.
    /// </summary>
    internal bool HasDurableFinalizationFailure => SessionReceipt.HasDurableFinalizationFailure;

    /// <summary>
    /// Gets whether an executor-style manual recovery state was produced.
    /// Session-routed auto rename never fabricates this legacy state.
    /// </summary>
    internal bool ManualRecoveryRequired => false;

    /// <summary>Gets whether optional post-commit cleanup failed without invalidating durability.</summary>
    internal bool CompletedWithCleanupFailure => SessionReceipt.CompletedWithCleanupFailure;

    /// <summary>Gets terminal paths that should be considered for manual confirmation.</summary>
    internal IReadOnlyList<string> RecoveryPaths => SessionReceipt.CandidatePaths;

    /// <summary>
    /// Returns immutable facts for a batch-level post-commit finalizer
    /// failure, preserving the failure's original exception identity.
    /// </summary>
    internal AutoRenameBatchResult WithDurableFinalizationFailure(ExceptionDispatchInfo failure)
    {
        if (failure == null || SessionReceipt.FinalizationFailure != null)
        {
            return this;
        }
        return new AutoRenameBatchResult(
            HasActionablePlan,
            AppliedPlanCount,
            SessionReceipt.WithFinalizationFailure(failure.SourceException),
            Diagnostics,
            PrimaryFailure ?? failure);
    }
}
