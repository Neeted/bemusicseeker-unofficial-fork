using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

internal sealed class DropInstallQueueProcessor(Action<DroppedInstallBatchRequest, CancellationToken> processBatch, Action<DropInstallQueueStatusSnapshot> statusChanged, Action<Exception> batchFailed = null)
{
    private readonly object syncRoot = new();

    private readonly Queue<DroppedInstallBatchRequest> pendingBatches = new();

    private readonly Action<DroppedInstallBatchRequest, CancellationToken> processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));

    private readonly Action<DropInstallQueueStatusSnapshot> statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));

    private readonly Action<Exception> batchFailed = batchFailed;

    private TaskCompletionSource<bool> idleCompletion = CreateCompletedCompletion();

    private bool workerRunning;

    private bool cancelRequested;

    private bool backgroundCleanupInProgress;

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
                    && !backgroundCleanupInProgress
                    && activeBatch == null
                    && pendingBatches.Count == 0;
            }
        }
    }

    /// <summary>
    /// Returns a task that completes after the current queue lifecycle has published its terminal
    /// inactive status and finished any detached cancellation cleanup.
    /// </summary>
    internal Task WaitForIdleAsync()
    {
        lock (syncRoot)
        {
            return idleCompletion.Task.IsCompleted
                ? Task.CompletedTask
                : idleCompletion.Task;
        }
    }

    /// <summary>
    /// Gets whether the current lifecycle has published its terminal status and completed its
    /// idle receipt.
    /// </summary>
    internal bool IsIdleReceiptCompleted
    {
        get
        {
            lock (syncRoot)
            {
                return idleCompletion.Task.IsCompleted;
            }
        }
    }

    /// <summary>
    /// Attempts to transfer an acquired request to this FIFO queue.
    /// </summary>
    /// <returns><see langword="true"/> only when the request remains accepted by the queue.</returns>
    internal bool TryEnqueue(DroppedInstallBatchRequest request)
    {
        EnqueueTransition transition = TryEnqueueCore(request);
        PublishEnqueueTransition(transition);
        return transition.Accepted;
    }

    /// <summary>
    /// Linearizes queue insertion without invoking callbacks or starting worker tasks.
    /// </summary>
    /// <remarks>
    /// The owner may call this while holding its generation lock because this method performs only
    /// queue-state mutation and status snapshot creation while holding the processor lock.
    /// </remarks>
    internal EnqueueTransition TryEnqueueCore(DroppedInstallBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PathCount == 0)
        {
            return EnqueueTransition.Rejected;
        }

        lock (syncRoot)
        {
            if (cancelRequested)
            {
                return EnqueueTransition.Rejected;
            }

            if (IsIdleUnsafe())
            {
                idleCompletion = CreatePendingCompletion();
            }

            pendingBatches.Enqueue(request);
            bool startWorker = !workerRunning;
            if (startWorker)
            {
                workerRunning = true;
            }
            return EnqueueTransition.Accept(
                CaptureStatusSnapshotUnsafe(),
                startWorker);
        }
    }

    /// <summary>
    /// Publishes the callback and worker-start portion of an accepted enqueue outside all owner and queue locks.
    /// </summary>
    internal void PublishEnqueueTransition(EnqueueTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!transition.Accepted)
        {
            return;
        }

        try
        {
            statusChanged(transition.StatusSnapshot);
        }
        finally
        {
            if (transition.StartWorker)
            {
                StartWorker();
            }
        }
    }

    public void CancelAll()
    {
        DroppedInstallBatchRequest[] abandonedBatches;
        CancellationTokenSource cancellationTokenSource;
        DropInstallQueueStatusSnapshot snapshot;
        lock (syncRoot)
        {
            if (cancelRequested
                || (activeBatch == null
                    && pendingBatches.Count == 0
                    && !workerRunning))
            {
                return;
            }

            cancelRequested = true;
            snapshot = CaptureStatusSnapshotUnsafe();
            abandonedBatches = [.. pendingBatches];
            pendingBatches.Clear();
            backgroundCleanupInProgress = abandonedBatches.Length > 0;
            cancellationTokenSource = activeCancellationTokenSource;
            activeBatch?.TryReserveAbandonmentBeforeInstallerHandoff();
        }

        TryCancel(cancellationTokenSource);
        try
        {
            if (snapshot.IsActive)
            {
                statusChanged(snapshot);
            }
        }
        finally
        {
            if (abandonedBatches.Length > 0)
            {
                StartBackgroundCleanup(abandonedBatches);
            }
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

    private void StartWorker()
    {
        Task.Factory.StartNew(
            ProcessLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ObserveFault("DropInstallQueueProcessor");
    }

    private void ProcessLoop()
    {
        while (true)
        {
            DroppedInstallBatchRequest batch;
            CancellationTokenSource cancellationTokenSource;
            DropInstallQueueStatusSnapshot snapshot;
            TaskCompletionSource<bool> terminalCompletion;
            bool shouldExit;
            lock (syncRoot)
            {
                if (cancelRequested || pendingBatches.Count == 0)
                {
                    workerRunning = false;
                    activeBatch = null;
                    activeCompletedPathCount = 0;
                    ClearActiveCurrentWorkUnsafe();
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    snapshot = cancelRequested
                        ? TryCompleteCancellationUnsafe()
                        : CaptureStatusSnapshotUnsafe();
                    terminalCompletion = snapshot == null ? null : idleCompletion;
                    batch = null;
                    cancellationTokenSource = null;
                    shouldExit = true;
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
                    terminalCompletion = null;
                    shouldExit = false;
                }
            }

            if (snapshot != null)
            {
                PublishStatus(snapshot, terminalCompletion);
            }
            if (shouldExit)
            {
                return;
            }

            try
            {
                try
                {
                    processBatch(batch, cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    ReportBatchFailure(exception);
                }
            }
            finally
            {
                batch.TryAbandonUnconsumedSources();
                lock (syncRoot)
                {
                    activeBatch = null;
                    activeCompletedPathCount = 0;
                    ClearActiveCurrentWorkUnsafe();
                    activeCancellationTokenSource?.Dispose();
                    activeCancellationTokenSource = null;
                    shouldExit = cancelRequested || pendingBatches.Count == 0;
                    if (shouldExit)
                    {
                        workerRunning = false;
                        snapshot = cancelRequested
                            ? TryCompleteCancellationUnsafe()
                            : CaptureStatusSnapshotUnsafe();
                        terminalCompletion = snapshot == null ? null : idleCompletion;
                    }
                    else
                    {
                        snapshot = CaptureStatusSnapshotUnsafe();
                        terminalCompletion = null;
                    }
                }

                if (snapshot != null)
                {
                    PublishStatus(snapshot, terminalCompletion);
                }
            }
            if (shouldExit)
            {
                return;
            }
        }
    }

    private void StartBackgroundCleanup(DroppedInstallBatchRequest[] abandonedBatches)
    {
        Task.Factory.StartNew(
            () => CompleteBackgroundCleanup(abandonedBatches),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ObserveFault("DropInstallQueueCleanup");
    }

    private void CompleteBackgroundCleanup(DroppedInstallBatchRequest[] abandonedBatches)
    {
        try
        {
            AbandonRequests(abandonedBatches);
        }
        finally
        {
            DropInstallQueueStatusSnapshot terminalSnapshot;
            TaskCompletionSource<bool> terminalCompletion;
            lock (syncRoot)
            {
                backgroundCleanupInProgress = false;
                terminalSnapshot = TryCompleteCancellationUnsafe();
                terminalCompletion = terminalSnapshot == null ? null : idleCompletion;
            }
            if (terminalSnapshot != null)
            {
                PublishStatus(terminalSnapshot, terminalCompletion);
            }
        }
    }

    private void PublishStatus(
        DropInstallQueueStatusSnapshot snapshot,
        TaskCompletionSource<bool> terminalCompletion)
    {
        if (terminalCompletion == null)
        {
            statusChanged(snapshot);
            return;
        }

        try
        {
            statusChanged(snapshot);
        }
        finally
        {
            terminalCompletion.TrySetResult(true);
        }
    }

    private DropInstallQueueStatusSnapshot TryCompleteCancellationUnsafe()
    {
        if (!cancelRequested
            || workerRunning
            || activeBatch != null
            || pendingBatches.Count > 0
            || backgroundCleanupInProgress)
        {
            return null;
        }

        cancelRequested = false;
        return CaptureStatusSnapshotUnsafe();
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

    private bool IsIdleUnsafe()
    {
        return !workerRunning
            && !cancelRequested
            && !backgroundCleanupInProgress
            && activeBatch == null
            && pendingBatches.Count == 0;
    }

    private static TaskCompletionSource<bool> CreatePendingCompletion()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<bool> CreateCompletedCompletion()
    {
        TaskCompletionSource<bool> completion = CreatePendingCompletion();
        completion.SetResult(true);
        return completion;
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

    private void ReportBatchFailure(Exception batchException)
    {
        try
        {
            batchFailed?.Invoke(batchException);
        }
        catch (Exception notificationException)
        {
            try
            {
                NLogWrapper.FileLogger?.Warn(
                    notificationException,
                    "drop_install_failure_notification_failed");
            }
            catch
            {
                // A diagnostic boundary must not strand the FIFO worker or its owned sources.
            }
        }
    }

    /// <summary>
    /// Carries a callback-free queue insertion result across the owner lock boundary.
    /// </summary>
    internal sealed class EnqueueTransition
    {
        private EnqueueTransition(
            bool accepted,
            DropInstallQueueStatusSnapshot statusSnapshot,
            bool startWorker)
        {
            Accepted = accepted;
            StatusSnapshot = statusSnapshot;
            StartWorker = startWorker;
        }

        /// <summary>
        /// Gets whether physical queue insertion succeeded.
        /// </summary>
        internal bool Accepted { get; }

        /// <summary>
        /// Gets the status captured at the insertion linearization point.
        /// </summary>
        internal DropInstallQueueStatusSnapshot StatusSnapshot { get; }

        /// <summary>
        /// Gets whether publishing this transition must start the serial worker.
        /// </summary>
        internal bool StartWorker { get; }

        /// <summary>
        /// Gets a transition that leaves request ownership with the caller.
        /// </summary>
        internal static EnqueueTransition Rejected { get; } = new(false, null, false);

        /// <summary>
        /// Creates a transition for a request physically inserted into the queue.
        /// </summary>
        internal static EnqueueTransition Accept(
            DropInstallQueueStatusSnapshot statusSnapshot,
            bool startWorker)
        {
            return new EnqueueTransition(
                accepted: true,
                statusSnapshot ?? throw new ArgumentNullException(nameof(statusSnapshot)),
                startWorker);
        }
    }
}
