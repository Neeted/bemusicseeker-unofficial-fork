using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistDetailVirtualView : IList, IChartListViewMetadata, INotifyCollectionChanged
{
    private ImmutableList<PlaylistDetailSourceRow> sourceRows;
    // 順序木は捕捉した比較キーを保ち、編集中の最新表示投影だけをこちらで置換します。
    private readonly Dictionary<BMSTableEntry, PlaylistDetailSourceRow> entries = [];
    private readonly List<Action> notifications = [];
    private readonly Action<string> observeWork;
    private PlaylistRequestIdentity? currentIdentity;

    private readonly Dictionary<BMSTableEntry, PlaylistDetailRow> realizedRows = [];
    private readonly Dictionary<BMSTableEntry, (PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After)> deferred = [];
    public event NotifyCollectionChangedEventHandler CollectionChanged;

    private readonly int distinctFolderCount;
    private readonly Dictionary<string, int> folderCounts = new(StringComparer.Ordinal);

    /// <summary>初回・明示条件変更の評価済み順序を取り込みます。局所更新では変更経路だけを共有します。</summary>
    internal PlaylistDetailVirtualView(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        int distinctFolderCount = -1, Action<string> observeWork = null, PlaylistRequestIdentity? identity = null)
    {
        this.observeWork = observeWork;
        currentIdentity = identity;
        this.sourceRows = (sourceRows ?? []).ToImmutableList();
        foreach (PlaylistDetailSourceRow row in this.sourceRows)
        { entries.Add(row.Entry, row); observeWork?.Invoke("playlist_order_buffer"); }
        this.distinctFolderCount = distinctFolderCount;
        if (distinctFolderCount >= 0) { foreach (PlaylistDetailSourceRow row in this.sourceRows) { ChangeFolderCount(row, 1); } }
    }

    public int Count => sourceRows.Count;

    public int RowCount => Count;

    public int DistinctFolderCount => distinctFolderCount < 0 ? -1 : folderCounts.Count;

    public int RealizedRowCount => realizedRows.Count;

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object this[int index]
    {
        get => GetOrCreate(index);
        set => throw new NotSupportedException();
    }

    public IEnumerator GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return GetOrCreate(index);
        }
    }

    public void DisposeRealizedRows()
    {
        foreach (PlaylistDetailRow row in realizedRows.Values)
        {
            if ((object)row is IDisposable disposable) { disposable.Dispose(); }
        }
        realizedRows.Clear();
    }

    /// <summary>対象行の入力と依存する所属・順序だけを更新し、他行の実体化と選択を保ちます。</summary>
    internal void ApplyChange(PlaylistDetailSourceRow before, PlaylistDetailSourceRow after,
        PlaylistRequestIdentity identity, bool deferPosition)
    {
        currentIdentity = identity;
        BMSTableEntry entry = after?.Entry ?? before.Entry;
        bool present = entries.TryGetValue(entry, out PlaylistDetailSourceRow ordered);
        realizedRows.TryGetValue(entry, out PlaylistDetailRow realized);
        if (after != null && realized != null)
        {
            realized.ApplySource(after, notify: false);
            notifications.Add(realized.PublishSourceChanged);
        }
        if (deferPosition)
        {
            deferred[entry] = (deferred.TryGetValue(entry, out (PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After) prior) ? prior.Before : before, after);
            return;
        }
        bool matches = after != null && Matches(after, identity);
        bool positionChanged = ordered == null || after == null || !SameSortInput(ordered, after, identity);
        if (present && matches && !positionChanged)
        {
            if (ordered.Folder != after.Folder) { ChangeFolderCount(ordered, -1); ChangeFolderCount(after, 1); }
            // 同値キーのままなので木の経路だけを置換します。
            sourceRows = sourceRows.SetItem(Find(ordered, identity), after);
            entries[entry] = after;
            observeWork?.Invoke("playlist_order_write");
            return;
        }
        if (!present && !matches) { return; }
        int index = present ? Find(ordered, identity) : -1;
        if (present)
        {
            ChangeFolderCount(ordered, -1);
            sourceRows = sourceRows.RemoveAt(index);
            entries.Remove(entry);
            observeWork?.Invoke("playlist_order_write");
        }
        if (!matches)
        {
            realizedRows.Remove(entry);
            QueueCollectionChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove,
                realized ?? Materialize(before), index));
            return;
        }
        int low = FindInsertion(after, identity);
        sourceRows = sourceRows.Insert(low, after);
        entries[entry] = after;
        ChangeFolderCount(after, 1);
        observeWork?.Invoke("playlist_order_write");
        if (present)
        {
            if (low != index)
            { QueueCollectionChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, realized ?? GetOrCreate(low), low, index)); }
        }
        else { QueueCollectionChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, GetOrCreate(low), low)); }
    }

    /// <summary>一配送の対象通知だけを、管理状態の確定と lock 解放後に公開します。</summary>
    internal void PublishChanges()
    {
        Action[] pending = notifications.ToArray();
        foreach (Action _ in pending) { observeWork?.Invoke("playlist_notification_buffer"); }
        notifications.Clear();
        foreach (Action notification in pending) { notification(); }
    }

    private void QueueCollectionChange(NotifyCollectionChangedEventArgs args)
        => notifications.Add(() => CollectionChanged?.Invoke(this, args));

    private int Find(PlaylistDetailSourceRow row, PlaylistRequestIdentity identity)
    {
        int index = FindInsertion(row, identity);
        return index < sourceRows.Count && ReferenceEquals(sourceRows[index].Entry, row.Entry) ? index
            : throw new InvalidOperationException("The playlist view lost its captured entry order.");
    }

    private int FindInsertion(PlaylistDetailSourceRow row, PlaylistRequestIdentity identity)
    {
        int low = 0, high = sourceRows.Count;
        while (low < high)
        {
            observeWork?.Invoke("playlist_order_access");
            int middle = low + (high - low) / 2;
            observeWork?.Invoke("playlist_order_comparison");
            if (PlaylistDetailSortEngine.Compare(sourceRows[middle], row, identity) < 0) { low = middle + 1; }
            else { high = middle; }
        }
        return low;
    }

    /// <summary>セル編集中に集約した行だけを、編集終了後に再配置します。</summary>
    internal void FlushDeferred(PlaylistRequestIdentity identity)
    {
        (PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After)[] changes = deferred.Values.ToArray();
        deferred.Clear();
        foreach ((PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After) change in changes) { ApplyChange(change.Before, change.After, identity, deferPosition: false); }
    }

    private static bool SameSortInput(PlaylistDetailSourceRow before, PlaylistDetailSourceRow after, PlaylistRequestIdentity identity)
        => PlaylistDetailSortEngine.HaveSameSortInput(before, after, identity);

    internal static bool Matches(PlaylistDetailSourceRow row, PlaylistRequestIdentity identity)
    {
        if (identity.FilterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected && row.IsOwned) { return false; }
        if (!string.IsNullOrWhiteSpace(identity.KeywordFilter)
            && !GridKeywordSearchQuery.Parse(identity.KeywordFilter).MatchesPlaylistDetail(row)) { return false; }
        ChartModeFilter mode = identity.ModeFilter;
        return mode == ChartModeFilter.All || row.mode == null || row.mode switch
        {
            5 => (mode & ChartModeFilter._5KEYS) != 0,
            7 => (mode & ChartModeFilter._7KEYS) != 0,
            9 => (mode & ChartModeFilter._9KEYS) != 0,
            10 => (mode & ChartModeFilter._10KEYS) != 0,
            14 => (mode & ChartModeFilter._14KEYS) != 0,
            _ => false
        };
    }

    private void ChangeFolderCount(PlaylistDetailSourceRow row, int delta)
    {
        if (distinctFolderCount < 0) { return; }
        string folder = row.Folder ?? string.Empty;
        folderCounts.TryGetValue(folder, out int count);
        if (count + delta == 0) { folderCounts.Remove(folder); }
        else { folderCounts[folder] = count + delta; }
    }

    public int Add(object value)
    {
        throw new NotSupportedException();
    }

    public void Clear()
    {
        throw new NotSupportedException();
    }

    public bool Contains(object value)
    {
        return IndexOf(value) >= 0;
    }

    public int IndexOf(object value)
    {
        if (value == null)
        {
            return -1;
        }
        if (value is not PlaylistDetailRow row || !realizedRows.TryGetValue(row.Entry, out PlaylistDetailRow realized)
            || !ReferenceEquals(realized, row) || !entries.TryGetValue(row.Entry, out PlaylistDetailSourceRow source)) { return -1; }
        return currentIdentity.HasValue ? Find(source, currentIdentity.Value) : sourceRows.IndexOf(source);
    }

    public void Insert(int index, object value)
    {
        throw new NotSupportedException();
    }

    public void Remove(object value)
    {
        throw new NotSupportedException();
    }

    public void RemoveAt(int index)
    {
        throw new NotSupportedException();
    }

    public void CopyTo(Array array, int index)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }
        for (int offset = 0; offset < Count; offset++)
        {
            array.SetValue(GetOrCreate(offset), index + offset);
        }
    }

    private PlaylistDetailRow GetOrCreate(int index)
    {
        if (index < 0 || index >= sourceRows.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        PlaylistDetailSourceRow sourceRow = sourceRows[index];
        realizedRows.TryGetValue(sourceRow.Entry, out PlaylistDetailRow realized);
        if (realized != null)
        {
            return realized;
        }
        if (sourceRow == null)
        {
            return null;
        }
        realized = Materialize(deferred.TryGetValue(sourceRow.Entry, out (PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After) change) ? change.After ?? sourceRow : sourceRow);
        realizedRows[sourceRow.Entry] = realized;
        return realized;
    }

    private PlaylistDetailRow Materialize(PlaylistDetailSourceRow source)
    {
        observeWork?.Invoke("playlist_detail_materialized");
        return source.CreateViewRow();
    }
}
