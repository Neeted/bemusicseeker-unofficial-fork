using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;

namespace BeMusicSeeker.Views;

/// <summary>保持範囲の全行参照を数え、通知対応行だけを購読します。</summary>
internal sealed class CustomTableRowChangeTracker
{
    private readonly Dictionary<object, int> retainedRows = new(ReferenceEqualityComparer<object>.Instance);
    private readonly Action<INotifyPropertyChanged, PropertyChangedEventArgs> rowChanged;
    private readonly Action<object> rowReleased;

    /// <summary>行変更の配送先と、最後の参照が離れた行の破棄先を接続します。</summary>
    internal CustomTableRowChangeTracker(Action<INotifyPropertyChanged, PropertyChangedEventArgs> rowChanged, Action<object> rowReleased = null)
    {
        this.rowChanged = rowChanged ?? throw new ArgumentNullException(nameof(rowChanged));
        this.rowReleased = rowReleased;
    }

    /// <summary>保持行のうち変更通知を購読している異なる行参照の数を返します。</summary>
    internal int SubscribedRowCount => retainedRows.Keys.Count(row => row is INotifyPropertyChanged);

    /// <summary>指定範囲の参照を保持し、残る参照の購読と値寿命を維持します。</summary>
    internal void ReplaceVisibleRows(IList rows, int firstIndex, int count)
    {
        if (rows == null || rows.Count == 0 || count <= 0)
        {
            DetachAllRows();
            return;
        }
        int startIndex = Math.Max(0, firstIndex);
        int endIndex = Math.Min(rows.Count, startIndex + count);
        var targetRows = new Dictionary<object, int>(ReferenceEqualityComparer<object>.Instance);
        for (int i = startIndex; i < endIndex; i++)
        {
            object row = rows[i];
            if (row != null)
            {
                targetRows.TryGetValue(row, out int existingCount);
                targetRows[row] = existingCount + 1;
            }
        }
        foreach (object row in retainedRows.Keys.ToArray())
        {
            if (!targetRows.ContainsKey(row))
            {
                ReleaseRow(row);
            }
        }
        foreach (KeyValuePair<object, int> target in targetRows)
        {
            if (!retainedRows.ContainsKey(target.Key) && target.Key is INotifyPropertyChanged notifyingRow)
            {
                PropertyChangedEventManager.AddHandler(notifyingRow, RowPropertyChanged, string.Empty);
            }
            retainedRows[target.Key] = target.Value;
        }
    }

    private void ReleaseRow(object row)
    {
        if (row is INotifyPropertyChanged notifyingRow)
        {
            PropertyChangedEventManager.RemoveHandler(notifyingRow, RowPropertyChanged, string.Empty);
        }
        retainedRows.Remove(row);
        rowReleased?.Invoke(row);
    }

    /// <summary>非表示・購読終了時に全参照の購読と値寿命を終了します。</summary>
    internal void DetachAllRows()
    {
        foreach (object row in retainedRows.Keys.ToArray())
        {
            ReleaseRow(row);
        }
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
    private readonly object syncRoot = new();
    private readonly HashSet<object> rows = new(ReferenceEqualityComparer<object>.Instance);

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
                return [];
            }
            object[] drainedRows = [.. rows];
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
    internal static readonly ReferenceEqualityComparer<T> Instance = new();

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
