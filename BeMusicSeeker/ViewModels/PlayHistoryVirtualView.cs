using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryVirtualView : IList, IChartListViewMetadata
{
    private readonly IReadOnlyList<PlayHistoryRow> rows;

    private readonly int distinctFolderCount;

    internal PlayHistoryVirtualView(IReadOnlyList<PlayHistoryRow> rows, int distinctFolderCount = -1)
    {
        this.rows = rows ?? [];
        this.distinctFolderCount = distinctFolderCount;
    }

    public int Count => rows.Count;

    public int RowCount => Count;

    public int DistinctFolderCount => distinctFolderCount;

    public int RealizedRowCount => Count;

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object this[int index]
    {
        get
        {
            if (index < 0 || index >= rows.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return rows[index];
        }
        set => throw new NotSupportedException();
    }

    public IEnumerator GetEnumerator()
    {
        return rows.GetEnumerator();
    }

    public void DisposeRealizedRows()
    {
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
        return rows.Contains(value);
    }

    public int IndexOf(object value)
    {
        if (value is not PlayHistoryRow row)
        {
            return -1;
        }
        for (int index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], row))
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
        for (int offset = 0; offset < rows.Count; offset++)
        {
            array.SetValue(rows[offset], index + offset);
        }
    }
}
