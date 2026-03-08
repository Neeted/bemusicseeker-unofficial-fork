using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal sealed class DropInstallQueueProcessor
{
    private readonly object syncRoot = new object();

    private readonly Queue<DroppedInstallBatchRequest> pendingBatches = new Queue<DroppedInstallBatchRequest>();

    private readonly Action<DroppedInstallBatchRequest, CancellationToken> processBatch;

    private readonly Action<DropInstallQueueStatusSnapshot> statusChanged;

    private readonly Action<Exception> batchFailed;

    private bool workerRunning;

    private bool cancelRequested;

    private DroppedInstallBatchRequest activeBatch;

    private CancellationTokenSource activeCancellationTokenSource;

    public DropInstallQueueProcessor(Action<DroppedInstallBatchRequest, CancellationToken> processBatch, Action<DropInstallQueueStatusSnapshot> statusChanged, Action<Exception> batchFailed = null)
    {
        this.processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));
        this.statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
        this.batchFailed = batchFailed;
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        DroppedInstallBatchRequest request = new DroppedInstallBatchRequest(paths);
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
            if (activeBatch == null)
            {
                workerRunning = false;
                cancelRequested = false;
            }
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
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    snapshot = CaptureStatusSnapshotUnsafe();
                    shouldExitImmediately = true;
                }
                else
                {
                    batch = pendingBatches.Dequeue();
                    activeBatch = batch;
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
            CurrentPathCount = displayedBatch?.PathCount ?? 0,
            CurrentDisplayName = displayedBatch?.DisplayName ?? string.Empty
        };
    }
}
