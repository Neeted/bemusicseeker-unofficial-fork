using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    public void ReplaceRows_OnlyTracksCurrentRows()
    {
        int changedCount = 0;
        TestRow oldRow = new TestRow();
        TestRow newRow = new TestRow();
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });

        tracker.ReplaceRows(new object[] { oldRow });
        oldRow.RaiseChanged();
        tracker.ReplaceRows(new object[] { newRow });
        oldRow.RaiseChanged();
        newRow.RaiseChanged();

        Assert.AreEqual(2, changedCount);
        Assert.AreEqual(1, tracker.SubscribedRowCount);
    }

    [TestMethod]
    public void ApplyCollectionChanged_UpdatesSubscriptions()
    {
        int changedCount = 0;
        TestRow row1 = new TestRow();
        TestRow row2 = new TestRow();
        TestRow row3 = new TestRow();
        ObservableCollection<object> rows = new ObservableCollection<object> { row1 };
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        tracker.ReplaceRows(rows);

        rows.Add(row2);
        tracker.ApplyCollectionChanged(rows, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, row2, 1));
        rows[1] = row3;
        tracker.ApplyCollectionChanged(rows, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, row3, row2, 1));
        rows.RemoveAt(0);
        tracker.ApplyCollectionChanged(rows, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, row1, 0));

        row1.RaiseChanged();
        row2.RaiseChanged();
        row3.RaiseChanged();

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual(1, tracker.SubscribedRowCount);
    }

    [TestMethod]
    public void ApplyCollectionChanged_ResetRebuildsSubscriptionsAndIgnoresNonNotifyRows()
    {
        int changedCount = 0;
        TestRow oldRow = new TestRow();
        TestRow newRow = new TestRow();
        object plainRow = new object();
        ObservableCollection<object> rows = new ObservableCollection<object> { newRow, plainRow };
        CustomTableRowChangeTracker tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        tracker.ReplaceRows(new object[] { oldRow });

        tracker.ApplyCollectionChanged(rows, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        oldRow.RaiseChanged();
        newRow.RaiseChanged();

        Assert.AreEqual(1, changedCount);
        Assert.AreEqual(1, tracker.SubscribedRowCount);
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
