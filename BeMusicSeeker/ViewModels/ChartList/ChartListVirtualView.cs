using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>通常一覧はtokenと局所更新可能な順序、専用一覧は捕捉添字を使い、訪問行だけを実体化します。</summary>
internal sealed class ChartListVirtualView : IList, IChartListViewMetadata, INotifyCollectionChanged
{
    private readonly IReadOnlyList<ChartListSourceRow> sourceRows;
    private readonly IReadOnlyList<int> orderedIndexes;
    private readonly Func<ChartListSourceRow, LibraryChartRow> rowFactory;
    private readonly Dictionary<int, LibraryChartRow> realizedSubsetRows = [];
    private readonly Dictionary<OwnedChartToken, LibraryChartRow> realizedNormalRows = [];
    private readonly NormalLibraryPendingChanges pendingNormalChanges = new();
    private readonly NormalLibraryViewOrder normalOrder;
    private readonly ChartSubsetViewOrder subsetOrder;
    private readonly ChartListScoreInput scoreInput;
    private readonly ChartSubsetReferenceRows subsetReferences;
    private int scorePosition;
    private readonly int distinctFolderCount;

    /// <summary>初回の整列結果を採用し、以後は同じ元集合の実差分だけを反映します。専用一覧の参照入力も捕捉位置とともに受け取ります。</summary>
    internal ChartListVirtualView(IReadOnlyList<ChartListSourceRow> sourceRows, ChartListOrder order,
        Func<ChartListSourceRow, LibraryChartRow> rowFactory, int distinctFolderCount = -1,
        Func<ChartListSourceRow, bool> normalFilter = null, ChartListScoreInput scoreInput = null,
        int? scorePosition = null, Action<string, int> observeWork = null,
        ChartListReferenceInput referenceInput = null, int catalogRevision = 0)
    {
        if (referenceInput != null && sourceRows is not NormalLibrarySourceRows)
        { subsetReferences = new(sourceRows ?? [], referenceInput, catalogRevision, observeWork); }
        this.sourceRows = subsetReferences ?? sourceRows ?? [];
        orderedIndexes = order?.Indexes ?? [];
        this.rowFactory = rowFactory ?? throw new ArgumentNullException(nameof(rowFactory));
        this.distinctFolderCount = distinctFolderCount;
        this.scoreInput = scoreInput ?? (sourceRows as NormalLibrarySourceRows)?.ScoreInput;
        this.scorePosition = scorePosition ?? this.scoreInput?.Position ?? 0;
        if (sourceRows is NormalLibrarySourceRows normal)
        {
            normalOrder = new(normal, order, normalFilter);
        }
        else if (this.scoreInput != null) { subsetOrder = new(this.sourceRows, order, normalFilter, observeWork); }
    }

    public event NotifyCollectionChangedEventHandler CollectionChanged;
    public int Count => normalOrder?.Count ?? subsetOrder?.Count ?? orderedIndexes.Count;
    public int RowCount => Count;
    public int DistinctFolderCount => normalOrder?.DistinctFolderCount ?? subsetOrder?.DistinctFolderCount ?? distinctFolderCount;
    public int RealizedRowCount => normalOrder != null ? realizedNormalRows.Count : realizedSubsetRows.Count;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public object this[int index] { get => GetOrCreate(index); set => throw new NotSupportedException(); }
    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++) { yield return GetOrCreate(i); }
    }
    public void DisposeRealizedRows()
    {
        realizedNormalRows.Clear();
        realizedSubsetRows.Clear();
    }
    private IEnumerable<LibraryChartRow> RealizedRows => normalOrder != null ? realizedNormalRows.Values : realizedSubsetRows.Values;

    /// <summary>差分で入力を接続済みの通常行かを、sourceを再取得せずに照合します。</summary>
    internal bool IsRealizedNormalRow(OwnedChartToken token, LibraryChartRow row)
        => token != null && realizedNormalRows.TryGetValue(token, out LibraryChartRow realized) && ReferenceEquals(realized, row);

    public int Add(object value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object value) => IndexOf(value) >= 0;
    public int IndexOf(object value)
    {
        if (normalOrder != null)
        {
            return value is LibraryChartRow row && row.Chart?.Token is OwnedChartToken token
                && realizedNormalRows.TryGetValue(token, out LibraryChartRow realized) && ReferenceEquals(value, realized)
                ? normalOrder.IndexOf(token) : -1;
        }
        foreach (KeyValuePair<int, LibraryChartRow> pair in realizedSubsetRows)
        {
            if (ReferenceEquals(pair.Value, value))
            {
                if (subsetOrder != null) { return subsetOrder.IndexOf(pair.Key); }
                for (int index = 0; index < orderedIndexes.Count; index++)
                { if (orderedIndexes[index] == pair.Key) { return index; } }
            }
        }
        return -1;
    }
    internal void ForEachRealizedRow(Action<LibraryChartRow> action)
    {
        if (action == null) { return; }
        foreach (LibraryChartRow row in RealizedRows) { action(row); }
    }

    /// <summary>確定結果だけを反映し、対象の実体化済み行を更新します。未訪問行は生成しません。</summary>
    internal bool TryApplyCatalogCommits(IReadOnlyList<CatalogCommitReceipt> commits,
        Action<LibraryChartRow, ChartListSourceRow> update, out bool orderChanged)
        => TryApplyLibraryChanges(commits, [], update, out orderChanged);

    /// <summary>参照の確定境界と構造差分を同じ元集合へ順次適用します。新hashの未配送境界では入力を保留します。</summary>
    internal bool TryApplyLibraryChanges(IReadOnlyList<CatalogCommitReceipt> commits,
        IReadOnlyList<PlaylistReferenceCommitReceipt> references,
        Action<LibraryChartRow, ChartListSourceRow> update, out bool orderChanged,
        Action<NormalLibrarySourceRows, CatalogCommitReceipt, NormalLibrarySourceRows> beforeCatalog = null, Action referenceValuesChanged = null,
        Action<NormalLibrarySourceRows, CatalogCommitReceipt> afterCatalog = null)
    {
        orderChanged = false;
        if (normalOrder == null) { return false; }
        foreach (NormalLibraryPendingChanges.Step step in pendingNormalChanges.Advance(NormalInput, commits, references))
        {
            IEnumerable<OwnedChartToken> changedTokens;
            if (step.Catalog != null)
            {
                beforeCatalog?.Invoke(step.Before, step.Catalog, step.After);
                if (!normalOrder.TryApply(step.Catalog, out bool changed, step.After)) { return false; }
                orderChanged |= changed;
                changedTokens = step.Catalog.Changes.Select(change => change.Token);
            }
            else
            {
                if (step.ReferenceTokens.Count > 0) { referenceValuesChanged?.Invoke(); }
                orderChanged |= normalOrder.ApplyReferenceInput(step.After, step.ReferenceTokens);
                changedTokens = step.ReferenceTokens;
            }
            foreach (OwnedChartToken token in changedTokens)
            {
                if (!realizedNormalRows.TryGetValue(token, out LibraryChartRow row)) { continue; }
                if (normalOrder.IndexOf(token) < 0)
                {
                    realizedNormalRows.Remove(token);
                }
                if (NormalInput.TryGetRow(token, out ChartListSourceRow source)) { update?.Invoke(row, source); }
            }
            if (step.Catalog != null) { afterCatalog?.Invoke(NormalInput, step.Catalog); }
        }
        return !pendingNormalChanges.HasPending;
    }

    /// <summary>採用前に解消する必要がある、短命の新hash/参照境界待ちです。</summary>
    internal bool HasPendingReferenceChanges => pendingNormalChanges.HasPending || subsetReferences?.HasPending == true;
    /// <summary>元集合へ実際に反映した参照位置です。通常と専用一覧で同じ意味を使います。</summary>
    internal long ReferencePosition => NormalInput?.References.Revision ?? subsetReferences?.References.Revision ?? 0;
    /// <summary>参照入力を所有するlibraryです。</summary>
    internal BMSLibrary ReferenceLibrary => NormalInput?.Library ?? subsetReferences?.References.Library;

    /// <summary>hashで依存する専門値だけを再評価し、実体化済み対象の表示を更新します。</summary>
    internal bool RefreshKeyDependencies(IEnumerable<string> md5Keys, IEnumerable<string> sha256Keys, Action<LibraryChartRow> refresh)
    {
        if (normalOrder == null) { return false; }
        HashSet<OwnedChartToken> tokens = normalOrder.GetTokens(md5Keys, sha256Keys);
        return RefreshTokens(tokens, refresh);
    }

    /// <summary>専門表示の実対象tokenだけを再評価します。表示外の依存行も含みます。</summary>
    internal bool RefreshTokens(IEnumerable<OwnedChartToken> tokens, Action<LibraryChartRow> refresh)
    {
        if (normalOrder == null) { return false; }
        bool changed = normalOrder.RefreshTokens(tokens);
        foreach (OwnedChartToken token in tokens)
        {
            if (realizedNormalRows.TryGetValue(token, out LibraryChartRow row))
            {
                refresh?.Invoke(row);
                if (normalOrder.IndexOf(token) < 0) { realizedNormalRows.Remove(token); }
            }
        }
        return changed;
    }

    /// <summary>変更キーを元集合の依存行だけへ適用し、順序・filterも反映したrangeまで位置を進めます。</summary>
    internal bool TryApplyScoreChanges(IReadOnlyList<ScoreSnapshotChange> changes, out bool orderChanged)
    {
        orderChanged = false;
        if (scoreInput == null) { return changes.Count == 0; }
        foreach (ScoreSnapshotChange change in changes)
        {
            if (change.Version <= scorePosition) { continue; }
            if (change.BaseVersion > scorePosition) { return false; }
            scoreInput.PrepareChanges();
            if (normalOrder != null)
            {
                HashSet<OwnedChartToken> tokens = normalOrder.GetTokens(change.Md5Keys, change.Sha256Keys);
                foreach (OwnedChartToken token in tokens)
                { if (NormalInput.TryGetRow(token, out ChartListSourceRow source)) { source.InvalidateScoreProjection(); } }
                orderChanged |= RefreshTokens(tokens, row => row.RefreshDisplayForDataDependency(MainViewDataDependency.Score));
            }
            else if (subsetOrder != null)
            {
                HashSet<int> indexes = subsetOrder.GetIndexes(change);
                foreach (int index in indexes) { sourceRows[index].InvalidateScoreProjection(); }
                orderChanged |= subsetOrder.Refresh(indexes);
                foreach (int index in indexes)
                {
                    if (realizedSubsetRows.TryGetValue(index, out LibraryChartRow row))
                    {
                        row.RefreshDisplayForDataDependency(MainViewDataDependency.Score);
                        if (subsetOrder.IndexOf(index) < 0) { realizedSubsetRows.Remove(index); }
                    }
                }
            }
            if (!scoreInput.CompleteChange(change)) { return false; }
            scorePosition = change.Version;
        }
        return true;
    }

    /// <summary>専用元集合のhash依存だけへ参照を適用し、変更行の表示・検索・順序を一致させます。</summary>
    internal bool TryApplySubsetLibraryChanges(IReadOnlyList<CatalogCommitReceipt> catalogs,
        IReadOnlyList<PlaylistReferenceCommitReceipt> references, out bool orderChanged, Action referenceValuesChanged = null)
    {
        orderChanged = false;
        if (subsetReferences == null || subsetOrder == null)
        {
            orderChanged = RefreshSubsetCatalogChanges(catalogs);
            return references.Count == 0;
        }
        HashSet<int> indexes = subsetReferences.Apply(subsetOrder, catalogs, references, referenceValuesChanged);
        orderChanged = subsetOrder.Refresh(indexes);
        foreach (int index in indexes)
        {
            if (!realizedSubsetRows.TryGetValue(index, out LibraryChartRow row)) { continue; }
            row.UpdateSubsetSourceProjection(sourceRows[index]);
            row.RefreshDisplayForDataDependency(MainViewDataDependency.ReferenceTables);
            row.RefreshDisplayForDataDependency(MainViewDataDependency.SourceMembership);
            if (subsetOrder.IndexOf(index) < 0) { realizedSubsetRows.Remove(index); }
        }
        return !subsetReferences.HasPending;
    }

    /// <summary>Rowsを保持する動的package入力へreceiptの対象だけを反映し、score更新前にも順序・folder集計を一致させます。</summary>
    internal bool RefreshSubsetCatalogChanges(IReadOnlyList<CatalogCommitReceipt> commits)
    {
        if (subsetOrder == null || commits.Count == 0) { return false; }
        HashSet<int> indexes = subsetOrder.GetIndexes(commits);
        bool changed = subsetOrder.Refresh(indexes);
        foreach (int index in indexes)
        {
            if (realizedSubsetRows.TryGetValue(index, out LibraryChartRow row))
            { row.RefreshDisplayForDataDependency(MainViewDataDependency.SourceMembership); }
        }
        return changed;
    }

    /// <summary>この元集合のscore読取り元です。適用位置をcatalog/detail版で補正しません。</summary>
    internal BMSLibrary ScoreLibrary => scoreInput?.Library;
    /// <summary>このviewの比較・検索・元集合へ変更キーを反映済みの位置です。</summary>
    internal int ScorePosition => scorePosition;

    /// <summary>所属・位置が変わったときだけcontrolへ通知し、値だけの更新では選択と編集を維持します。</summary>
    internal void PublishOrderChanged() => CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    /// <summary>通常一覧候補の捕捉カタログを検査します。独立score位置はrange適用後に別に検査します。</summary>
    internal bool IsCatalogInputCurrent => NormalInput?.IsCurrent != false;
    /// <summary>通常一覧の処理済み確定入力です。専用対象集合ではnullです。</summary>
    internal NormalLibrarySourceRows NormalInput => normalOrder?.Source;
    /// <summary>選択位置をtokenへ変換します。選択保持のために行を実体化しません。</summary>
    internal OwnedChartToken GetNormalTokenAt(int index) => normalOrder != null && index >= 0 && index < Count
        ? normalOrder.TokenAt(index) : null;
    /// <summary>捕捉tokenの現在表示位置を返し、未訪問行を生成しません。</summary>
    internal int IndexOfNormalToken(OwnedChartToken token) => normalOrder?.IndexOf(token) ?? -1;
    /// <summary>専用subsetの選択を元集合の添字で保持し、行を実体化しません。</summary>
    internal int GetSubsetSourceIndexAt(int index) => subsetOrder != null && index >= 0 && index < Count ? subsetOrder.SourceIndexAt(index) : -1;
    /// <summary>局所再配置後の専用subset選択位置を取得します。</summary>
    internal int IndexOfSubsetSourceIndex(int index) => subsetOrder?.IndexOf(index) ?? -1;
    public void Insert(int index, object value) => throw new NotSupportedException();
    public void Remove(object value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < Count; i++) { array.SetValue(GetOrCreate(i), index + i); }
    }
    private LibraryChartRow GetOrCreate(int index)
    {
        if (index < 0 || index >= Count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        if (normalOrder != null)
        {
            OwnedChartToken token = normalOrder.TokenAt(index);
            if (!realizedNormalRows.TryGetValue(token, out LibraryChartRow row))
            {
                if (!NormalInput.TryGetRow(token, out ChartListSourceRow source)) { return null; }
                realizedNormalRows.Add(token, row = rowFactory(source));
            }
            return row;
        }
        int sourceIndex = subsetOrder?.SourceIndexAt(index) ?? orderedIndexes[index];
        if (realizedSubsetRows.TryGetValue(sourceIndex, out LibraryChartRow realized)) { return realized; }
        if (sourceIndex < 0 || sourceIndex >= sourceRows.Count) { return null; }
        realizedSubsetRows.Add(sourceIndex, realized = rowFactory(sourceRows[sourceIndex]));
        return realized;
    }
}
