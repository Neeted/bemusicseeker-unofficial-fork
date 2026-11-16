using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>捕捉したカタログrootから通常一覧を投影します。不変値の投影だけを共有し、変更ごとの全件長配列を持ちません。</summary>
internal sealed class NormalLibrarySourceRows : IReadOnlyList<ChartListSourceRow>
{
    private readonly Func<ChartFile, ChartListSourceRow> project;
    private ImmutableDictionary<ChartFile, ChartListSourceRow> projections;
    private readonly Action<string, int> observeWork;
    private ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> tokensByMd5;
    private ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> tokensBySha256;

    /// <summary>同じ確定時点の値・所属・順序を保持します。行getterで可変の現在値を検索しません。</summary>
    internal NormalLibrarySourceRows(BMSLibrary library, CatalogChartCollectionSnapshot snapshot,
        bool includeBmsonRows, Func<ChartFile, ChartListSourceRow> project, NormalLibrarySourceRows previous = null,
        Action<string, int> observeWork = null, ChartListScoreInput scoreInput = null, ChartListReferenceInput referenceInput = null)
    {
        Library = library;
        Snapshot = snapshot;
        IncludeBmsonRows = includeBmsonRows;
        this.project = project;
        this.observeWork = observeWork;
        ScoreInput = scoreInput ?? previous?.ScoreInput;
        References = referenceInput ?? previous?.References ?? new(library);
        // 旧要求が投影を作り続けても、現在値のcacheを古い値で上書きしません。
        // 退役値はsnapshotと投影への参照がなくなると回収でき、所属変更時の全cache剪定も不要です。
        projections = previous != null && ReferenceEquals(library, previous.Library)
            ? previous.projections : ImmutableDictionary.Create<ChartFile, ChartListSourceRow>(ReferenceEqualityComparer.Instance);
        // nullは初回列挙前だけを表します。hash登録は同じ投影列挙に含め、元集合を二度読みません。
        tokensByMd5 = previous?.tokensByMd5;
        tokensBySha256 = previous?.tokensBySha256;
    }

    /// <summary>捕捉元のライブラリ識別です。</summary>
    internal BMSLibrary Library { get; }
    /// <summary>元集合全体へ接続した独立score入力です。</summary>
    internal ChartListScoreInput ScoreInput { get; }
    /// <summary>同じ元集合の参照表示と反映済み位置です。</summary>
    internal ChartListReferenceInput References { get; private set; }
    /// <summary>この入力の確定済みカタログrootです。</summary>
    internal CatalogChartCollectionSnapshot Snapshot { get; }
    /// <summary>BMSONを表示範囲に含めるかを返します。</summary>
    internal bool IncludeBmsonRows { get; }
    /// <summary>非同期候補の採用時だけ現在の確定版と照合します。</summary>
    internal bool IsCurrent => Library == null || Snapshot.CatalogRevision == Library.CatalogRevision;
    public int Count => Snapshot.BmsRows.Count + (IncludeBmsonRows ? Snapshot.BmsonRows.Count : 0);
    public ChartListSourceRow this[int index] => Project(index < Snapshot.BmsRows.Count
        ? Snapshot.BmsRows[index] : Snapshot.BmsonRows[index - Snapshot.BmsRows.Count]);

    /// <summary>確定結果の構造共有rootを使う次の入力を作ります。現在カタログは捕捉し直しません。</summary>
    internal NormalLibrarySourceRows WithSnapshot(CatalogChartCollectionSnapshot snapshot, IReadOnlyList<CatalogChartChange> changes,
        ChartListReferenceInput references = null)
    {
        PrepareForOrder();
        var next = new NormalLibrarySourceRows(Library, snapshot, IncludeBmsonRows, project, this, observeWork, ScoreInput, references);
        foreach (CatalogChartChange change in changes)
        {
            if (change.Before != null && !ReferenceEquals(change.Before, change.After)) { next.projections = next.projections.Remove(change.Before); }
            if (change.After != null && !ReferenceEquals(next.References, References)
                && next.projections.TryGetValue(change.After, out ChartListSourceRow existing))
            {
                ChartListSourceRow updated = existing.WithReferenceDisplay(next.References.Resolve(change.After.Md5, change.After.Sha256));
                if (!ReferenceEquals(existing, updated)) { ObserveProjection(change.After); next.projections = next.projections.SetItem(change.After, updated); }
            }
            if (change.Before?.Kind == change.After?.Kind
                && StringComparer.OrdinalIgnoreCase.Equals(change.Before?.Md5, change.After?.Md5)
                && StringComparer.OrdinalIgnoreCase.Equals(change.Before?.Sha256, change.After?.Sha256)) { continue; }
            next.RemoveHashKeys(change.Before);
            next.AddHashKeys(change.After);
        }
        var removedKeys = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
        foreach (CatalogChartChange change in changes)
        {
            if (!string.IsNullOrWhiteSpace(change.Before?.Md5) && !next.tokensByMd5.ContainsKey(change.Before.Md5))
            { removedKeys.Add(new(PlaylistEntryLookupKeyKind.Md5, change.Before.Md5)); }
            if (!string.IsNullOrWhiteSpace(change.Before?.Sha256) && !next.tokensBySha256.ContainsKey(change.Before.Sha256))
            { removedKeys.Add(new(PlaylistEntryLookupKeyKind.Sha256, change.Before.Sha256)); }
        }
        next.References = next.References.RemoveDependencies(removedKeys);
        return next;
    }

    /// <summary>元集合全体のhash依存を使い、filter外・未訪問の実対象tokenも返します。</summary>
    internal HashSet<OwnedChartToken> GetTokens(IEnumerable<string> md5Keys, IEnumerable<string> sha256Keys)
    {
        PrepareForOrder();
        HashSet<OwnedChartToken> result = [];
        CaptureTokens(tokensByMd5, md5Keys, result);
        CaptureTokens(tokensBySha256, sha256Keys, result);
        return result;
    }

    /// <summary>非表示中の既存入力も配送rangeの実対象だけを更新し、後続要求へ未反映gapを持ち越しません。</summary>
    internal bool TryApplyScoreChanges(IReadOnlyList<ScoreSnapshotChange> changes)
    {
        if (ScoreInput == null) { return changes.Count == 0; }
        foreach (ScoreSnapshotChange change in changes)
        {
            if (change.Version <= ScoreInput.Position) { continue; }
            if (change.BaseVersion > ScoreInput.Position) { return false; }
            ScoreInput.PrepareChanges();
            foreach (OwnedChartToken token in GetTokens(change.Md5Keys, change.Sha256Keys))
            {
                if (!TryGetRow(token, out ChartListSourceRow row)) { continue; }
                row.InvalidateScoreProjection();
                _ = row.ScoreValue;
            }
            if (!ScoreInput.CompleteChange(change)) { return false; }
        }
        return true;
    }

    private void AddHashKeys(ChartFile chart)
    {
        if (chart?.Token == null || !IncludeBmsonRows && chart.Kind == ChartFileKind.Bmson) { return; }
        observeWork?.Invoke("hash_dependency_visit", 1);
        tokensByMd5 = AddKey(tokensByMd5, chart.Md5, chart.Token);
        tokensBySha256 = AddKey(tokensBySha256, chart.Sha256, chart.Token);
    }
    private void RemoveHashKeys(ChartFile chart)
    {
        if (chart?.Token == null) { return; }
        tokensByMd5 = RemoveKey(tokensByMd5, chart.Md5, chart.Token);
        tokensBySha256 = RemoveKey(tokensBySha256, chart.Sha256, chart.Token);
    }
    private static ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> AddKey(
        ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> index, string key, OwnedChartToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) { return index; }
        return index.SetItem(key, (index.TryGetValue(key, out ImmutableHashSet<OwnedChartToken> tokens) ? tokens : ImmutableHashSet<OwnedChartToken>.Empty).Add(token));
    }
    private static void AddInitialKey(ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>>.Builder index,
        string key, OwnedChartToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) { return; }
        index[key] = (index.TryGetValue(key, out ImmutableHashSet<OwnedChartToken> tokens) ? tokens : ImmutableHashSet<OwnedChartToken>.Empty).Add(token);
    }
    private static ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> RemoveKey(
        ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> index, string key, OwnedChartToken token)
    {
        if (string.IsNullOrWhiteSpace(key) || !index.TryGetValue(key, out ImmutableHashSet<OwnedChartToken> tokens)) { return index; }
        tokens = tokens.Remove(token);
        return tokens.Count == 0 ? index.Remove(key) : index.SetItem(key, tokens);
    }
    private static void CaptureTokens(ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>> index,
        IEnumerable<string> keys, HashSet<OwnedChartToken> target)
    {
        foreach (string key in keys ?? [])
        { if (!string.IsNullOrWhiteSpace(key) && index.TryGetValue(key, out ImmutableHashSet<OwnedChartToken> tokens)) { target.UnionWith(tokens); } }
    }

    /// <summary>対象tokenだけを捕捉rootから取得・投影し、現在値や別配置へ救済しません。</summary>
    internal bool TryGetRow(OwnedChartToken token, out ChartListSourceRow row)
    {
        row = null;
        if (!Snapshot.TryGetChart(token, out ChartFile chart, out _)
            || !IncludeBmsonRows && chart.Kind == ChartFileKind.Bmson)
        {
            return false;
        }
        row = Project(chart);
        return true;
    }

    /// <summary>同じ不変値の投影を再利用します。旧新の派生キー比較にも利用できます。</summary>
    internal ChartListSourceRow Project(ChartFile chart)
    {
        observeWork?.Invoke("source_visit", 1);
        return ImmutableInterlocked.GetOrAdd(ref projections, chart, value =>
        {
            ObserveProjection(value);
            ChartListSourceRow row = project(value);
            return References.IsCaptured ? row.WithReferenceDisplay(References.Resolve(value.Md5, value.Sha256)) : row;
        });
    }

    /// <summary>既存のhash依存索引から必要な両形式keyを返します。カタログを追加走査しません。</summary>
    internal IEnumerable<PlaylistEntryLookupKey> ReferenceKeys()
    {
        foreach (string hash in tokensByMd5.Keys) { yield return new(PlaylistEntryLookupKeyKind.Md5, hash); }
        foreach (string hash in tokensBySha256.Keys) { yield return new(PlaylistEntryLookupKeyKind.Sha256, hash); }
    }

    /// <summary>新しいカタログ差分が増やすhash依存だけを得ます。</summary>
    internal HashSet<PlaylistEntryLookupKey> AddedReferenceKeys(CatalogCommitReceipt commit)
    {
        var result = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
        foreach (CatalogChartChange change in commit.Changes)
        {
            ChartFile chart = change.After;
            if (chart == null || !IncludeBmsonRows && chart.Kind == ChartFileKind.Bmson) { continue; }
            if (!string.IsNullOrWhiteSpace(chart.Md5) && !tokensByMd5.ContainsKey(chart.Md5)) { result.Add(new(PlaylistEntryLookupKeyKind.Md5, chart.Md5)); }
            if (!string.IsNullOrWhiteSpace(chart.Sha256) && !tokensBySha256.ContainsKey(chart.Sha256)) { result.Add(new(PlaylistEntryLookupKeyKind.Sha256, chart.Sha256)); }
        }
        return result;
    }

    /// <summary>参照差分の実対象だけを不変投影へ差し替え、無関係な投影と旧入力を共有します。</summary>
    internal NormalLibrarySourceRows WithReferenceCommit(PlaylistReferenceCommitReceipt commit, out HashSet<OwnedChartToken> changed)
    {
        changed = [];
        ChartListReferenceInput references = References.Apply(commit, key => key.Kind == PlaylistEntryLookupKeyKind.Md5
            ? tokensByMd5.ContainsKey(key.Hash) : tokensBySha256.ContainsKey(key.Hash));
        if (ReferenceEquals(references, References)) { return this; }
        var next = new NormalLibrarySourceRows(Library, Snapshot, IncludeBmsonRows, project, this, observeWork, ScoreInput, references);
        foreach (KeyValuePair<PlaylistEntryLookupKey, PlaylistReferenceDisplay> pair in commit.Changes)
        {
            HashSet<OwnedChartToken> tokens = pair.Key.Kind == PlaylistEntryLookupKeyKind.Md5 ? GetTokens([pair.Key.Hash], []) : GetTokens([], [pair.Key.Hash]);
            foreach (OwnedChartToken token in tokens)
            {
                if (changed.Contains(token) || !TryGetRow(token, out ChartListSourceRow before)) { continue; }
                ChartListSourceRow after = before.WithReferenceDisplay(references.Resolve(before.Hash, before.Sha256));
                if (ReferenceEquals(before, after)) { continue; }
                ObserveProjection(before.CapturedChart);
                next.projections = next.projections.SetItem(before.CapturedChart, after);
                changed.Add(token);
            }
        }
        return next;
    }

    private void ObserveProjection(ChartFile chart)
    {
        observeWork?.Invoke("projection", 1);
        observeWork?.Invoke(chart.Kind == ChartFileKind.Bms ? "projection_bms" : "projection_bmson", 1);
    }

    /// <summary>実際の順序キー取得・格納件数を観測します。</summary>
    internal void ObserveWork(string kind, int count) => observeWork?.Invoke(kind, count);

    /// <summary>基準添字を再利用できる所属・順序かを返します。</summary>
    internal bool HasSameMembershipOrder(NormalLibrarySourceRows other) => other != null
        && ReferenceEquals(Library, other.Library) && IncludeBmsonRows == other.IncludeBmsonRows
        && ReferenceEquals(Snapshot.MembershipOrderIdentity, other.Snapshot.MembershipOrderIdentity);

    /// <summary>初回の全体投影と同じ列挙でhash依存を準備します。最後まで読んだ入力だけを準備済みとします。</summary>
    public IEnumerator<ChartListSourceRow> GetEnumerator()
    {
        ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>>.Builder md5 = tokensByMd5 == null
            ? ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<OwnedChartToken>>(StringComparer.OrdinalIgnoreCase) : null;
        ImmutableDictionary<string, ImmutableHashSet<OwnedChartToken>>.Builder sha256 = md5 != null
            ? ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<OwnedChartToken>>(StringComparer.OrdinalIgnoreCase) : null;
        ImmutableDictionary<ChartFile, ChartListSourceRow>.Builder initialProjections = md5 != null ? projections.ToBuilder() : null;
        ChartListSourceRow ProjectInitial(ChartFile chart)
        {
            if (md5 != null && chart.Token != null)
            {
                observeWork?.Invoke("hash_dependency_visit", 1);
                AddInitialKey(md5, chart.Md5, chart.Token);
                AddInitialKey(sha256, chart.Sha256, chart.Token);
            }
            if (initialProjections == null) { return Project(chart); }
            observeWork?.Invoke("source_visit", 1);
            if (!initialProjections.TryGetValue(chart, out ChartListSourceRow row))
            {
                ObserveProjection(chart);
                initialProjections.Add(chart, row = project(chart));
            }
            return row;
        }
        foreach (ChartFile chart in Snapshot.BmsRows) { yield return ProjectInitial(chart); }
        if (IncludeBmsonRows)
        {
            foreach (ChartFile chart in Snapshot.BmsonRows) { yield return ProjectInitial(chart); }
        }
        if (md5 != null)
        {
            tokensBySha256 = sha256.ToImmutable();
            tokensByMd5 = md5.ToImmutable();
            projections = initialProjections.ToImmutable();
            References.CaptureInitial(ReferenceKeys());
            observeWork?.Invoke("reference_capture", 1);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>初回の全体投影とhash依存を一回で準備します。共有cache公開後や明示整列では再走査しません。</summary>
    internal void PrepareForOrder()
    {
        if (tokensByMd5 != null) { return; }
        foreach (ChartListSourceRow row in this) { }
    }
}
