using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>通常一覧の派生順序・表示所属とhash依存だけを保持します。局所変更では不変木の変更経路を共有します。</summary>
internal sealed class NormalLibraryViewOrder
{
    private ImmutableList<Entry> entries;
    private readonly Dictionary<OwnedChartToken, Entry> entriesByToken = [];
    private readonly Dictionary<string, int> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<ChartListSourceRow, bool> matches;
    private readonly string columnName;
    private readonly ListSortDirection direction;

    /// <summary>初回または明示条件変更で評価済みの順序を採用し、非表示行を含むhash依存を一回だけ構築します。</summary>
    internal NormalLibraryViewOrder(NormalLibrarySourceRows source, ChartListOrder order, Func<ChartListSourceRow, bool> matches)
    {
        Source = source;
        columnName = order.ColumnName;
        direction = order.Direction;
        this.matches = matches ?? (_ => true);
        ImmutableList<Entry>.Builder initial = ImmutableList.CreateBuilder<Entry>();
        foreach (int index in order.Indexes)
        {
            Entry entry = Capture(source[index]);
            initial.Add(entry);
            entriesByToken.Add(entry.Token, entry);
            AddFolder(entry.Folder);
        }
        source.ObserveWork("order_buffer", initial.Count);
        entries = initial.ToImmutable();

    }

    /// <summary>この順序が処理済みの不変入力です。</summary>
    internal NormalLibrarySourceRows Source { get; private set; }
    internal int Count => entries.Count;
    internal int DistinctFolderCount => folders.Count;
    internal OwnedChartToken TokenAt(int index) => entries[index].Token;
    internal int IndexOf(OwnedChartToken token) => token != null && entriesByToken.TryGetValue(token, out Entry entry)
        ? Find(entry) : -1;

    /// <summary>値変更と順序変更の対象を旧比較規則で除去し、同commitの新snapshotで再挿入します。順序だけの生値は再投影せず、未知の版範囲・実全置換は局所更新として受理しません。</summary>
    internal bool TryApply(CatalogCommitReceipt commit, out bool orderChanged, NormalLibrarySourceRows nextSource = null)
    {
        orderChanged = false;
        if (commit.Revision <= Source.Snapshot.CatalogRevision) { return true; }
        if (commit.IsFullReplacement || commit.Snapshot == null || commit.BaseRevision != Source.Snapshot.CatalogRevision) { return false; }
        nextSource ??= Source.WithSnapshot(commit.Snapshot, commit.Changes);
        bool membershipChanged = !nextSource.HasSameMembershipOrder(Source);
        var affected = new Dictionary<OwnedChartToken, (Entry Before, Entry After, int Index)>();
        foreach (CatalogChartChange change in commit.Changes)
        {
            entriesByToken.TryGetValue(change.Token, out Entry before);
            Entry after = nextSource.TryGetRow(change.Token, out ChartListSourceRow row) && matches(row) ? Capture(row) : null;
            if (before != null && after != null && !membershipChanged && before.Folder == after.Folder
                && ChartListOrder.CompareKeys(before.Key, after.Key, direction) == 0)
            {
                continue;
            }
            if (before != null || after != null)
            {
                affected.Add(change.Token, (before, after, before == null ? -1 : Find(before)));
            }
        }
        foreach (OwnedChartToken token in commit.OrderChangedTokens)
        {
            if (affected.ContainsKey(token) || !entriesByToken.TryGetValue(token, out Entry entry)) { continue; }
            // 値・filter適否は不変です。捕捉済みキーを保ち、旧comparerで外して新comparerへ挿し直します。
            affected.Add(token, (entry, entry, Find(entry)));
        }
        // canonical位置が移る前に影響対象だけを除去します。値だけの更新では順序木を触りません。
        foreach (KeyValuePair<OwnedChartToken, (Entry Before, Entry After, int Index)> pair in affected)
        {
            if (pair.Value.Before != null)
            {
                entries = entries.RemoveAt(Find(pair.Value.Before));
                entriesByToken.Remove(pair.Key);
                RemoveFolder(pair.Value.Before.Folder);
            }
        }
        Source = nextSource;
        foreach (KeyValuePair<OwnedChartToken, (Entry Before, Entry After, int Index)> pair in affected)
        {
            Entry after = pair.Value.After;
            if (after != null)
            {
                entries = entries.Insert(FindInsertion(after), after);
                entriesByToken.Add(pair.Key, after);
                AddFolder(after.Folder);
            }
        }
        foreach (KeyValuePair<OwnedChartToken, (Entry Before, Entry After, int Index)> pair in affected)
        {
            orderChanged |= pair.Value.Index != IndexOf(pair.Key);
        }
        return true;
    }

    /// <summary>score・詳細等の独立した変更キーから、非表示行も含む依存tokenだけを返します。</summary>
    internal HashSet<OwnedChartToken> GetTokens(IEnumerable<string> md5Keys, IEnumerable<string> sha256Keys)
    {
        return Source.GetTokens(md5Keys, sha256Keys);
    }

    /// <summary>参照値の確定入力を採用し、実表示値が変わるtokenだけ検索・順序へ反映します。</summary>
    internal bool ApplyReferenceInput(NormalLibrarySourceRows source, IEnumerable<OwnedChartToken> tokens)
    {
        Source = source;
        return RefreshTokens(tokens);
    }

    /// <summary>既存入力を保った専門依存の変更を、対象行の検索適否と順序へ反映します。</summary>
    internal bool RefreshTokens(IEnumerable<OwnedChartToken> tokens)
    {
        bool changed = false;
        foreach (OwnedChartToken token in tokens)
        {
            int oldIndex = IndexOf(token);
            Entry next = Source.TryGetRow(token, out ChartListSourceRow row) && matches(row) ? Capture(row) : null;
            if (oldIndex >= 0 && next != null && Compare(entries[oldIndex], next) == 0) { continue; }
            if (oldIndex >= 0)
            {
                RemoveFolder(entries[oldIndex].Folder);
                entries = entries.RemoveAt(oldIndex);
                entriesByToken.Remove(token);
            }
            int newIndex = -1;
            if (next != null)
            {
                newIndex = FindInsertion(next);
                entries = entries.Insert(newIndex, next);
                entriesByToken.Add(token, next);
                AddFolder(next.Folder);
            }
            changed |= oldIndex != newIndex;
        }
        return changed;
    }

    private Entry Capture(ChartListSourceRow row)
    {
        Source.ObserveWork("order_key", 1);
        return new(row.Token, ChartListOrder.CaptureKey(row, columnName), row.Folder);
    }

    private int Compare(Entry left, Entry right)
    {
        Source.ObserveWork("order_compare", 1);
        int compared = ChartListOrder.CompareKeys(left.Key, right.Key, direction);
        if (compared != 0 || ReferenceEquals(left.Token, right.Token)) { return compared; }
        // 追加・削除で位置の数値が変わっても、未変更token間の相対順序は変わりません。
        Source.Snapshot.TryGetChart(left.Token, out _, out int leftPosition);
        Source.Snapshot.TryGetChart(right.Token, out _, out int rightPosition);
        return leftPosition.CompareTo(rightPosition);
    }

    private int Find(Entry entry)
    {
        int index = FindInsertion(entry);
        return index < entries.Count && ReferenceEquals(entries[index].Token, entry.Token) ? index
            : throw new InvalidOperationException("The normal view order lost its captured token.");
    }

    private int FindInsertion(Entry entry)
    {
        int low = 0, high = entries.Count;
        while (low < high)
        {
            Source.ObserveWork("order_access", 1);
            int middle = low + (high - low) / 2;
            if (Compare(entries[middle], entry) < 0) { low = middle + 1; }
            else { high = middle; }
        }
        return low;
    }

    private void AddFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder)) { folders[folder] = folders.GetValueOrDefault(folder) + 1; }
    }
    private void RemoveFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && folders.TryGetValue(folder, out int count))
        {
            if (count == 1) { folders.Remove(folder); } else { folders[folder] = count - 1; }
        }
    }
    private sealed record Entry(OwnedChartToken Token, ChartListCapturedSortKey Key, string Folder);
}
