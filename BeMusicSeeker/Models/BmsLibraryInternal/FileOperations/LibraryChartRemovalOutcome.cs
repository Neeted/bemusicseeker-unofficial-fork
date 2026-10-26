using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>既存の削除実行で観測した成功・未実行・物理失敗と、事前未解決の結果です。要求の結び付き異常は個別結果へ混ぜません。</summary>
internal enum LibraryChartRemovalState
{
    Confirmed,
    NotExecuted,
    Unconfirmed,
    Unresolved
}

/// <summary>A chart target and its observed filesystem result; failures retain the original exception.</summary>
internal sealed record LibraryChartRemovalTarget(string Path, LibraryChartRemovalState State, Exception Failure = null);

/// <summary>一回の削除について物理事実・catalog終端・登録保存失敗を別々に保持する不変の結果です。</summary>
internal sealed class LibraryChartRemovalOutcome
{
    /// <summary>観測した対象別事実と独立した必須終端の原因を保持します。</summary>
    internal LibraryChartRemovalOutcome(IEnumerable<LibraryChartRemovalTarget> targets,
        bool catalogApplyAttempted = false, bool catalogDurable = false, Exception catalogFailure = null, Exception registrationFailure = null)
    {
        Targets = Array.AsReadOnly((targets ?? []).ToArray());
        CatalogApplyAttempted = catalogApplyAttempted;
        CatalogDurable = catalogDurable;
        CatalogFailure = catalogFailure;
        RegistrationFailure = registrationFailure;
    }

    /// <summary>物理成功を保ち、変更セッションと登録保存の独立した終端を集めます。</summary>
    /// <param name="targets">削除APIの観測結果。</param>
    /// <param name="sessionReceipt">同じ操作のcatalog・派生状態の終端証票。</param>
    /// <param name="catalogApplyAttempted">変更セッションの確定を試みたか。</param>
    /// <param name="registrationFailure">登録保存・限定公開の失敗。catalog原因へ置き換えません。</param>
    /// <param name="catalogFailure">証票を返す前に起きたcatalog処理の例外。</param>
    /// <param name="deletedFolderPaths">実executorの削除APIが成功したscope。</param>
    internal LibraryChartRemovalOutcome(
        IEnumerable<LibraryChartRemovalTarget> targets,
        LibraryMutationSessionReceipt sessionReceipt,
        bool catalogApplyAttempted, Exception registrationFailure = null, Exception catalogFailure = null, IEnumerable<string> deletedFolderPaths = null)
    {
        Targets = Array.AsReadOnly((targets ?? []).ToArray());
        SessionReceipt = sessionReceipt ?? LibraryMutationSessionReceipt.Empty;
        CatalogApplyAttempted = catalogApplyAttempted;
        CatalogDurable = SessionReceipt.DurableCommit;
        RegistrationFailure = registrationFailure;
        DeletedFolderPaths = Array.AsReadOnly((deletedFolderPaths ?? []).ToArray());
        CatalogFailure = catalogFailure ?? SessionReceipt.ApplyFailure
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
    /// <summary>物理成功後の登録保存・限定公開の失敗です。catalog原因と区別します。</summary>
    internal Exception RegistrationFailure { get; }
    /// <summary>executorの削除APIが成功したscopeだけを保持します。</summary>
    internal IReadOnlyList<string> DeletedFolderPaths { get; } = Array.AsReadOnly(Array.Empty<string>());
    /// <summary>必須終端の独立した原因を失わず、外側の結果へ渡します。</summary>
    internal Exception RequiredFailure => CatalogFailure == null ? RegistrationFailure
        : RegistrationFailure == null ? CatalogFailure : new AggregateException(CatalogFailure, RegistrationFailure);
    /// <summary>catalog確定後の必須最終化の失敗です。</summary>
    internal bool RequiredFinalizationFailed => CatalogDurable && CatalogFailure != null;
    /// <summary>Whether the deletion needs a terminal error report.</summary>
    internal bool HasError => RegistrationFailure != null || CatalogFailure != null || Targets.Any(target => target.State != LibraryChartRemovalState.Confirmed);
}

/// <summary>Stops dependent repair maintenance while carrying the already observed deletion facts.</summary>
/// <remarks>削除済み対象とカタログ反映結果を引き継ぐ必要があるため、結果を持たない標準の簡略コンストラクターは提供しません。</remarks>
internal sealed class LibraryChartRemovalException : Exception
{
    /// <summary>Preserves the deletion outcome across the repair bridge's unwind.</summary>
    internal LibraryChartRemovalException(LibraryChartRemovalOutcome outcome, Exception failure = null)
        : base("Library chart removal required application failed.", failure ?? outcome.RequiredFailure)
    {
        Outcome = outcome;
    }

    /// <summary>Observed facts retained after the outer operation releases its gate.</summary>
    internal LibraryChartRemovalOutcome Outcome { get; }
}
