using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// storage 行の現在値と、順序・exact lookup に必要な不変の位置情報をまとめます。
/// </summary>
internal sealed class CatalogStorageSequenceEntry<T>
{
    /// <summary>storage 行と lookup・順序情報を結び付けます。</summary>
    /// <param name="value">live owner row。</param>
    /// <param name="exactPath">row を識別する exact path。</param>
    /// <param name="sortKey">現在の sequence 比較に使う key。</param>
    /// <param name="ordinal">同値 key の安定順序。</param>
    internal CatalogStorageSequenceEntry(
        T value,
        string exactPath,
        string sortKey,
        long ordinal)
    {
        Value = value;
        ExactPath = exactPath;
        SortKey = sortKey;
        Ordinal = ordinal;
    }

    /// <summary>live owner row。</summary>
    internal T Value { get; }

    /// <summary>row の exact path。</summary>
    internal string ExactPath { get; }

    /// <summary>sequence の比較 key。</summary>
    internal string SortKey { get; }

    /// <summary>同値 key の安定順序。</summary>
    internal long Ordinal { get; }
}

/// <summary>
/// storage sequence の実アクセスを記録する内部観測口です。
/// production では未指定で、変更処理の仕事量を別状態として保持しません。
/// </summary>
internal interface ICatalogStorageSequenceWorkObserver
{
    /// <summary>sequence の index access を一回記録します。</summary>
    void ObserveAccess();

    /// <summary>sequence の列挙開始を一回記録します。</summary>
    void ObserveEnumeration();

    /// <summary>sequence の列挙で実際に訪問した entry を一つ記録します。</summary>
    void ObserveEntryVisit();

    /// <summary>全 entry を読み出す明示的な materialization を記録します。</summary>
    void ObserveMaterialization(int count);
}

/// <summary>
/// index で読める永続 sequence です。
/// 更新時は ImmutableList の変更箇所だけを共有し、既存 snapshot を変更しません。
/// </summary>
internal sealed class CatalogStorageIndexedSequence<T>
{
    private readonly ImmutableList<CatalogStorageSequenceEntry<T>> entries;

    private readonly Comparison<CatalogStorageSequenceEntry<T>> comparison;

    private readonly ICatalogStorageSequenceWorkObserver workObserver;

    private CatalogStorageIndexedSequence(
        ImmutableList<CatalogStorageSequenceEntry<T>> entries,
        Comparison<CatalogStorageSequenceEntry<T>> comparison,
        ICatalogStorageSequenceWorkObserver workObserver)
    {
        this.entries = entries ?? throw new ArgumentNullException(nameof(entries));
        this.comparison = comparison ?? throw new ArgumentNullException(nameof(comparison));
        this.workObserver = workObserver;
    }

    /// <summary>空の sequence を作成します。</summary>
    /// <param name="comparison">entry の順序比較。</param>
    /// <param name="workObserver">実アクセスの任意観測口。</param>
    internal static CatalogStorageIndexedSequence<T> Empty(
        Comparison<CatalogStorageSequenceEntry<T>> comparison,
        ICatalogStorageSequenceWorkObserver workObserver = null)
    {
        return new CatalogStorageIndexedSequence<T>(
            ImmutableList<CatalogStorageSequenceEntry<T>>.Empty,
            comparison,
            workObserver);
    }

    /// <summary>既に比較順となっている入力から初期 sequence を作成します。</summary>
    /// <param name="entries">比較順に並んだ初期 entry。</param>
    /// <param name="comparison">entry の順序比較。</param>
    /// <param name="workObserver">実アクセスの任意観測口。</param>
    internal static CatalogStorageIndexedSequence<T> FromEntries(
        IReadOnlyList<CatalogStorageSequenceEntry<T>> entries,
        Comparison<CatalogStorageSequenceEntry<T>> comparison,
        ICatalogStorageSequenceWorkObserver workObserver = null)
    {
        if (entries == null || entries.Count == 0)
        {
            return Empty(comparison, workObserver);
        }
        ImmutableList<CatalogStorageSequenceEntry<T>> immutableEntries =
            ImmutableList.CreateRange(entries);
        workObserver?.ObserveMaterialization(entries.Count);
        return new CatalogStorageIndexedSequence<T>(immutableEntries, comparison, workObserver);
    }

    /// <summary>格納要素数を返します。</summary>
    internal int Count => entries.Count;

    /// <summary>指定 index の内部 entry を返します。</summary>
    /// <param name="index">取得する index。</param>
    internal CatalogStorageSequenceEntry<T> EntryAt(int index)
    {
        CatalogStorageSequenceEntry<T> entry = entries[index];
        workObserver?.ObserveAccess();
        return entry;
    }

    /// <summary>sequence の末尾へ entry を追加した新世代を返します。</summary>
    /// <param name="entry">追加する entry。</param>
    internal CatalogStorageIndexedSequence<T> Append(
        CatalogStorageSequenceEntry<T> entry)
    {
        return InsertAt(Count, entry);
    }

    /// <summary>指定位置へ entry を挿入した新世代を返します。</summary>
    /// <param name="index">挿入位置。</param>
    /// <param name="entry">挿入する entry。</param>
    internal CatalogStorageIndexedSequence<T> InsertAt(
        int index,
        CatalogStorageSequenceEntry<T> entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        return new CatalogStorageIndexedSequence<T>(
            entries.Insert(index, entry),
            comparison,
            workObserver);
    }

    /// <summary>指定位置を除去した新世代を返します。</summary>
    /// <param name="index">除去位置。</param>
    internal CatalogStorageIndexedSequence<T> RemoveAt(int index)
    {
        return new CatalogStorageIndexedSequence<T>(entries.RemoveAt(index), comparison, workObserver);
    }

    /// <summary>指定位置を置換した新世代を返します。</summary>
    /// <param name="index">置換位置。</param>
    /// <param name="entry">置換後の entry。</param>
    internal CatalogStorageIndexedSequence<T> ReplaceAt(
        int index,
        CatalogStorageSequenceEntry<T> entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        return new CatalogStorageIndexedSequence<T>(
            entries.SetItem(index, entry),
            comparison,
            workObserver);
    }

    /// <summary>同じentry列を共有したまま比較器だけを切り替えた新世代を返します。</summary>
    /// <param name="comparison">新しいentryの順序比較。</param>
    internal CatalogStorageIndexedSequence<T> WithComparison(
        Comparison<CatalogStorageSequenceEntry<T>> comparison)
    {
        return new CatalogStorageIndexedSequence<T>(
            entries,
            comparison ?? throw new ArgumentNullException(nameof(comparison)),
            workObserver);
    }

    /// <summary>
    /// 指定範囲だけをsequenceから捕捉します。変更されない範囲は列挙しません。
    /// </summary>
    /// <param name="index">捕捉開始位置。</param>
    /// <param name="count">捕捉する件数。</param>
    internal IReadOnlyList<CatalogStorageSequenceEntry<T>> CaptureRange(int index, int count)
    {
        ValidateRange(index, count);
        var captured = new List<CatalogStorageSequenceEntry<T>>(count);
        workObserver?.ObserveEnumeration();
        for (int offset = 0; offset < count; offset++)
        {
            CatalogStorageSequenceEntry<T> entry = entries[index + offset];
            workObserver?.ObserveAccess();
            workObserver?.ObserveEntryVisit();
            captured.Add(entry);
        }
        workObserver?.ObserveMaterialization(count);
        return captured;
    }

    /// <summary>指定範囲だけを置換した新世代を返します。</summary>
    /// <param name="index">置換開始位置。</param>
    /// <param name="count">除去する件数。</param>
    /// <param name="replacement">置換後のentry。</param>
    internal CatalogStorageIndexedSequence<T> ReplaceRange(
        int index,
        int count,
        IReadOnlyList<CatalogStorageSequenceEntry<T>> replacement)
    {
        ValidateRange(index, count);
        ArgumentNullException.ThrowIfNull(replacement);
        ImmutableList<CatalogStorageSequenceEntry<T>> nextEntries = entries.RemoveRange(index, count);
        if (replacement.Count > 0)
        {
            nextEntries = nextEntries.InsertRange(index, replacement);
        }
        return new CatalogStorageIndexedSequence<T>(nextEntries, comparison, workObserver);
    }

    /// <summary>同じ entry の index を二分探索で返します。</summary>
    /// <param name="entry">検索する entry。</param>
    internal int FindIndex(CatalogStorageSequenceEntry<T> entry)
    {
        if (entry == null)
        {
            return -1;
        }

        int low = 0;
        int high = entries.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            CatalogStorageSequenceEntry<T> current = entries[middle];
            workObserver?.ObserveAccess();
            int compared = comparison(current, entry);
            if (compared == 0)
            {
                return ReferenceEquals(current, entry) ? middle : -1;
            }
            if (compared < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return -1;
    }

    /// <summary>
    /// candidate より大きい最初の要素の index を返します。同じ sort key は既存要素の後ろです。
    /// </summary>
    /// <param name="candidate">挿入候補。</param>
    internal int FindInsertionIndex(CatalogStorageSequenceEntry<T> candidate)
    {
        if (candidate == null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }
        int low = 0;
        int high = entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            CatalogStorageSequenceEntry<T> current = entries[middle];
            workObserver?.ObserveAccess();
            if (comparison(current, candidate) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>現在世代の entry を順序どおり列挙します。</summary>
    internal IEnumerable<CatalogStorageSequenceEntry<T>> EnumerateEntries()
    {
        workObserver?.ObserveEnumeration();
        foreach (CatalogStorageSequenceEntry<T> entry in entries)
        {
            workObserver?.ObserveEntryVisit();
            yield return entry;
        }
    }

    /// <summary>read view の明示的な全件 materialization を記録します。</summary>
    internal void ObserveMaterialization()
    {
        workObserver?.ObserveMaterialization(entries.Count);
    }

    private void ValidateRange(int index, int count)
    {
        if (index < 0 || count < 0 || index > entries.Count - count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}

/// <summary>
/// immutable sequence を IList-compatible な read-only view として公開します。
/// 要素の materialize は CopyTo など明示的な利用時だけ行われます。
/// </summary>
internal sealed class CatalogStorageReadOnlyView<T> : IReadOnlyList<T>, IList<T>, IList
{
    private readonly CatalogStorageIndexedSequence<T> sequence;

    /// <summary>指定された sequence を read-only projection として公開します。</summary>
    /// <param name="sequence">projection の backing sequence。</param>
    internal CatalogStorageReadOnlyView(CatalogStorageIndexedSequence<T> sequence)
    {
        this.sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
    }

    public int Count => sequence.Count;

    public T this[int index]
    {
        get => sequence.EntryAt(index).Value;
        set => throw new NotSupportedException();
    }

    bool ICollection<T>.IsReadOnly => true;

    bool IList.IsReadOnly => true;

    bool IList.IsFixedSize => true;

    object ICollection.SyncRoot => this;

    bool ICollection.IsSynchronized => false;

    object IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (CatalogStorageSequenceEntry<T> entry in sequence.EnumerateEntries())
        {
            yield return entry.Value;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(T item)
    {
        int index = 0;
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        foreach (T value in this)
        {
            if (comparer.Equals(value, item))
            {
                return index;
            }
            index++;
        }
        return -1;
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length - Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        }
        sequence.ObserveMaterialization();
        int index = arrayIndex;
        foreach (T value in this)
        {
            array[index++] = value;
        }
    }

    public void Add(T item) => throw new NotSupportedException();

    public void Insert(int index, T item) => throw new NotSupportedException();

    public bool Remove(T item) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    int IList.Add(object value) => throw new NotSupportedException();

    bool IList.Contains(object value)
    {
        return value is T typed
            ? Contains(typed)
            : value is null && default(T) is null && Contains(default);
    }

    int IList.IndexOf(object value)
    {
        return value is T typed
            ? IndexOf(typed)
            : value is null && default(T) is null ? IndexOf(default) : -1;
    }

    void IList.Insert(int index, object value) => throw new NotSupportedException();

    void IList.Remove(object value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Rank != 1 || index < 0 || index > array.Length - Count)
        {
            throw new ArgumentException("The destination array is invalid.", nameof(array));
        }
        sequence.ObserveMaterialization();
        int destinationIndex = index;
        foreach (T value in this)
        {
            array.SetValue(value, destinationIndex++);
        }
    }
}
