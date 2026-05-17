using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistDetailVirtualView : IList, IChartListViewMetadata
{
    private readonly IReadOnlyList<PlaylistDetailSourceRow> sourceRows;

    private readonly PlaylistDetailRow[] realizedRows;

    private readonly int distinctFolderCount;

    internal PlaylistDetailVirtualView(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        int distinctFolderCount = -1)
    {
        this.sourceRows = sourceRows ?? [];
        realizedRows = new PlaylistDetailRow[this.sourceRows.Count];
        this.distinctFolderCount = distinctFolderCount;
    }

    public int Count => sourceRows.Count;

    public int RowCount => Count;

    public int DistinctFolderCount => distinctFolderCount;

    public int RealizedRowCount => realizedRows.Count(row => row != null);

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
        for (int index = 0; index < realizedRows.Length; index++)
        {
            object row = realizedRows[index];
            if (row is IDisposable disposable)
            {
                disposable.Dispose();
            }
            realizedRows[index] = null;
        }
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
        for (int index = 0; index < realizedRows.Length; index++)
        {
            if (ReferenceEquals(realizedRows[index], value))
            {
                return index;
            }
        }
        return -1;
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
        PlaylistDetailRow realized = realizedRows[index];
        if (realized != null)
        {
            return realized;
        }
        PlaylistDetailSourceRow sourceRow = sourceRows[index];
        if (sourceRow == null)
        {
            return null;
        }
        realized = sourceRow.CreateViewRow();
        realizedRows[index] = realized;
        return realized;
    }
}
