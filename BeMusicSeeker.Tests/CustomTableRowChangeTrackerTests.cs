using System;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableRowChangeTrackerTests
{
    [TestMethod]
    public void ReplaceVisibleRows_OnlyTracksVisibleRows()
    {
        int changedCount = 0;
        TestRow row0 = new TestRow();
        TestRow row1 = new TestRow();
        TestRow row2 = new TestRow();
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });

        tracker.ReplaceVisibleRows(new object[] { row0, row1, row2 }, 1, 1);
        row0.RaiseChanged();
        row1.RaiseChanged();
        row2.RaiseChanged();

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual(1, tracker.SubscribedRowCount);
    }

    [TestMethod]
    public void ReplaceVisibleRows_ScrollsSubscriptions()
    {
        int changedCount = 0;
        TestRow row1 = new TestRow();
        TestRow row2 = new TestRow();
        TestRow row3 = new TestRow();
        object[] rows = { row1, row2, row3 };
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });

        tracker.ReplaceVisibleRows(rows, 0, 2);
        tracker.ReplaceVisibleRows(rows, 1, 2);

        row1.RaiseChanged();
        row2.RaiseChanged();
        row3.RaiseChanged();

        Assert.AreEqual(2, changedCount);
        Assert.AreEqual(2, tracker.SubscribedRowCount);
    }

    [TestMethod]
    public void ReplaceVisibleRows_RebuildsSubscriptionsAndIgnoresNonNotifyRows()
    {
        int changedCount = 0;
        TestRow oldRow = new TestRow();
        TestRow newRow = new TestRow();
        object plainRow = new object();
        object[] rows = { newRow, plainRow };
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        tracker.ReplaceVisibleRows(new object[] { oldRow }, 0, 1);

        tracker.ReplaceVisibleRows(rows, 0, 2);
        oldRow.RaiseChanged();
        newRow.RaiseChanged();

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual(1, tracker.SubscribedRowCount);
    }

    [TestMethod]
    public void ReplaceVisibleRows_TracksDuplicateRowReferencesWithReferenceCount()
    {
        int changedCount = 0;
        TestRow duplicatedRow = new TestRow();
        TestRow otherRow = new TestRow();
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });

        tracker.ReplaceVisibleRows(new object[] { duplicatedRow, duplicatedRow, otherRow }, 0, 2);
        tracker.ReplaceVisibleRows(new object[] { duplicatedRow, duplicatedRow, otherRow }, 1, 2);

        duplicatedRow.RaiseChanged();
        otherRow.RaiseChanged();

        Assert.AreEqual(2, changedCount);
        Assert.AreEqual(2, tracker.SubscribedRowCount);
    }

    [TestMethod]
    public void RedrawScheduler_CoalescesRequestsUntilDispatcherRuns()
    {
        RunOnSta(delegate
        {
            int redrawCount = 0;
            CustomTableRedrawScheduler scheduler = new CustomTableRedrawScheduler(
                Dispatcher.CurrentDispatcher,
                delegate { redrawCount++; });

            scheduler.Request();
            scheduler.Request();
            scheduler.Request();

            Assert.AreEqual(1, scheduler.ScheduledCount);
            Assert.AreEqual(0, redrawCount);

            DrainDispatcher();

            Assert.AreEqual(1, redrawCount);

            scheduler.Request();
            DrainDispatcher();

            Assert.AreEqual(2, scheduler.ScheduledCount);
            Assert.AreEqual(2, redrawCount);
        });
    }

    [TestMethod]
    public void RedrawScheduler_BackgroundRequestRunsActionOnDispatcherThread()
    {
        RunOnSta(delegate
        {
            int dispatcherThreadId = Thread.CurrentThread.ManagedThreadId;
            int actionThreadId = -1;
            CustomTableRedrawScheduler scheduler = new CustomTableRedrawScheduler(
                Dispatcher.CurrentDispatcher,
                delegate { actionThreadId = Thread.CurrentThread.ManagedThreadId; });

            Thread worker = new Thread((ThreadStart)delegate
            {
                scheduler.Request();
                scheduler.Request();
            });
            worker.Start();
            worker.Join();

            Assert.AreEqual(-1, actionThreadId);

            DrainDispatcher();

            Assert.AreEqual(dispatcherThreadId, actionThreadId);
            Assert.AreEqual(1, scheduler.ScheduledCount);
        });
    }

    [TestMethod]
    public void RowInvalidationQueue_CoalescesDuplicateRowsUntilDrain()
    {
        CustomTableRowInvalidationQueue queue = new CustomTableRowInvalidationQueue();
        object row = new object();
        object otherRow = new object();

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

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
        {
            frame.Continue = false;
        });
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception exception = null!;
        Thread thread = new Thread(delegate()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed class TestRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        internal void RaiseChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RaiseChanged)));
        }
    }
}
