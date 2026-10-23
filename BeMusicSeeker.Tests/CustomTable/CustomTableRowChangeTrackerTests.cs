using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableRowChangeTrackerTests
{
    /// <summary>三行の中央一行だけを可視にした場合はその行の変更だけ通知することを確認し、画面外の全行を購読して再描画する誤りを検出します。</summary>
    [TestMethod]
    public void ReplaceVisibleRows_OnlyTracksVisibleRows()
    {
        int changedCount = 0;
        var row0 = new TestRow();
        var row1 = new TestRow();
        var row2 = new TestRow();
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        try
        {
            tracker.ReplaceVisibleRows(new object[] { row0, row1, row2 }, 1, 1);
            row0.RaiseChanged();
            row1.RaiseChanged();
            row2.RaiseChanged();

            Assert.AreEqual(1, changedCount);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>二行ずつの可視範囲を移すと外れた行を解除して新しい二行だけ通知することを確認し、スクロール後の購読残留や新規行の購読漏れを検出します。</summary>
    [TestMethod]
    public void ReplaceVisibleRows_ScrollsSubscriptions()
    {
        int changedCount = 0;
        var row1 = new TestRow();
        var row2 = new TestRow();
        var row3 = new TestRow();
        object[] rows = [row1, row2, row3];
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        try
        {
            tracker.ReplaceVisibleRows(rows, 0, 2);
            tracker.ReplaceVisibleRows(rows, 1, 2);

            row1.RaiseChanged();
            row2.RaiseChanged();
            row3.RaiseChanged();

            Assert.AreEqual(2, changedCount);
            Assert.AreEqual(2, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>行集合交換で旧行を解除し通知可能な新行だけ購読することを確認し、旧集合の通知や通知不能な行を購読数に混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void ReplaceVisibleRows_RebuildsSubscriptionsAndIgnoresNonNotifyRows()
    {
        int changedCount = 0;
        var oldRow = new TestRow();
        var newRow = new TestRow();
        object plainRow = new();
        object[] rows = [newRow, plainRow];
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        try
        {
            tracker.ReplaceVisibleRows(new object[] { oldRow }, 0, 1);

            tracker.ReplaceVisibleRows(rows, 0, 2);
            oldRow.RaiseChanged();
            newRow.RaiseChanged();

            Assert.AreEqual(1, changedCount);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>重複参照の片方だけが離れても購読と値寿命を保ち、最後の離脱で一回だけ解除することを確認します。</summary>
    [TestMethod]
    public void ReplaceVisibleRows_TracksDuplicateRowReferencesUntilLastDeparture()
    {
        int changedCount = 0;
        var duplicatedRow = new TestRow();
        var otherRow = new TestRow();
        object[] rows = [duplicatedRow, duplicatedRow, otherRow];
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; }, released.Add);
        try
        {
            tracker.ReplaceVisibleRows(rows, 0, 2);
            tracker.ReplaceVisibleRows(rows, 1, 2);
            duplicatedRow.RaiseChanged();
            otherRow.RaiseChanged();
            Assert.AreEqual(2, changedCount);
            Assert.AreEqual(2, tracker.SubscribedRowCount);
            Assert.AreEqual(0, released.Count);
            tracker.ReplaceVisibleRows(rows, 2, 1);
            duplicatedRow.RaiseChanged();
            otherRow.RaiseChanged();
            Assert.AreEqual(3, changedCount);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
            CollectionAssert.AreEqual(new object[] { duplicatedRow }, released);
        }
        finally { tracker.DetachAllRows(); }
        CollectionAssert.AreEqual(new object[] { duplicatedRow, otherRow }, released);
    }

    /// <summary>Dispatcher実行前の三要求を一回へまとめ、完了後の次要求は新たに実行することを確認し、過剰な再描画や次回要求の取り落としを検出します。</summary>
    [TestMethod]
    public void RedrawScheduler_CoalescesRequestsUntilDispatcherRuns()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            int redrawCount = 0;
            var redrawApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = new CustomTableRedrawScheduler(
                Dispatcher.CurrentDispatcher,
                delegate { redrawCount++; redrawApplied.TrySetResult(); });

            scheduler.Request();
            scheduler.Request();
            scheduler.Request();

            Assert.AreEqual(1, scheduler.ScheduledCount);
            Assert.AreEqual(0, redrawCount);

            TestUiDispatcherHost.AwaitTaskOnDispatcher(redrawApplied.Task, "redraw-first-action");

            Assert.AreEqual(1, redrawCount);

            redrawApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.Request();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(redrawApplied.Task, "redraw-second-action");

            Assert.AreEqual(2, scheduler.ScheduledCount);
            Assert.AreEqual(2, redrawCount);
        });
    }

    /// <summary>workerからの複数再描画要求はまとめてUIスレッドへ配送することを確認し、workerで描画を行う誤りや二重配送を検出します。</summary>
    [TestMethod]
    public void RedrawScheduler_BackgroundRequestRunsActionOnDispatcherThread()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            int dispatcherThreadId = Thread.CurrentThread.ManagedThreadId;
            int actionThreadId = -1;
            var redrawApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = new CustomTableRedrawScheduler(
                Dispatcher.CurrentDispatcher,
                delegate { actionThreadId = Thread.CurrentThread.ManagedThreadId; redrawApplied.TrySetResult(); });

            var worker = new Thread((ThreadStart)delegate
            {
                scheduler.Request();
                scheduler.Request();
            });
            worker.Start();
            worker.Join();

            Assert.AreEqual(-1, actionThreadId);

            TestUiDispatcherHost.AwaitTaskOnDispatcher(redrawApplied.Task, "redraw-worker-action");

            Assert.AreEqual(dispatcherThreadId, actionThreadId);
            Assert.AreEqual(1, scheduler.ScheduledCount);
        });
    }

    /// <summary>同じ行の失効要求はDrainまで重複除去し別行を保持して取出し後は空にすることを確認し、重複失効や取出し済みの要求を再利用する誤りを検出します。</summary>
    [TestMethod]
    public void RowInvalidationQueue_CoalescesDuplicateRowsUntilDrain()
    {
        var queue = new CustomTableRowInvalidationQueue();
        object row = new();
        object otherRow = new();

        Assert.IsTrue(queue.Enqueue(row));
        Assert.IsFalse(queue.Enqueue(row));
        Assert.IsTrue(queue.Enqueue(otherRow));

        object[] rows = queue.Drain();

        Assert.AreEqual(2, rows.Length);
        CollectionAssert.Contains(rows, row);
        CollectionAssert.Contains(rows, otherRow);
        Assert.AreEqual(0, queue.Count);
        Assert.AreEqual(0, queue.Drain().Length);
    }

    private sealed class TestRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged = delegate { };

        internal void RaiseChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RaiseChanged)));
        }
    }
}
