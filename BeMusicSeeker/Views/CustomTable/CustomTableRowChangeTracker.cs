using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;

namespace BeMusicSeeker.Views;

/// <summary>
/// 保持範囲（可視行と上下の余白の行）の行参照を数え、通知対応行だけを購読します。
/// 範囲を上下へ動かすときは、入る位置だけを行集合から読み、残る行へは触れません。
/// 購読と値の寿命は行参照ごとに数え、同じ参照が複数の位置にあれば最後の位置が離れるまで保ちます。
/// </summary>
internal sealed class CustomTableRowChangeTracker
{
    private readonly Dictionary<object, int> retainedRows = new(ReferenceEqualityComparer<object>.Instance);
    private readonly Action<INotifyPropertyChanged, PropertyChangedEventArgs> rowChanged;
    private readonly Action<object> rowReleased;
    // 範囲の各位置で保持した行参照です。出る行の参照を行集合から読み直さずに解放するために持ちます。
    private object[] window = [];
    private int windowFirstIndex;
    private IList windowSource;

    /// <summary>行変更の配送先と、最後の参照が離れた行の破棄先を接続します。</summary>
    internal CustomTableRowChangeTracker(Action<INotifyPropertyChanged, PropertyChangedEventArgs> rowChanged, Action<object> rowReleased = null)
    {
        this.rowChanged = rowChanged ?? throw new ArgumentNullException(nameof(rowChanged));
        this.rowReleased = rowReleased;
    }

    /// <summary>保持行のうち変更通知を購読している異なる行参照の数を返します。</summary>
    internal int SubscribedRowCount => retainedRows.Keys.Count(row => row is INotifyPropertyChanged);

    /// <summary>
    /// 保持範囲を指定の範囲へ動かします。前回と同じ行集合で範囲が重なる場合は、入る位置だけを読み、購読の追加は入る行、解除は出る行に限ります。
    /// 同じ範囲なら行集合へ触れません。前回がない場合と範囲が重ならない場合は <see cref="Rebuild"/> と同じです。
    /// </summary>
    /// <param name="rows">行集合。</param>
    /// <param name="firstIndex">保持範囲の先頭位置。</param>
    /// <param name="count">保持範囲の位置の数。行集合の末尾を越える分は切り詰めます。</param>
    internal void Move(IList rows, int firstIndex, int count)
    {
        if (!TryResolveRange(rows, firstIndex, count, out int startIndex, out int endIndex))
        {
            DetachAllRows();
            return;
        }
        int previousStart = windowFirstIndex;
        int previousEnd = previousStart + window.Length;
        if (!ReferenceEquals(rows, windowSource) || endIndex <= previousStart || startIndex >= previousEnd)
        {
            Rebuild(rows, firstIndex, count);
            return;
        }
        if (startIndex == previousStart && endIndex == previousEnd)
        {
            return;
        }
        int overlapStart = Math.Max(startIndex, previousStart);
        int overlapEnd = Math.Min(endIndex, previousEnd);
        object[] next = new object[endIndex - startIndex];
        Array.Copy(window, overlapStart - previousStart, next, overlapStart - startIndex, overlapEnd - overlapStart);
        // 入る行を先に数え、同じ参照が出ると同時に入る場合に購読を外して付け直さない。
        for (int i = startIndex; i < overlapStart; i++)
        {
            next[i - startIndex] = AcquireRow(rows[i]);
        }
        for (int i = overlapEnd; i < endIndex; i++)
        {
            next[i - startIndex] = AcquireRow(rows[i]);
        }
        for (int i = previousStart; i < overlapStart; i++)
        {
            ReleaseReference(window[i - previousStart]);
        }
        for (int i = overlapEnd; i < previousEnd; i++)
        {
            ReleaseReference(window[i - previousStart]);
        }
        window = next;
        windowFirstIndex = startIndex;
        windowSource = rows;
    }

    /// <summary>
    /// 指定範囲の全位置を読み直して保持範囲を作り直します。同じ位置で参照が替わった行は新しい参照を購読して旧い参照を解除し、
    /// 残る参照の購読と値寿命は維持します。行集合の差替え・変更通知・全体の取り直しで使います。
    /// </summary>
    /// <param name="rows">行集合。</param>
    /// <param name="firstIndex">保持範囲の先頭位置。</param>
    /// <param name="count">保持範囲の位置の数。行集合の末尾を越える分は切り詰めます。</param>
    internal void Rebuild(IList rows, int firstIndex, int count)
    {
        if (!TryResolveRange(rows, firstIndex, count, out int startIndex, out int endIndex))
        {
            DetachAllRows();
            return;
        }
        object[] next = new object[endIndex - startIndex];
        var targetRows = new Dictionary<object, int>(ReferenceEqualityComparer<object>.Instance);
        for (int i = startIndex; i < endIndex; i++)
        {
            object row = rows[i];
            next[i - startIndex] = row;
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
        window = next;
        windowFirstIndex = startIndex;
        windowSource = rows;
    }

    /// <summary>
    /// 保持範囲が保持している位置の行参照を、行集合へ触れずに返します。指定の行集合に対する範囲の外、または保持していないときは <see langword="false"/> です。
    /// </summary>
    internal bool TryGetRow(IList rows, int index, out object row)
    {
        int offset = index - windowFirstIndex;
        if (ReferenceEquals(rows, windowSource) && offset >= 0 && offset < window.Length)
        {
            row = window[offset];
            return true;
        }
        row = null;
        return false;
    }

    private static bool TryResolveRange(IList rows, int firstIndex, int count, out int startIndex, out int endIndex)
    {
        startIndex = Math.Max(0, firstIndex);
        endIndex = 0;
        if (rows == null || rows.Count == 0 || count <= 0)
        {
            return false;
        }
        endIndex = Math.Min(rows.Count, startIndex + count);
        return endIndex > startIndex;
    }

    private object AcquireRow(object row)
    {
        if (row == null)
        {
            return null;
        }
        if (retainedRows.TryGetValue(row, out int referenceCount))
        {
            retainedRows[row] = referenceCount + 1;
            return row;
        }
        if (row is INotifyPropertyChanged notifyingRow)
        {
            PropertyChangedEventManager.AddHandler(notifyingRow, RowPropertyChanged, string.Empty);
        }
        retainedRows[row] = 1;
        return row;
    }

    private void ReleaseReference(object row)
    {
        if (row == null)
        {
            return;
        }
        int referenceCount = retainedRows[row] - 1;
        if (referenceCount > 0)
        {
            retainedRows[row] = referenceCount;
            return;
        }
        ReleaseRow(row);
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
        window = [];
        windowFirstIndex = 0;
        windowSource = null;
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
