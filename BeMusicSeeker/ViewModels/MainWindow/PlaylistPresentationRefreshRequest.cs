using System;
using System.Collections.ObjectModel;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal enum PlaylistPresentationRefreshKind
{
    SummaryData,
    SummaryPresentation,
    Tree,
    HydrationCompleted
}

internal sealed class PlaylistPresentationRefreshRequestedEventArgs : EventArgs
{
    internal PlaylistPresentationRefreshRequestedEventArgs(
        PlaylistPresentationRefreshKind kind,
        string reason,
        bool rebuildAsync = true,
        int hydrationVersion = 0,
        BMSPlaylist hydrationSourceStore = null,
        ObservableCollection<BMSTable> hydrationSourceTables = null,
        long hydrationNotificationGeneration = 0L,
        PlaylistHydrationCompletionReceipt hydrationCompletionReceipt = null)
    {
        Kind = kind;
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        RebuildAsync = rebuildAsync;
        HydrationVersion = hydrationVersion;
        HydrationSourceStore = hydrationSourceStore;
        HydrationSourceTables = hydrationSourceTables;
        HydrationNotificationGeneration = hydrationNotificationGeneration;
        HydrationCompletionReceipt = hydrationCompletionReceipt;
    }

    internal PlaylistPresentationRefreshKind Kind { get; }

    internal string Reason { get; }

    internal bool RebuildAsync { get; }

    internal int HydrationVersion { get; }

    internal BMSPlaylist HydrationSourceStore { get; }

    internal ObservableCollection<BMSTable> HydrationSourceTables { get; }

    internal long HydrationNotificationGeneration { get; }

    internal PlaylistHydrationCompletionReceipt HydrationCompletionReceipt { get; }
}

internal sealed class PlaylistHydrationCompletionReceipt
{
    private readonly object synchronizationRoot = new();

    internal PlaylistHydrationCompletionReceipt(bool requestAlreadyPublished = false)
    {
        state = requestAlreadyPublished ? 3 : 1;
    }

    private int state;

    private int activeExecutions;

    internal bool TryBegin()
    {
        return Interlocked.CompareExchange(ref state, 2, 1) == 1
            || Interlocked.CompareExchange(ref state, 4, 3) == 3;
    }

    internal bool MarkRequestPublished()
    {
        return Interlocked.CompareExchange(ref state, 3, 2) == 2;
    }

    internal void Invalidate()
    {
        lock (synchronizationRoot)
        {
            Interlocked.Exchange(ref state, 0);
            while (activeExecutions != 0)
            {
                Monitor.Wait(synchronizationRoot);
            }
        }
    }

    internal bool IsBegun
        => Volatile.Read(ref state) == 2 || Volatile.Read(ref state) == 4;

    internal bool IsRequestPublished
    {
        get
        {
            int currentState = Volatile.Read(ref state);
            return currentState == 3 || currentState == 4;
        }
    }

    internal bool ExecuteIfCurrent(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        lock (synchronizationRoot)
        {
            int currentState = Volatile.Read(ref state);
            if (currentState != 2 && currentState != 4)
            {
                return false;
            }
            activeExecutions++;
        }
        try
        {
            action();
            return true;
        }
        finally
        {
            lock (synchronizationRoot)
            {
                activeExecutions--;
                if (activeExecutions == 0)
                {
                    Monitor.PulseAll(synchronizationRoot);
                }
            }
        }
    }
}
