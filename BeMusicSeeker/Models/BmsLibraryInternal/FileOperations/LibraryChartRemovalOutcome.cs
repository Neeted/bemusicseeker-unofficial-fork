using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>Facts observed by the existing deletion executor, without a filesystem recheck.</summary>
internal enum LibraryChartRemovalState
{
    Confirmed,
    NotExecuted,
    Unconfirmed,
    Stale,
    Unresolved
}

/// <summary>A chart target and its observed filesystem result; failures retain the original exception.</summary>
internal sealed record LibraryChartRemovalTarget(string Path, LibraryChartRemovalState State, Exception Failure = null);

/// <summary>Callback-free, immutable filesystem and session terminal facts for one library deletion.</summary>
internal sealed class LibraryChartRemovalOutcome
{
    /// <summary>Copies the observed targets and the existing catalog owner's commit facts.</summary>
    internal LibraryChartRemovalOutcome(IEnumerable<LibraryChartRemovalTarget> targets,
        bool catalogApplyAttempted = false, bool catalogDurable = false, Exception catalogFailure = null)
    {
        Targets = Array.AsReadOnly((targets ?? []).ToArray());
        CatalogApplyAttempted = catalogApplyAttempted;
        CatalogDurable = catalogDurable;
        CatalogFailure = catalogFailure;
    }

    /// <summary>Copies deletion facts and derives catalog terminal state from the operation session.</summary>
    /// <param name="targets">Observed filesystem deletion results.</param>
    /// <param name="sessionReceipt">Operation-scoped catalog/derived-state terminal facts.</param>
    /// <param name="catalogApplyAttempted">Whether the delete command reached its session commit boundary.</param>
    internal LibraryChartRemovalOutcome(
        IEnumerable<LibraryChartRemovalTarget> targets,
        LibraryMutationSessionReceipt sessionReceipt,
        bool catalogApplyAttempted)
    {
        Targets = Array.AsReadOnly((targets ?? []).ToArray());
        SessionReceipt = sessionReceipt ?? LibraryMutationSessionReceipt.Empty;
        CatalogApplyAttempted = catalogApplyAttempted;
        CatalogDurable = SessionReceipt.DurableCommit;
        CatalogFailure = SessionReceipt.ApplyFailure
            ?? SessionReceipt.FinalizationFailure
            ?? (SessionReceipt.ConfirmedChangeCount > 0 && !SessionReceipt.DurableCommit
                ? new InvalidOperationException("Catalog mutation did not produce a durable session receipt.")
                : null);
    }

    /// <summary>All target facts, including successful deletions preceding a failure.</summary>
    internal IReadOnlyList<LibraryChartRemovalTarget> Targets { get; }
    /// <summary>Operation-scoped terminal facts when deletion used the mutation-session path.</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; }
    /// <summary>Number of chart targets whose deletion API returned successfully.</summary>
    internal int ConfirmedChartCount => Targets.Count(target => target.State == LibraryChartRemovalState.Confirmed);
    /// <summary>Whether the existing catalog apply was called.</summary>
    internal bool CatalogApplyAttempted { get; }
    /// <summary>Whether the existing catalog owner observed its durable callback.</summary>
    internal bool CatalogDurable { get; }
    /// <summary>Catalog or required postcommit failure, independent of filesystem facts.</summary>
    internal Exception CatalogFailure { get; }
    /// <summary>Required finalization failed after the catalog commit was observed.</summary>
    internal bool RequiredFinalizationFailed => CatalogDurable && CatalogFailure != null;
    /// <summary>Whether the deletion needs a terminal error report.</summary>
    internal bool HasError => CatalogFailure != null || Targets.Any(target => target.State != LibraryChartRemovalState.Confirmed);
}

/// <summary>Stops dependent repair maintenance while carrying the already observed deletion facts.</summary>
/// <remarks>削除済み対象とカタログ反映結果を引き継ぐ必要があるため、結果を持たない標準の簡略コンストラクターは提供しません。</remarks>
internal sealed class LibraryChartRemovalException : Exception
{
    /// <summary>Preserves the deletion outcome across the repair bridge's unwind.</summary>
    internal LibraryChartRemovalException(LibraryChartRemovalOutcome outcome, Exception failure = null)
        : base("Library chart removal catalog application failed.", failure ?? outcome.CatalogFailure)
    {
        Outcome = outcome;
    }

    /// <summary>Observed facts retained after the outer operation releases its gate.</summary>
    internal LibraryChartRemovalOutcome Outcome { get; }
}
