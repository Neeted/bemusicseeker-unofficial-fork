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

    private DroppedInstallBatchRequest activeBatch;

    private CancellationTokenSource activeCancellationTokenSource;

    private int activeCompletedPathCount;

    public bool IsIdle
    {
        get
        {
            lock (syncRoot)
            {
                return !workerRunning && activeBatch == null && pendingBatches.Count == 0;
            }
        }
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        var request = new DroppedInstallBatchRequest(paths);
        if (request.PathCount == 0)
        {
            return;
        }
        bool startWorker = false;
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            pendingBatches.Enqueue(request);
            if (!workerRunning)
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
    }

    public void CancelAll()
    {
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            if (activeBatch == null && pendingBatches.Count == 0 && !workerRunning)
            {
                return;
            }
            cancelRequested = true;
            pendingBatches.Clear();
            activeCancellationTokenSource?.Cancel();
            snapshot = CaptureStatusSnapshotUnsafe();
        }
        statusChanged(snapshot);
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        DateTime deadlineUtc = DateTime.UtcNow + timeout;
        while (true)
        {
            if (IsIdle)
            {
                return true;
            }
            if (DateTime.UtcNow >= deadlineUtc)
            {
                return false;
            }
            await Task.Delay(100).ConfigureAwait(false);
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
            lock (syncRoot)
            {
                if (pendingBatches.Count == 0)
                {
                    workerRunning = false;
                    cancelRequested = false;
                    activeBatch = null;
                    activeCompletedPathCount = 0;
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    snapshot = CaptureStatusSnapshotUnsafe();
                    shouldExitImmediately = true;
                }
                else
                {
                    batch = pendingBatches.Dequeue();
                    activeBatch = batch;
                    activeCompletedPathCount = 0;
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = new CancellationTokenSource();
                    cancellationTokenSource = activeCancellationTokenSource;
                    snapshot = CaptureStatusSnapshotUnsafe();
                }
            }
            statusChanged(snapshot);
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
                lock (syncRoot)
                {
                    activeBatch = null;
                    activeCompletedPathCount = 0;
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    if (cancelRequested)
                    {
                        pendingBatches.Clear();
                    }
                    shouldExitAfterFinally = pendingBatches.Count == 0;
                    if (shouldExitAfterFinally)
                    {
                        workerRunning = false;
                        cancelRequested = false;
                    }
                    snapshot = CaptureStatusSnapshotUnsafe();
                }
                statusChanged(snapshot);
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
            IsActive = displayedBatch != null,
            CanCancel = displayedBatch != null && !cancelRequested,
            IsCancellationRequested = cancelRequested,
            PendingBatchCount = pendingCount,
            TotalPathCount = displayedBatch?.PathCount ?? 0,
            CompletedPathCount = (activeBatch != null) ? activeCompletedPathCount : 0,
            CurrentDisplayName = displayedBatch?.DisplayName ?? string.Empty
        };
    }
}
