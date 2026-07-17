using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

internal sealed class PendingInstallEstimateQueueProcessor(
    Action<PendingInstallEstimateBatchRequest, CancellationToken> processBatch,
    Action<PendingInstallEstimateQueueStatusSnapshot> statusChanged,
    Action<Exception> batchFailed = null)
{
    private readonly object syncRoot = new();

    private readonly Queue<PendingInstallEstimateBatchRequest> pendingBatches = new();

    private readonly Action<PendingInstallEstimateBatchRequest, CancellationToken> processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));

    private readonly Action<PendingInstallEstimateQueueStatusSnapshot> statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));

    private readonly Action<Exception> batchFailed = batchFailed;

    private bool workerRunning;

    private bool cancelRequested;

    private PendingInstallEstimateBatchRequest activeBatch;

    private CancellationTokenSource activeCancellationTokenSource;

    private int activeCompletedPackageCount;

    private long statusSequence;

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

    public void Enqueue(PendingInstallEstimateBatchRequest request)
    {
        if (request == null || request.PackageCount == 0)
        {
            return;
        }
        bool startWorker = false;
        PendingInstallEstimateQueueStatusSnapshot snapshot;
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
            Task.Factory.StartNew(ProcessLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Logging("PendingInstallEstimateQueueProcessor");
        }
    }

    public void CancelAll()
    {
        PendingInstallEstimateQueueStatusSnapshot snapshot;
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

    public void ReportActiveBatchProgress(int completedPackageCount)
    {
        PendingInstallEstimateQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            if (activeBatch == null)
            {
                return;
            }
            activeCompletedPackageCount = Math.Max(0, Math.Min(completedPackageCount, activeBatch.PackageCount));
            snapshot = CaptureStatusSnapshotUnsafe();
        }
        statusChanged(snapshot);
    }

    public PendingInstallEstimateQueueStatusSnapshot GetStatusSnapshot()
    {
        lock (syncRoot)
        {
            return CaptureStatusSnapshotUnsafe();
        }
    }

    private void ProcessLoop()
    {
        while (true)
        {
            PendingInstallEstimateBatchRequest batch = null;
            CancellationTokenSource cancellationTokenSource = null;
            PendingInstallEstimateQueueStatusSnapshot snapshot;
            bool shouldExitImmediately = false;
            lock (syncRoot)
            {
                if (pendingBatches.Count == 0)
                {
                    workerRunning = false;
                    cancelRequested = false;
                    activeBatch = null;
                    activeCompletedPackageCount = 0;
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    snapshot = CaptureStatusSnapshotUnsafe();
                    shouldExitImmediately = true;
                }
                else
                {
                    batch = pendingBatches.Dequeue();
                    activeBatch = batch;
                    activeCompletedPackageCount = 0;
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
                    activeCompletedPackageCount = 0;
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

    private PendingInstallEstimateQueueStatusSnapshot CaptureStatusSnapshotUnsafe()
    {
        PendingInstallEstimateBatchRequest displayedBatch = activeBatch;
        if (displayedBatch == null && pendingBatches.Count > 0)
        {
            displayedBatch = pendingBatches.Peek();
        }
        int pendingCount = (activeBatch != null) ? pendingBatches.Count : Math.Max(0, pendingBatches.Count - 1);
        return new PendingInstallEstimateQueueStatusSnapshot
        {
            Sequence = ++statusSequence,
            IsActive = displayedBatch != null,
            Source = displayedBatch?.Source ?? PendingInstallEstimateBatchSource.StartupRestore,
            PendingBatchCount = pendingCount,
            CurrentPackageCount = displayedBatch?.PackageCount ?? 0,
            CompletedPackageCount = (activeBatch != null) ? activeCompletedPackageCount : 0,
            CurrentDisplayName = displayedBatch?.DisplayName ?? string.Empty
        };
    }
}
