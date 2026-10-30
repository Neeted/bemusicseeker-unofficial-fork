using System;
using System.Collections.Generic;
using System.Threading;

using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    private long displayTargetCatalogRefreshRequestedRevision;

    private long displayTargetCatalogRefreshCompletedRevision;

    private long displayTargetCatalogRefreshScheduled;

    private int displayTargetCatalogRefreshSelectionQueued;

    private int displayTargetCatalogRefreshActiveCount;

    private Func<bool> isDisplayTargetCatalogRefreshShutdownRequested;

    private Func<IReadOnlyList<BMSTable>> snapshotDisplayTargetCatalogTables;

    private Action<Action> scheduleDisplayTargetCatalogRefresh;

    internal void ConfigureDisplayTargetCatalogRefresh(
        Func<bool> isShutdownRequested,
        Func<IReadOnlyList<BMSTable>> snapshotTables,
        Action<Action> scheduleRefresh)
    {
        isDisplayTargetCatalogRefreshShutdownRequested = isShutdownRequested
            ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        snapshotDisplayTargetCatalogTables = snapshotTables
            ?? throw new ArgumentNullException(nameof(snapshotTables));
        scheduleDisplayTargetCatalogRefresh = scheduleRefresh
            ?? throw new ArgumentNullException(nameof(scheduleRefresh));
    }

    void ISettingsDialogPlayHistoryPort.RefreshDisplayTargetSetsFromSettings(
        string serializedDisplayTargetSets,
        bool queueRefreshWhenSelectionChanges, string previousName, string confirmedName)
        => RefreshDisplayTargetSetsFromSettings(serializedDisplayTargetSets, queueRefreshWhenSelectionChanges,
            previousName, confirmedName);

    internal void RefreshDisplayTargetCatalog(bool queueRefreshWhenSelectionChanges = true)
    {
        EnsureDisplayTargetCatalogRefreshConfigured();
        IReadOnlyList<BMSTable> tables = snapshotDisplayTargetCatalogTables();
        ReplaceDisplayTargetCatalog(tables, queueRefreshWhenSelectionChanges);
    }

    /// <summary>保存成功のプリセット定義を採用し、旧名に対応する希望識別だけを同じモードの確定名へ移します。</summary>
    internal void RefreshDisplayTargetSetsFromSettings(
        string serializedDisplayTargetSets,
        bool queueRefreshWhenSelectionChanges,
        string previousName = null,
        string confirmedName = null)
    {
        EnsureDisplayTargetCatalogRefreshConfigured();
        IReadOnlyList<BMSTable> tables = snapshotDisplayTargetCatalogTables();
        if (!PlayHistoryDisplayTargetSetStore.TryDeserialize(
            serializedDisplayTargetSets,
            out IReadOnlyList<PlayHistoryDisplayTargetSet> targetSets))
        {
            throw new InvalidOperationException("Play-history display target settings JSON is invalid.");
        }
        if (previousName != null && confirmedName != null)
        {
            string oldKey = previousName.Trim().ToUpperInvariant();
            string newKey = confirmedName.Trim().ToUpperInvariant();
            foreach (string prefix in new[] { "set:", "set-folder:" })
            {
                if (string.Equals(preferredDisplayTargetIdentity, prefix + oldKey, StringComparison.Ordinal))
                {
                    preferredDisplayTargetIdentity = prefix + newKey;
                    persistDisplayTargetIdentity?.Invoke(preferredDisplayTargetIdentity);
                    break;
                }
            }
        }
        ReplaceDisplayTargetSets(targetSets, tables, queueRefreshWhenSelectionChanges);
    }

    internal void QueueDisplayTargetCatalogRefresh(bool queueRefreshWhenSelectionChanges = true)
    {
        EnsureDisplayTargetCatalogRefreshConfigured();
        if (isDisplayTargetCatalogRefreshShutdownRequested())
        {
            return;
        }
        if (queueRefreshWhenSelectionChanges)
        {
            Interlocked.Exchange(ref displayTargetCatalogRefreshSelectionQueued, 1);
        }
        Interlocked.Increment(ref displayTargetCatalogRefreshRequestedRevision);
        if (Interlocked.CompareExchange(ref displayTargetCatalogRefreshScheduled, 1L, 0L) != 0L)
        {
            return;
        }

        void Refresh()
        {
            Interlocked.Increment(ref displayTargetCatalogRefreshActiveCount);
            try
            {
                while (true)
                {
                    if (isDisplayTargetCatalogRefreshShutdownRequested())
                    {
                        long requestedRevision = Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision);
                        Interlocked.Exchange(ref displayTargetCatalogRefreshCompletedRevision, requestedRevision);
                        return;
                    }

                    long refreshRevision = Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision);
                    bool refreshSelectionWhenChanged = Interlocked.Exchange(ref displayTargetCatalogRefreshSelectionQueued, 0) == 1;
                    ReplaceDisplayTargetCatalog(
                        snapshotDisplayTargetCatalogTables(),
                        refreshSelectionWhenChanged);
                    Interlocked.Exchange(ref displayTargetCatalogRefreshCompletedRevision, refreshRevision);
                    if (refreshRevision == Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision))
                    {
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref displayTargetCatalogRefreshActiveCount);
                Interlocked.Exchange(ref displayTargetCatalogRefreshScheduled, 0L);
                if (Interlocked.Read(ref displayTargetCatalogRefreshCompletedRevision)
                    != Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision)
                    && Interlocked.CompareExchange(ref displayTargetCatalogRefreshScheduled, 1L, 0L) == 0L)
                {
                    ScheduleDisplayTargetCatalogRefresh(Refresh);
                }
            }
        }

        ScheduleDisplayTargetCatalogRefresh(Refresh);
    }

    internal void CancelDisplayTargetCatalogRefreshesForShutdown()
    {
        Interlocked.Exchange(
            ref displayTargetCatalogRefreshRequestedRevision,
            Interlocked.Read(ref displayTargetCatalogRefreshCompletedRevision));
        Interlocked.Exchange(ref displayTargetCatalogRefreshSelectionQueued, 0);
        Interlocked.Exchange(ref displayTargetCatalogRefreshScheduled, 0L);
    }

    internal void RejectDisplayTargetCatalogRefreshScheduling()
    {
        Interlocked.Exchange(
            ref displayTargetCatalogRefreshCompletedRevision,
            Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision));
        Interlocked.Exchange(ref displayTargetCatalogRefreshScheduled, 0L);
        Interlocked.Exchange(ref displayTargetCatalogRefreshSelectionQueued, 0);
    }

    internal bool IsDisplayTargetCatalogRefreshIdle =>
        Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision)
            <= Interlocked.Read(ref displayTargetCatalogRefreshCompletedRevision)
        && Interlocked.Read(ref displayTargetCatalogRefreshScheduled) == 0L
        && Volatile.Read(ref displayTargetCatalogRefreshActiveCount) == 0;

    internal string DescribeDisplayTargetCatalogRefresh()
    {
        return "displayTargetsRequestedRevision="
            + Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision)
            + " displayTargetsCompletedRevision="
            + Interlocked.Read(ref displayTargetCatalogRefreshCompletedRevision)
            + " displayTargetsScheduled="
            + Interlocked.Read(ref displayTargetCatalogRefreshScheduled)
            + " displayTargetsActiveCount="
            + Volatile.Read(ref displayTargetCatalogRefreshActiveCount);
    }

    private void EnsureDisplayTargetCatalogRefreshConfigured()
    {
        if (isDisplayTargetCatalogRefreshShutdownRequested == null
            || snapshotDisplayTargetCatalogTables == null
            || scheduleDisplayTargetCatalogRefresh == null)
        {
            throw new InvalidOperationException("Display-target catalog refresh must be configured before it is queued.");
        }
    }

    private void ScheduleDisplayTargetCatalogRefresh(Action refresh)
    {
        try
        {
            scheduleDisplayTargetCatalogRefresh(refresh);
        }
        catch
        {
            Interlocked.Exchange(
                ref displayTargetCatalogRefreshCompletedRevision,
                Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision));
            Interlocked.Exchange(ref displayTargetCatalogRefreshScheduled, 0L);
            throw;
        }
    }
}
