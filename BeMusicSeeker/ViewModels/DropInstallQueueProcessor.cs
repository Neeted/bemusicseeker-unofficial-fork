using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal sealed class DropInstallQueueProcessor(Action<DroppedInstallBatchRequest, CancellationToken> processBatch, Action<DropInstallQueueStatusSnapshot> statusChanged, Action<Exception> batchFailed = null)
{
    private readonly object syncRoot = new();

    private readonly Queue<DroppedInstallBatchRequest> pendingBatches = new();

    private readonly Action<DroppedInstallBatchRequest, CancellationToken> processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));

    private readonly Action<DropInstallQueueStatusSnapshot> statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));

    private readonly Action<Exception> batchFailed = batchFailed;

    private bool workerRunning;

    private bool cancelRequested;

    private int detachedAbandonmentCount;

    private DroppedInstallBatchRequest activeBatch;

    private CancellationTokenSource activeCancellationTokenSource;

    private int activeCompletedPathCount;

    private bool activeCurrentWorkInProgress;

    private int activeCurrentWorkIndex;

    private int activeCurrentWorkTotal;

    private string activeCurrentWorkDisplayName = string.Empty;

    private long statusSequence;

    public bool IsIdle
    {
        get
        {
            lock (syncRoot)
            {
                return !workerRunning
                    && !cancelRequested
                    && activeBatch == null
                    && pendingBatches.Count == 0
                    && detachedAbandonmentCount == 0;
            }
        }
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        var request = new DroppedInstallBatchRequest(paths);
        Enqueue(request);
    }

    /// <summary>
    /// Transfers an already acquired request to this FIFO queue.
    /// </summary>
    /// <returns><see langword="true"/> when the queue accepted ownership of the request.</returns>
    internal bool Enqueue(DroppedInstallBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PathCount == 0)
        {
            return false;
        }
        bool startWorker = false;
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            pendingBatches.Enqueue(request);
            if (!workerRunning && !cancelRequested)
            {
                workerRunning = true;
                startWorker = true;
            }
            snapshot = CaptureStatusSnapshotUnsafe();
        }
        statusChanged(snapshot);
        if (startWorker)
        {
            Task.Factory.StartNew(ProcessLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Logging("DropInstallQueueProcessor");
        }
        return true;
    }

    public void CancelAll()
    {
        DroppedInstallBatchRequest[] abandonedBatches;
        DroppedInstallBatchRequest activeRequest;
        CancellationTokenSource cancellationTokenSource;
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            if (activeBatch == null
                && pendingBatches.Count == 0
                && !workerRunning)
            {
                return;
            }
            cancelRequested = true;
            snapshot = CaptureStatusSnapshotUnsafe();
            abandonedBatches = DetachPendingBatchesUnsafe();
            activeRequest = activeBatch;
            cancellationTokenSource = activeCancellationTokenSource;
            activeRequest?.TryReserveAbandonmentBeforeInstallerHandoff();
        }
        TryCancel(cancellationTokenSource);
        if (snapshot.IsActive)
        {
            statusChanged(snapshot);
        }
        DropInstallQueueStatusSnapshot terminalSnapshot = CompleteDetachedAbandonment(abandonedBatches);
        if (terminalSnapshot != null)
        {
            statusChanged(terminalSnapshot);
        }
    }

    public void ReportActiveBatchProgress(int completedPathCount)
    {
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            if (activeBatch == null)
            {
                return;
            }
            activeCompletedPathCount = Math.Max(0, Math.Min(completedPathCount, activeBatch.PathCount));
            if (activeCurrentWorkInProgress)
            {
                ClearActiveCurrentWorkUnsafe();
            }
            snapshot = CaptureStatusSnapshotUnsafe();
        }
        statusChanged(snapshot);
    }

    public void ReportActiveBatchCurrentWork(int currentPathIndex, int totalPathCount, string displayName)
    {
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            if (activeBatch == null)
            {
                return;
            }
            int normalizedTotalPathCount = Math.Max(0, totalPathCount);
            activeCurrentWorkInProgress = currentPathIndex > 0 && normalizedTotalPathCount > 0;
            activeCurrentWorkIndex = Math.Max(0, Math.Min(currentPathIndex, normalizedTotalPathCount));
            activeCurrentWorkTotal = normalizedTotalPathCount;
            activeCurrentWorkDisplayName = displayName ?? string.Empty;
            snapshot = CaptureStatusSnapshotUnsafe();
        }
        statusChanged(snapshot);
    }

    private void ProcessLoop()
    {
        while (true)
        {
            DroppedInstallBatchRequest batch = null;
            CancellationTokenSource cancellationTokenSource = null;
            DropInstallQueueStatusSnapshot snapshot;
            bool shouldExitImmediately = false;
            DroppedInstallBatchRequest[] cancelledBeforeStart = [];
            lock (syncRoot)
            {
                if (cancelRequested && pendingBatches.Count > 0)
                {
                    cancelledBeforeStart = DetachPendingBatchesUnsafe();
                    snapshot = null;
                }
                else if (pendingBatches.Count == 0)
                {
                    workerRunning = false;
                    activeBatch = null;
                    activeCompletedPathCount = 0;
                    ClearActiveCurrentWorkUnsafe();
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    if (cancelRequested && detachedAbandonmentCount > 0)
                    {
                        snapshot = null;
                    }
                    else
                    {
                        cancelRequested = false;
                        snapshot = CaptureStatusSnapshotUnsafe();
                    }
                    shouldExitImmediately = true;
                }
                else
                {
                    batch = pendingBatches.Dequeue();
                    activeBatch = batch;
                    activeCompletedPathCount = 0;
                    ClearActiveCurrentWorkUnsafe();
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = new CancellationTokenSource();
                    cancellationTokenSource = activeCancellationTokenSource;
                    snapshot = CaptureStatusSnapshotUnsafe();
                }
            }
            if (cancelledBeforeStart.Length > 0)
            {
                DropInstallQueueStatusSnapshot cancellationTerminalSnapshot =
                    CompleteDetachedAbandonment(cancelledBeforeStart);
                if (cancellationTerminalSnapshot != null)
                {
                    statusChanged(cancellationTerminalSnapshot);
                }
                continue;
            }
            if (snapshot != null)
            {
                statusChanged(snapshot);
            }
            if (shouldExitImmediately)
            {
                return;
            }
            bool shouldExitAfterFinally = false;
            try
            {
                processBatch(batch, cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                batchFailed?.Invoke(ex);
            }
            finally
            {
                DroppedInstallBatchRequest[] abandonedBatches;
                lock (syncRoot)
                {
                    activeBatch = null;
                    activeCompletedPathCount = 0;
                    ClearActiveCurrentWorkUnsafe();
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    if (cancelRequested)
                    {
                        abandonedBatches = DetachPendingBatchesUnsafe();
                    }
                    else
                    {
                        abandonedBatches = [];
                    }
                }
                batch.TryAbandonUnconsumedSources();
                DropInstallQueueStatusSnapshot detachedTerminalSnapshot =
                    CompleteDetachedAbandonment(abandonedBatches);
                if (detachedTerminalSnapshot != null)
                {
                    statusChanged(detachedTerminalSnapshot);
                }

                while (true)
                {
                    DroppedInstallBatchRequest[] batchesEnqueuedDuringCancellation;
                    lock (syncRoot)
                    {
                        if (cancelRequested && pendingBatches.Count > 0)
                        {
                            batchesEnqueuedDuringCancellation = DetachPendingBatchesUnsafe();
                            snapshot = null;
                        }
                        else
                        {
                            batchesEnqueuedDuringCancellation = [];
                            shouldExitAfterFinally = pendingBatches.Count == 0;
                            if (shouldExitAfterFinally)
                            {
                                workerRunning = false;
                                if (detachedAbandonmentCount == 0)
                                {
                                    cancelRequested = false;
                                    snapshot = CaptureStatusSnapshotUnsafe();
                                }
                                else
                                {
                                    snapshot = null;
                                }
                            }
                            else
                            {
                                snapshot = CaptureStatusSnapshotUnsafe();
                            }
                        }
                    }
                    DropInstallQueueStatusSnapshot cancellationTerminalSnapshot =
                        CompleteDetachedAbandonment(batchesEnqueuedDuringCancellation);
                    if (cancellationTerminalSnapshot != null)
                    {
                        statusChanged(cancellationTerminalSnapshot);
                    }
                    if (batchesEnqueuedDuringCancellation.Length == 0)
                    {
                        break;
                    }
                }
                if (snapshot != null)
                {
                    statusChanged(snapshot);
                }
            }
            if (shouldExitAfterFinally)
            {
                return;
            }
        }
    }

    private DropInstallQueueStatusSnapshot CaptureStatusSnapshotUnsafe()
    {
        DroppedInstallBatchRequest displayedBatch = activeBatch;
        if (displayedBatch == null && pendingBatches.Count > 0)
        {
            displayedBatch = pendingBatches.Peek();
        }
        int pendingCount = (activeBatch != null) ? pendingBatches.Count : Math.Max(0, pendingBatches.Count - 1);
        return new DropInstallQueueStatusSnapshot
        {
            Sequence = ++statusSequence,
            IsActive = displayedBatch != null,
            CanCancel = displayedBatch != null && !cancelRequested,
            IsCancellationRequested = cancelRequested,
            PendingBatchCount = pendingCount,
            TotalPathCount = displayedBatch?.PathCount ?? 0,
            CompletedPathCount = (activeBatch != null) ? activeCompletedPathCount : 0,
            CurrentDisplayName = displayedBatch?.DisplayName ?? string.Empty,
            IsCurrentWorkInProgress = activeBatch != null && activeCurrentWorkInProgress,
            CurrentWorkIndex = (activeBatch != null) ? activeCurrentWorkIndex : 0,
            CurrentWorkTotal = (activeBatch != null) ? activeCurrentWorkTotal : 0,
            CurrentWorkDisplayName = (activeBatch != null) ? activeCurrentWorkDisplayName : string.Empty
        };
    }

    private void ClearActiveCurrentWorkUnsafe()
    {
        activeCurrentWorkInProgress = false;
        activeCurrentWorkIndex = 0;
        activeCurrentWorkTotal = 0;
        activeCurrentWorkDisplayName = string.Empty;
    }

    private static void AbandonRequests(IEnumerable<DroppedInstallBatchRequest> requests)
    {
        foreach (DroppedInstallBatchRequest request in requests ?? [])
        {
            request?.TryAbandonUnconsumedSources();
        }
    }

    private DroppedInstallBatchRequest[] DetachPendingBatchesUnsafe()
    {
        DroppedInstallBatchRequest[] detached = [.. pendingBatches];
        pendingBatches.Clear();
        detachedAbandonmentCount += detached.Length;
        return detached;
    }

    private DropInstallQueueStatusSnapshot CompleteDetachedAbandonment(
        DroppedInstallBatchRequest[] initiallyDetached)
    {
        DroppedInstallBatchRequest[] detached = initiallyDetached ?? [];
        while (true)
        {
            DropInstallQueueStatusSnapshot terminalSnapshot = null;
            try
            {
                AbandonRequests(detached);
            }
            finally
            {
                lock (syncRoot)
                {
                    detachedAbandonmentCount -= detached.Length;
                    if (cancelRequested && pendingBatches.Count > 0)
                    {
                        detached = DetachPendingBatchesUnsafe();
                    }
                    else
                    {
                        detached = [];
                        if (cancelRequested
                            && detachedAbandonmentCount == 0
                            && activeBatch == null
                            && !workerRunning)
                        {
                            cancelRequested = false;
                            terminalSnapshot = CaptureStatusSnapshotUnsafe();
                        }
                    }
                }
            }
            if (detached.Length == 0)
            {
                return terminalSnapshot;
            }
        }
    }

    private static void TryCancel(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker may complete between the lock snapshot and cancellation.
        }
    }
}
