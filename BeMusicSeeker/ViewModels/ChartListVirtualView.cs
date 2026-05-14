using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListVirtualView : IList, IChartListViewMetadata
{
    private readonly IReadOnlyList<ChartListSourceRow> sourceRows;
    private readonly int[] orderedIndexes;
    private readonly Func<ChartListSourceRow, LibraryChartRow> rowFactory;
    private readonly LibraryChartRow[] realizedRows;
    private readonly int distinctFolderCount;

    internal ChartListVirtualView(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        ChartListOrder order,
        Func<ChartListSourceRow, LibraryChartRow> rowFactory)
    {
        this.sourceRows = sourceRows ?? Array.Empty<ChartListSourceRow>();
        orderedIndexes = order?.Indexes ?? Array.Empty<int>();
        this.rowFactory = rowFactory ?? throw new ArgumentNullException(nameof(rowFactory));
        realizedRows = new LibraryChartRow[orderedIndexes.Length];
        distinctFolderCount = this.sourceRows
            .Select(row => row?.Folder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    public int Count => orderedIndexes.Length;

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
        for (int i = 0; i < Count; i++)
        {
            yield return GetOrCreate(i);
        }
    }

    public void DisposeRealizedRows()
    {
        for (int i = 0; i < realizedRows.Length; i++)
        {
            object row = realizedRows[i];
            if (row is IDisposable disposable)
            {
                disposable.Dispose();
            }
            realizedRows[i] = null;
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
        for (int i = 0; i < realizedRows.Length; i++)
        {
            if (ReferenceEquals(realizedRows[i], value))
            {
                return i;
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
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(GetOrCreate(i), index + i);
        }
    }

    private LibraryChartRow GetOrCreate(int index)
    {
        if (index < 0 || index >= orderedIndexes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        LibraryChartRow realized = realizedRows[index];
        if (realized != null)
        {
            return realized;
        }
        int sourceIndex = orderedIndexes[index];
        if (sourceIndex < 0 || sourceIndex >= sourceRows.Count)
        {
            return null;
        }
        realized = rowFactory(sourceRows[sourceIndex]);
        realizedRows[index] = realized;
        return realized;
    }
}
