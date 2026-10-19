using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一つの確定区間で採用した所持項目の旧新値です。削除後も旧キーを保持します。</summary>
internal sealed record CatalogChartChange(ChartFile Before, ChartFile After)
{
    /// <summary>同じ所持項目の識別です。追加・削除でも利用できます。</summary>
    internal OwnedChartToken Token => After?.Token ?? Before?.Token;
    /// <summary>候補の所属・解決・順序に影響する変更かを返します。</summary>
    internal bool ChangesCandidate => Before == null || After == null || Before.Token != After.Token
        || Before.Kind != After.Kind || Before.Path != After.Path
        || !string.Equals(Before.Md5, After.Md5, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(Before.Sha256, After.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>現在値、版、旧新キーを同じ境界で確定した不変結果です。配送時に現在値を捕捉し直しません。</summary>
internal sealed class CatalogCommitReceipt
{
    /// <summary>owner が確定した結果を構成します。変更集合は外部から変更できません。</summary>
    internal CatalogCommitReceipt(int baseRevision, int revision, IEnumerable<CatalogChartChange> changes, bool isFullReplacement, CatalogChartCollectionSnapshot snapshot = null)
    {
        Snapshot = snapshot;
        BaseRevision = baseRevision;
        Revision = revision;
        Changes = Array.AsReadOnly((changes ?? []).ToArray());
        IsFullReplacement = isFullReplacement;
    }
    /// <summary>確定時の同じ不変rootを共有する一覧入力です。後続currentを捕捉し直しません。</summary>
    internal CatalogChartCollectionSnapshot Snapshot { get; }
    /// <summary>確定前のカタログ版です。</summary>
    internal int BaseRevision { get; }
    /// <summary>確定後のカタログ版です。同値・対象なしでは元版と一致します。</summary>
    internal int Revision { get; }
    /// <summary>実際に変更した項目だけの旧新値です。</summary>
    internal IReadOnlyList<CatalogChartChange> Changes { get; }
    /// <summary>所属と順序を全体で置換した確定かを返します。</summary>
    internal bool IsFullReplacement { get; }
    /// <summary>現在値の実変更を伴うかを返します。</summary>
    internal bool HasChanges => Revision != BaseRevision;
}

/// <summary>不変集合の参照差だけを実変更と判定せず、現在値の意味を比較します。</summary>
internal static class CatalogChartValueEquality
{
    /// <summary>一覧内の不変リストを内容比較し、それ以外の現在値をrecord契約で比較します。</summary>
    private static object WarningValue(ChartWarning value) => (value.Kind, value.Message, value.Priority, value.DigestLabel, value.HighlightRow, value.ShowInDigest, value.ShowInTooltip);

    internal static bool AreEqual(ChartFile left, ChartFile right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return left == right with
        {
            Warnings = left.Warnings,
            Resources = left.Resources,
            InstallDestinationSuggestions = left.InstallDestinationSuggestions
        }
            && left.Warnings.Select(WarningValue).SequenceEqual(right.Warnings.Select(WarningValue))
            && left.InstallDestinationSuggestions.SequenceEqual(right.InstallDestinationSuggestions)
            && (left.Resources == right.Resources || left.Resources != null && right.Resources != null
                && left.Resources.SequenceEqual(right.Resources));
    }
}
