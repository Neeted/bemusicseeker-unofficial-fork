using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

internal sealed class PendingInstallEstimateQueueProcessor
{
    private readonly object syncRoot = new object();

    private readonly Queue<PendingInstallEstimateBatchRequest> pendingBatches = new Queue<PendingInstallEstimateBatchRequest>();

    private readonly Action<PendingInstallEstimateBatchRequest, CancellationToken> processBatch;

    private readonly Action<PendingInstallEstimateQueueStatusSnapshot> statusChanged;

    private readonly Action<Exception> batchFailed;

    private bool workerRunning;

    private PendingInstallEstimateBatchRequest activeBatch;

    private int activeCompletedPackageCount;

    public PendingInstallEstimateQueueProcessor(
        Action<PendingInstallEstimateBatchRequest, CancellationToken> processBatch,
        Action<PendingInstallEstimateQueueStatusSnapshot> statusChanged,
        Action<Exception> batchFailed = null)
    {
        this.processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));
        this.statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
        this.batchFailed = batchFailed;
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
            PendingInstallEstimateQueueStatusSnapshot snapshot;
            lock (syncRoot)
            {
                if (pendingBatches.Count == 0)
                {
                    workerRunning = false;
                    activeBatch = null;
                    activeCompletedPackageCount = 0;
                    snapshot = CaptureStatusSnapshotUnsafe();
                    statusChanged(snapshot);
                    return;
                }
                batch = pendingBatches.Dequeue();
                activeBatch = batch;
                activeCompletedPackageCount = 0;
                snapshot = CaptureStatusSnapshotUnsafe();
            }
            statusChanged(snapshot);
            try
            {
                processBatch(batch, CancellationToken.None);
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
                    snapshot = CaptureStatusSnapshotUnsafe();
                }
                statusChanged(snapshot);
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
            IsActive = displayedBatch != null,
            Source = displayedBatch?.Source ?? PendingInstallEstimateBatchSource.StartupRestore,
            PendingBatchCount = pendingCount,
            CurrentPackageCount = displayedBatch?.PackageCount ?? 0,
            CompletedPackageCount = (activeBatch != null) ? activeCompletedPackageCount : 0,
            CurrentDisplayName = displayedBatch?.DisplayName ?? string.Empty
        };
    }
}
