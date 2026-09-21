using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable identity for one post-initialization warmup run.
/// </summary>
internal sealed class StartupPostInitializationWarmupRequest
{
    /// <summary>
    /// Initializes a warmup request for one startup token and scheduler generation.
    /// </summary>
    /// <param name="reason">The startup completion reason.</param>
    /// <param name="operationToken">The startup progress operation token.</param>
    /// <param name="schedulerGeneration">The scheduler generation that owns the work.</param>
    internal StartupPostInitializationWarmupRequest(
        string reason,
        long operationToken,
        long schedulerGeneration)
    {
        Reason = reason ?? string.Empty;
        OperationToken = operationToken;
        SchedulerGeneration = schedulerGeneration;
    }

    /// <summary>Gets the stable scheduling reason.</summary>
    internal string Reason { get; }

    /// <summary>Gets the startup operation token.</summary>
    internal long OperationToken { get; }

    /// <summary>Gets the scheduler generation.</summary>
    internal long SchedulerGeneration { get; }
}

/// <summary>
/// Describes how one post-initialization warmup reached its terminal boundary.
/// </summary>
internal enum StartupPostInitializationWarmupCompletionKind
{
    Completed,
    Failed,
    Rejected,
    Discarded,
    Cancelled
}

/// <summary>
/// Immutable terminal receipt for one post-initialization warmup.
/// </summary>
internal sealed class StartupPostInitializationWarmupCompletion
{
    /// <summary>Initializes a terminal warmup receipt.</summary>
    internal StartupPostInitializationWarmupCompletion(
        StartupPostInitializationWarmupRequest request,
        StartupPostInitializationWarmupCompletionKind kind,
        string failedStage,
        Exception exception)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Kind = kind;
        FailedStage = failedStage ?? string.Empty;
        Exception = exception;
    }

    /// <summary>Gets the exact request that reached the terminal boundary.</summary>
    internal StartupPostInitializationWarmupRequest Request { get; }

    /// <summary>Gets the terminal disposition.</summary>
    internal StartupPostInitializationWarmupCompletionKind Kind { get; }

    /// <summary>Gets the failed stage name, or an empty value when no stage failed.</summary>
    internal string FailedStage { get; }

    /// <summary>Gets the original stage failure, when present.</summary>
    internal Exception Exception { get; }
}

/// <summary>
/// Owns the single post-initialization adjacent-index and virtual-order warmup lifecycle.
/// </summary>
internal sealed class StartupPostInitializationWarmupOwner
{
    internal const string SchedulerTaskName = "playlist_virtual_order_prewarm";

    private const int WorkScheduling = 0;
    private const int WorkQueued = 1;
    private const int WorkRunning = 2;
    private const int WorkTerminalBeforeRun = 3;
    private const int WorkFinished = 4;

    private sealed class Operation
    {
        internal StartupPostInitializationWarmupRequest Request;
        internal StartupBackgroundTaskReservation Reservation;
        internal RegularChartListPrewarmLease Lease;
        internal CancellationTokenSource Cancellation;
        internal int Completed;
        internal int WorkState;
        internal int SchedulingCancelled;
        internal int LeaseCancellationRequested;
        internal int LeaseReleased;
        internal int CancellationDisposed;
        internal int QueueCancellationInProgress;
        internal int QueueCancellationPending;
        internal int QueueCancellationSatisfied;
        internal string CancellationReason;
    }

    private readonly object syncRoot = new();
    private readonly Func<RegularChartListPrewarmLease> acquireLease;
    private readonly Action<RegularChartListPrewarmLease> cancelLease;
    private readonly Action<RegularChartListPrewarmLease, string, CancellationToken> warmRealPath;
    private readonly Action<RegularChartListPrewarmLease, string, CancellationToken> warmInstallDestinationOverlay;
    private readonly Action<RegularChartListPrewarmLease, string, CancellationToken> warmInstalledPrimaryHash;
    private readonly Action<RegularChartListPrewarmLease, string, CancellationToken> warmPlaylistHash;
    private readonly Action<RegularChartListPrewarmLease, string> warmVirtualOrder;
    private readonly Func<string, long, long, StartupBackgroundTaskReservation> reserve;
    private readonly Func<StartupBackgroundTaskReservation, string, Func<Task>, Action<string>, bool> queueReserved;
    private readonly Func<StartupBackgroundTaskReservation, string, bool> cancelQueued;
    private readonly Action<StartupPostInitializationWarmupCompletion> completed;
    private readonly Action<Exception, string> logFailure;
    private readonly Func<CancellationToken, CancellationTokenSource> createLinkedCancellation;

    private Operation activeOperation;
    private bool hasScheduledIdentity;
    private long lastScheduledOperationToken;
    private long lastScheduledGeneration;
    // Reset is the only owner event that invalidates an in-flight reservation
    // categorically. Completion and supersession must not make a newer scheduler
    // reservation lose the owner arbitration race.
    private long resetEpoch;
    private long lastCommittedReservationSequence = long.MinValue;
    private long reservationAttemptSequence;

    /// <summary>
    /// Initializes the owner with the exact warmup stages and narrow scheduler/lease boundaries.
    /// </summary>
    /// <param name="acquireLease">Acquires the exact prewarm lease for one operation.</param>
    /// <param name="cancelLease">Cancels and releases a lease that cannot finish normally.</param>
    /// <param name="warmRealPath">Warms the real-path index.</param>
    /// <param name="warmInstallDestinationOverlay">Warms the install-destination overlay.</param>
    /// <param name="warmInstalledPrimaryHash">Warms the installed primary-hash index.</param>
    /// <param name="warmPlaylistHash">Warms the playlist-hash index.</param>
    /// <param name="warmVirtualOrder">Warms the virtual chart order.</param>
    /// <param name="reserve">Issues the named scheduler reservation for the captured generation and owner attempt sequence before lease acquisition.</param>
    /// <param name="queueReserved">Submits work through the reservation identity.</param>
    /// <param name="cancelQueued">Cancels the exact queued request represented by a reservation.</param>
    /// <param name="completed">Publishes the immutable terminal receipt.</param>
    /// <param name="logFailure">Records secondary boundary failures without replacing the primary result.</param>
    /// <param name="createLinkedCancellation">
    /// Creates the operation-owned cancellation source linked to the lease token.
    /// </param>
    internal StartupPostInitializationWarmupOwner(
        Func<RegularChartListPrewarmLease> acquireLease,
        Action<RegularChartListPrewarmLease> cancelLease,
        Action<RegularChartListPrewarmLease, string, CancellationToken> warmRealPath,
        Action<RegularChartListPrewarmLease, string, CancellationToken> warmInstallDestinationOverlay,
        Action<RegularChartListPrewarmLease, string, CancellationToken> warmInstalledPrimaryHash,
        Action<RegularChartListPrewarmLease, string, CancellationToken> warmPlaylistHash,
        Action<RegularChartListPrewarmLease, string> warmVirtualOrder,
        Func<string, long, long, StartupBackgroundTaskReservation> reserve,
        Func<StartupBackgroundTaskReservation, string, Func<Task>, Action<string>, bool> queueReserved,
        Func<StartupBackgroundTaskReservation, string, bool> cancelQueued,
        Action<StartupPostInitializationWarmupCompletion> completed,
        Action<Exception, string> logFailure,
        Func<CancellationToken, CancellationTokenSource> createLinkedCancellation = null)
    {
        this.acquireLease = acquireLease ?? throw new ArgumentNullException(nameof(acquireLease));
        this.cancelLease = cancelLease ?? throw new ArgumentNullException(nameof(cancelLease));
        this.warmRealPath = warmRealPath ?? throw new ArgumentNullException(nameof(warmRealPath));
        this.warmInstallDestinationOverlay = warmInstallDestinationOverlay ?? throw new ArgumentNullException(nameof(warmInstallDestinationOverlay));
        this.warmInstalledPrimaryHash = warmInstalledPrimaryHash ?? throw new ArgumentNullException(nameof(warmInstalledPrimaryHash));
        this.warmPlaylistHash = warmPlaylistHash ?? throw new ArgumentNullException(nameof(warmPlaylistHash));
        this.warmVirtualOrder = warmVirtualOrder ?? throw new ArgumentNullException(nameof(warmVirtualOrder));
        this.reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        this.queueReserved = queueReserved ?? throw new ArgumentNullException(nameof(queueReserved));
        this.cancelQueued = cancelQueued ?? throw new ArgumentNullException(nameof(cancelQueued));
        this.completed = completed ?? throw new ArgumentNullException(nameof(completed));
        this.logFailure = logFailure ?? throw new ArgumentNullException(nameof(logFailure));
        this.createLinkedCancellation = createLinkedCancellation
            ?? CancellationTokenSource.CreateLinkedTokenSource;
    }

    /// <summary>
    /// Schedules one warmup for an initialization-complete request and deduplicates its identity.
    /// </summary>
    /// <param name="request">The startup token and scheduler generation to own.</param>
    /// <returns><see langword="true"/> when a new scheduler request was accepted.</returns>
    internal bool Schedule(StartupPostInitializationWarmupRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var operation = new Operation
        {
            Request = request
        };
        long expectedResetEpoch;
        long attemptSequence;
        lock (syncRoot)
        {
            if (hasScheduledIdentity
                && lastScheduledOperationToken == request.OperationToken
                && lastScheduledGeneration == request.SchedulerGeneration)
            {
                return false;
            }
            expectedResetEpoch = resetEpoch;
            attemptSequence = ++reservationAttemptSequence;
        }

        Operation previous = null;
        StartupBackgroundTaskReservation reservation = null;
        Exception reservationFailure = null;
        bool reservationCommitted = false;
        // Reservation acquisition must remain outside syncRoot. The scheduler
        // takes its progress lock, while Reset can be entered from a progress
        // callback that already owns that lock.
        try
        {
            reservation = reserve(SchedulerTaskName, request.SchedulerGeneration, attemptSequence);
        }
        catch (Exception exception)
        {
            reservationFailure = exception;
        }
        if (reservation != null && reservationFailure == null)
        {
            lock (syncRoot)
            {
                bool canCommit = resetEpoch == expectedResetEpoch
                    && attemptSequence > lastCommittedReservationSequence
                    && reservation.OwnerSequence == attemptSequence;
                if (canCommit)
                {
                    operation.Reservation = reservation;
                    previous = activeOperation;
                    activeOperation = operation;
                    hasScheduledIdentity = true;
                    lastScheduledOperationToken = request.OperationToken;
                    lastScheduledGeneration = request.SchedulerGeneration;
                    lastCommittedReservationSequence = attemptSequence;
                    reservationCommitted = true;
                }
            }
            if (!reservationCommitted)
            {
                reservationFailure = new InvalidOperationException(
                    "The warmup reservation was superseded before state commit.");
                // A late reservation has no operation state to own. Typed cancellation
                // invalidates only this pre-submit receipt and never the newer queue.
                operation.Reservation = reservation;
                CancelQueuedOperation(operation, "schedule_state_changed");
            }
        }

        if (reservationFailure == null && !reservationCommitted && reservation != null)
        {
            reservationFailure = new InvalidOperationException(
                "The warmup reservation could not be committed.");
            operation.Reservation = reservation;
            CancelQueuedOperation(operation, "schedule_state_changed");
        }
        CancelOperation(previous, "superseded");
        if (previous != null)
        {
            CancelQueuedOperation(previous, "superseded");
        }

        if (reservationFailure != null)
        {
            Terminate(
                operation,
                StartupPostInitializationWarmupCompletionKind.Failed,
                "reservation",
                reservationFailure,
                cancel: false);
            return false;
        }
        if (reservation == null)
        {
            Terminate(
                operation,
                StartupPostInitializationWarmupCompletionKind.Rejected,
                "reservation",
                null,
                cancel: false);
            return false;
        }

        if (Volatile.Read(ref operation.SchedulingCancelled) != 0)
        {
            CancelQueuedOperation(operation, operation.CancellationReason ?? "schedule_cancelled");
            return false;
        }

        RegularChartListPrewarmLease lease;
        try
        {
            lease = acquireLease();
        }
        catch (Exception exception)
        {
            Terminate(
                operation,
                StartupPostInitializationWarmupCompletionKind.Failed,
                "lease",
                exception,
                cancel: false);
            CancelQueuedOperation(operation, "lease_failed");
            return false;
        }
        if (lease == null)
        {
            Terminate(
                operation,
                StartupPostInitializationWarmupCompletionKind.Rejected,
                "lease",
                null,
                cancel: false);
            CancelQueuedOperation(operation, "lease_rejected");
            return false;
        }

        Volatile.Write(ref operation.Lease, lease);
        try
        {
            Volatile.Write(
                ref operation.Cancellation,
                createLinkedCancellation(lease.Token)
                    ?? throw new InvalidOperationException("The linked cancellation factory returned null."));
        }
        catch (Exception exception)
        {
            Terminate(
                operation,
                StartupPostInitializationWarmupCompletionKind.Failed,
                "cancellation",
                exception,
                cancel: true);
            CancelQueuedOperation(operation, "cancellation_failed");
            return false;
        }
        if (Volatile.Read(ref operation.LeaseCancellationRequested) != 0)
        {
            ReleaseLease(operation, cancel: true);
        }
        if (Volatile.Read(ref operation.SchedulingCancelled) != 0
            || Volatile.Read(ref operation.Completed) != 0)
        {
            RequestLeaseCancellation(operation);
            DisposeCancellation(operation);
            CancelQueuedOperation(operation, operation.CancellationReason ?? "schedule_cancelled");
            return false;
        }

        bool accepted;
        try
        {
            accepted = queueReserved(
                operation.Reservation,
                request.Reason,
                () => RunIfOwnedAsync(operation),
                reason => Discard(operation, reason));
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref operation.SchedulingCancelled) != 0)
            {
                CancelQueuedOperation(operation, operation.CancellationReason ?? "schedule_cancelled");
                return false;
            }
            try
            {
                Terminate(
                    operation,
                    StartupPostInitializationWarmupCompletionKind.Failed,
                    "scheduler",
                    exception,
                    cancel: true);
            }
            finally
            {
                // QueueReserved may have registered the request before reporting a
                // post-registration failure. The receipt is the only safe cleanup
                // identity; retrying by name could remove a newer reservation.
                CancelQueuedOperation(operation, "scheduler_failed");
            }
            return false;
        }
        if (Volatile.Read(ref operation.SchedulingCancelled) != 0)
        {
            CancelQueuedOperation(operation, operation.CancellationReason ?? "schedule_cancelled");
            return false;
        }
        if (!accepted)
        {
            Terminate(
                operation,
                StartupPostInitializationWarmupCompletionKind.Rejected,
                "scheduler",
                null,
                cancel: true);
        }
        else
        {
            Interlocked.CompareExchange(ref operation.WorkState, WorkQueued, WorkScheduling);
        }
        if (Volatile.Read(ref operation.SchedulingCancelled) != 0)
        {
            CancelQueuedOperation(operation, operation.CancellationReason ?? "schedule_cancelled");
            return false;
        }
        return accepted;
    }

    /// <summary>
    /// Cancels the active lease and queued scheduler request before a new startup generation begins.
    /// </summary>
    /// <param name="reason">The reset reason.</param>
    internal void Reset(string reason)
    {
        Operation operation;
        lock (syncRoot)
        {
            operation = activeOperation;
            activeOperation = null;
            hasScheduledIdentity = false;
            lastScheduledOperationToken = 0L;
            lastScheduledGeneration = 0L;
            resetEpoch++;
        }
        CancelOperation(operation, reason);
        if (operation != null)
        {
            CancelQueuedOperation(operation, reason);
        }
    }

    private Task RunIfOwnedAsync(Operation operation)
    {
        while (true)
        {
            int state = Volatile.Read(ref operation.WorkState);
            if (state != WorkScheduling && state != WorkQueued)
            {
                DisposeCancellation(operation);
                return Task.CompletedTask;
            }
            if (Interlocked.CompareExchange(ref operation.WorkState, WorkRunning, state) == state)
            {
                return RunAsync(operation);
            }
        }
    }

    private async Task RunAsync(Operation operation)
    {
        StartupPostInitializationWarmupCompletionKind kind =
            StartupPostInitializationWarmupCompletionKind.Completed;
        string failedStage = string.Empty;
        Exception failure = null;
        try
        {
            CancellationToken token = operation.Cancellation.Token;
            (string Name, Action<RegularChartListPrewarmLease, string, CancellationToken> Run)[] adjacentStages =
            [
                ("real_path", warmRealPath),
                ("install_destination_overlay", warmInstallDestinationOverlay),
                ("installed_primary_hash", warmInstalledPrimaryHash),
                ("playlist_hash", warmPlaylistHash)
            ];
            foreach ((string name, Action<RegularChartListPrewarmLease, string, CancellationToken> run) in adjacentStages)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    run(operation.Lease, operation.Request.Reason, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    kind = StartupPostInitializationWarmupCompletionKind.Failed;
                    failedStage = name;
                    failure = exception;
                    ReportSecondaryFailure(exception, name);
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            try
            {
                warmVirtualOrder(operation.Lease, operation.Request.Reason);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (failure == null)
                {
                    kind = StartupPostInitializationWarmupCompletionKind.Failed;
                    failedStage = "virtual_order";
                    failure = exception;
                }
                ReportSecondaryFailure(exception, "virtual_order");
            }
            await Task.CompletedTask;
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            kind = StartupPostInitializationWarmupCompletionKind.Cancelled;
            failedStage = string.Empty;
            failure = null;
        }
        finally
        {
            ReleaseLease(operation, cancel: false);
            Terminate(operation, kind, failedStage, failure, cancel: false);
            DisposeCancellation(operation);
            Interlocked.Exchange(ref operation.WorkState, WorkFinished);
        }
    }

    private void Discard(Operation operation, string reason)
    {
        operation.CancellationReason = reason ?? string.Empty;
        Terminate(
            operation,
            StartupPostInitializationWarmupCompletionKind.Discarded,
            reason,
            null,
            cancel: true);
    }

    private void CancelOperation(Operation operation, string reason)
    {
        if (operation == null)
        {
            return;
        }
        operation.CancellationReason = reason ?? string.Empty;
        Terminate(
            operation,
            StartupPostInitializationWarmupCompletionKind.Cancelled,
            reason,
            null,
            cancel: true);
    }

    private void Terminate(
        Operation operation,
        StartupPostInitializationWarmupCompletionKind kind,
        string failedStage,
        Exception exception,
        bool cancel)
    {
        if (operation == null)
        {
            return;
        }
        if (cancel)
        {
            Interlocked.Exchange(ref operation.SchedulingCancelled, 1);
        }
        if (Interlocked.Exchange(ref operation.Completed, 1) != 0)
        {
            return;
        }
        if (cancel)
        {
            RequestLeaseCancellation(operation);
            DisposeCancellationBeforeRun(operation);
        }
        lock (syncRoot)
        {
            if (ReferenceEquals(activeOperation, operation))
            {
                activeOperation = null;
            }
        }
        PublishCompletion(new StartupPostInitializationWarmupCompletion(
            operation.Request,
            kind,
            failedStage,
            exception));
    }

    private void RequestLeaseCancellation(Operation operation)
    {
        Interlocked.Exchange(ref operation.LeaseCancellationRequested, 1);
        CancellationTokenSource cancellation = Volatile.Read(ref operation.Cancellation);
        if (cancellation != null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                ReportSecondaryFailure(exception, "lease_cancellation");
            }
        }
        ReleaseLease(operation, cancel: true);
    }

    private void ReleaseLease(Operation operation, bool cancel)
    {
        RegularChartListPrewarmLease lease = Volatile.Read(ref operation.Lease);
        if (lease == null || Interlocked.Exchange(ref operation.LeaseReleased, 1) != 0)
        {
            return;
        }
        try
        {
            if (cancel)
            {
                cancelLease(lease);
            }
            else
            {
                lease.Dispose();
            }
        }
        catch (Exception exception)
        {
            ReportSecondaryFailure(exception, cancel ? "lease_cancel" : "lease_dispose");
        }
    }

    private void DisposeCancellation(Operation operation)
    {
        CancellationTokenSource cancellation = Volatile.Read(ref operation.Cancellation);
        if (cancellation != null
            && Interlocked.Exchange(ref operation.CancellationDisposed, 1) == 0)
        {
            try
            {
                cancellation.Dispose();
            }
            catch (Exception exception)
            {
                ReportSecondaryFailure(exception, "cancellation_dispose");
            }
        }
    }

    private void DisposeCancellationBeforeRun(Operation operation)
    {
        while (true)
        {
            int state = Volatile.Read(ref operation.WorkState);
            if (state == WorkRunning || state == WorkFinished)
            {
                return;
            }
            if (state == WorkTerminalBeforeRun)
            {
                DisposeCancellation(operation);
                return;
            }
            if (Interlocked.CompareExchange(
                    ref operation.WorkState,
                    WorkTerminalBeforeRun,
                    state) == state)
            {
                DisposeCancellation(operation);
                return;
            }
        }
    }

    private void CancelQueuedOperation(Operation operation, string reason)
    {
        if (Volatile.Read(ref operation.QueueCancellationSatisfied) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref operation.QueueCancellationPending, 1);
        if (Interlocked.CompareExchange(ref operation.QueueCancellationInProgress, 1, 0) != 0)
        {
            return;
        }

        while (true)
        {
            Interlocked.Exchange(ref operation.QueueCancellationPending, 0);
            try
            {
                if (cancelQueued(operation.Reservation, reason))
                {
                    Interlocked.Exchange(ref operation.QueueCancellationSatisfied, 1);
                    Interlocked.Exchange(ref operation.QueueCancellationInProgress, 0);
                    return;
                }
            }
            catch (Exception exception)
            {
                ReportSecondaryFailure(exception, "scheduler_cancel");
            }

            if (Volatile.Read(ref operation.QueueCancellationPending) != 0)
            {
                continue;
            }
            Interlocked.Exchange(ref operation.QueueCancellationInProgress, 0);
            if (Interlocked.Exchange(ref operation.QueueCancellationPending, 0) == 0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref operation.QueueCancellationInProgress, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private void ReportSecondaryFailure(Exception exception, string stage)
    {
        try
        {
            logFailure(exception, stage);
        }
        catch
        {
            // Diagnostics must not replace the primary operation result or stop cleanup.
        }
    }

    private void PublishCompletion(StartupPostInitializationWarmupCompletion completion)
    {
        try
        {
            completed(completion);
        }
        catch (Exception exception)
        {
            ReportSecondaryFailure(exception, "completion_notification");
        }
    }
}
