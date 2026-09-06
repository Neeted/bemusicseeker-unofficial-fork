using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

/// <summary>Callback-free, immutable filesystem and catalog facts for one library deletion.</summary>
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

    /// <summary>All target facts, including successful deletions preceding a failure.</summary>
    internal IReadOnlyList<LibraryChartRemovalTarget> Targets { get; }
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
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "削除済み対象とカタログ反映結果の引き継ぎが必須であり、結果を持たない標準コンストラクターは提供しない。")]
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
