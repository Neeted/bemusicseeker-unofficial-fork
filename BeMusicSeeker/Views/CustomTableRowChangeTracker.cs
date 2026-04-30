using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;

namespace BeMusicSeeker.Views;

internal sealed class CustomTableRowChangeTracker
{
    private readonly Dictionary<INotifyPropertyChanged, int> subscribedRows = new Dictionary<INotifyPropertyChanged, int>(ReferenceEqualityComparer<INotifyPropertyChanged>.Instance);
    private readonly Action<INotifyPropertyChanged, PropertyChangedEventArgs> rowChanged;

    internal CustomTableRowChangeTracker(Action<INotifyPropertyChanged, PropertyChangedEventArgs> rowChanged)
    {
        this.rowChanged = rowChanged ?? throw new ArgumentNullException(nameof(rowChanged));
    }

    internal int SubscribedRowCount => subscribedRows.Count;

    internal void ReplaceVisibleRows(IList rows, int firstIndex, int count)
    {
        if (rows == null || rows.Count == 0 || count <= 0)
        {
            DetachAllRows();
            return;
        }
        int startIndex = Math.Max(0, firstIndex);
        if (startIndex >= rows.Count)
        {
            DetachAllRows();
            return;
        }
        int endIndex = Math.Min(rows.Count, startIndex + count);
        Dictionary<INotifyPropertyChanged, int> targetRows = new Dictionary<INotifyPropertyChanged, int>(ReferenceEqualityComparer<INotifyPropertyChanged>.Instance);
        for (int i = startIndex; i < endIndex; i++)
        {
            if (rows[i] is INotifyPropertyChanged row)
            {
                targetRows.TryGetValue(row, out int existingCount);
                targetRows[row] = existingCount + 1;
            }
        }
        foreach (KeyValuePair<INotifyPropertyChanged, int> currentRow in subscribedRows.ToArray())
        {
            targetRows.TryGetValue(currentRow.Key, out int targetCount);
            for (int i = currentRow.Value; i > targetCount; i--)
            {
                DetachRow(currentRow.Key);
            }
        }
        foreach (KeyValuePair<INotifyPropertyChanged, int> targetRow in targetRows)
        {
            subscribedRows.TryGetValue(targetRow.Key, out int currentCount);
            for (int i = currentCount; i < targetRow.Value; i++)
            {
                AttachRow(targetRow.Key);
            }
        }
    }

    private void AttachRow(INotifyPropertyChanged row)
    {
        if (row == null)
        {
            return;
        }
        if (subscribedRows.TryGetValue(row, out int count))
        {
            subscribedRows[row] = count + 1;
            return;
        }
        subscribedRows.Add(row, 1);
        PropertyChangedEventManager.AddHandler(row, RowPropertyChanged, string.Empty);
    }

    private void DetachRow(INotifyPropertyChanged row)
    {
        if (row == null || !subscribedRows.TryGetValue(row, out int count))
        {
            return;
        }
        if (count > 1)
        {
            subscribedRows[row] = count - 1;
            return;
        }
        subscribedRows.Remove(row);
        PropertyChangedEventManager.RemoveHandler(row, RowPropertyChanged, string.Empty);
    }

    internal void DetachAllRows()
    {
        foreach (INotifyPropertyChanged row in subscribedRows.Keys)
        {
            PropertyChangedEventManager.RemoveHandler(row, RowPropertyChanged, string.Empty);
        }
        subscribedRows.Clear();
    }

    private void RowPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        rowChanged(sender as INotifyPropertyChanged, e);
    }
}

internal sealed class CustomTableRedrawScheduler
{
    private readonly Dispatcher dispatcher;
    private readonly DispatcherPriority priority;
    private readonly Action redrawAction;
    private int isPending;
    private int scheduledCount;

    internal CustomTableRedrawScheduler(Dispatcher dispatcher, Action redrawAction, DispatcherPriority priority = DispatcherPriority.Render)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.redrawAction = redrawAction ?? throw new ArgumentNullException(nameof(redrawAction));
        this.priority = priority;
    }

    internal int ScheduledCount => Volatile.Read(ref scheduledCount);

    internal void Request()
    {
        if (Interlocked.Exchange(ref isPending, 1) == 1)
        {
            return;
        }
        Interlocked.Increment(ref scheduledCount);
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref isPending, 0);
            return;
        }
        dispatcher.BeginInvoke(priority, (Action)Run);
    }

    private void Run()
    {
        Interlocked.Exchange(ref isPending, 0);
        redrawAction();
    }
}

internal sealed class CustomTableRowInvalidationQueue
{
    private readonly object syncRoot = new object();
    private readonly HashSet<object> rows = new HashSet<object>(ReferenceEqualityComparer<object>.Instance);

    internal int Count
    {
        get
        {
            lock (syncRoot)
            {
                return rows.Count;
            }
        }
    }

    internal bool Enqueue(object row)
    {
        if (row == null)
        {
            return false;
        }
        lock (syncRoot)
        {
            return rows.Add(row);
        }
    }

    internal object[] Drain()
    {
        lock (syncRoot)
        {
            if (rows.Count == 0)
            {
                return Array.Empty<object>();
            }
            object[] drainedRows = rows.ToArray();
            rows.Clear();
            return drainedRows;
        }
    }

    internal void Clear()
    {
        lock (syncRoot)
        {
            rows.Clear();
        }
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    internal static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

    private ReferenceEqualityComparer()
    {
    }

    public bool Equals(T x, T y)
    {
        return ReferenceEquals(x, y);
    }

    public int GetHashCode(T obj)
    {
        return RuntimeHelpers.GetHashCode(obj);
    }
}
