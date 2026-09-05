using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable identity issued before a named startup request is submitted.
/// </summary>
internal sealed class StartupBackgroundTaskReservation
{
    /// <summary>Initializes a scheduler-owned reservation identity.</summary>
    /// <param name="name">The normalized scheduler request name.</param>
    /// <param name="reservationId">The monotonically increasing reservation identity.</param>
    /// <param name="expectedGeneration">The scheduler generation captured at reservation time.</param>
    /// <param name="ownerSequence">The warmup owner's globally monotonic reservation attempt sequence.</param>
    internal StartupBackgroundTaskReservation(
        string name,
        long reservationId,
        long expectedGeneration,
        long ownerSequence)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ReservationId = reservationId;
        ExpectedGeneration = expectedGeneration;
        OwnerSequence = ownerSequence;
    }

    /// <summary>Gets the normalized named queue identity.</summary>
    internal string Name { get; }

    /// <summary>Gets the immutable reservation sequence identity.</summary>
    internal long ReservationId { get; }

    /// <summary>Gets the scheduler generation captured before submit.</summary>
    internal long ExpectedGeneration { get; }

    /// <summary>Gets the warmup owner's monotonic reservation attempt sequence.</summary>
    internal long OwnerSequence { get; }
}

/// <summary>
/// Owns startup background work scheduling, accounting, and shutdown drain policy.
/// </summary>
internal sealed class StartupBackgroundTaskSchedulerOwner
{
    private const int ReservationStateQueued = 1;
    private const int ReservationStateRunning = 2;
    private const int ReservationStateTerminal = 3;

    private sealed class Request
    {
        internal string Name;

        internal string Reason;

        internal string Dependency;

        internal string CoalesceKey;

        internal string Lane;

        internal bool IsPostInitialization;

        internal int Priority;

        internal long Version;

        internal long Generation;

        internal StartupBackgroundTaskReservation Reservation;

        // A reserved identity is a one-shot submit receipt. The state is only
        // observed and changed while the scheduler locks are held.
        internal int ReservationState;

        internal Func<Task> Work;

        internal Action<string> Discard;
    }

    private sealed class Metric
    {
        internal string Name = string.Empty;

        internal string Reason = string.Empty;

        internal string Dependency = string.Empty;

        internal string Lane = string.Empty;

        internal long QueuedCount;

        internal long StartedCount;

        internal long CompletedCount;

        internal long FailedCount;

        internal long TotalElapsedMs;

        internal long LastElapsedMs;

        internal string LastStatus = string.Empty;

        internal string LastDetail = string.Empty;
    }

    private readonly object syncRoot = new();

    private readonly object progressSynchronization;

    private readonly Func<bool> isShutdownRequested;

    private readonly Action<string> logInfo;

    private readonly Action<string> logWarning;

    private readonly Action<string> logShutdown;

    private readonly Func<string, string> formatTextForLog;

    private readonly Action<long, long> schedulerIdleChanged;

    private readonly List<Request> queue = [];

    private readonly Dictionary<string, long> latestRequestVersionByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, long> completedRequestVersionByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, StartupBackgroundTaskReservation> currentReservationByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, long> latestReservationSequenceByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> runningCountByLane = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Metric> metrics = new(StringComparer.OrdinalIgnoreCase);

    private bool started;

    private bool postInitializationSchedulingComplete;

    private bool requiredInitializationSchedulingComplete;

    private int runningCount;

    private int runningRequiredCount;

    private int runningPostInitializationCount;

    private int runningPostInitializationIdleOnlyCount;

    private bool shutdownRequested;

    private long version;

    private long generation;

    private long reservationId;

    private long idleRevision;

    internal StartupBackgroundTaskSchedulerOwner(
        Func<bool> isShutdownRequested,
        Action<string> logInfo,
        Action<string> logWarning,
        Action<string> logShutdown,
        Func<string, string> formatTextForLog,
        Action<long, long> schedulerIdleChanged,
        object progressSynchronization)
    {
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        this.logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        this.logShutdown = logShutdown ?? throw new ArgumentNullException(nameof(logShutdown));
        this.formatTextForLog = formatTextForLog ?? throw new ArgumentNullException(nameof(formatTextForLog));
        this.schedulerIdleChanged = schedulerIdleChanged ?? throw new ArgumentNullException(nameof(schedulerIdleChanged));
        this.progressSynchronization = progressSynchronization ?? throw new ArgumentNullException(nameof(progressSynchronization));
    }

    internal object ProgressSynchronization => progressSynchronization;

    internal bool IsStarted
    {
        get
        {
            lock (syncRoot)
            {
                return started;
            }
        }
    }

    internal bool IsIdle
    {
        get
        {
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    return IsRequiredWorkIdleUnsafe();
                }
            }
        }
    }

    internal bool IsFullyIdle
    {
        get
        {
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    return IsFullyIdleUnsafe();
                }
            }
        }
    }

    internal StartupBackgroundWorkSnapshot CaptureWorkSnapshot()
    {
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                return new StartupBackgroundWorkSnapshot(queue.Count, runningCount);
            }
        }
    }

    internal bool IsPostInitializationSchedulingComplete
    {
        get
        {
            lock (syncRoot)
            {
                return postInitializationSchedulingComplete;
            }
        }
    }

    /// <summary>
    /// Gets whether no more required-initialization requests may be enrolled in the current generation.
    /// </summary>
    internal bool IsRequiredInitializationSchedulingComplete
    {
        get
        {
            lock (syncRoot)
            {
                return requiredInitializationSchedulingComplete;
            }
        }
    }

    internal long CurrentGeneration
    {
        get
        {
            lock (syncRoot)
            {
                return generation;
            }
        }
    }

    internal bool IsCurrentGeneration(long candidateGeneration)
    {
        lock (syncRoot)
        {
            return generation == candidateGeneration;
        }
    }

    internal bool IsCurrentIdleSnapshot(long candidateGeneration, long candidateRevision)
    {
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                return generation == candidateGeneration
                    && idleRevision == candidateRevision
                    && IsRequiredWorkIdleUnsafe();
            }
        }
    }

    /// <summary>
    /// Atomically issues a named reservation only when the generation is current and the
    /// owner attempt sequence is newer than every accepted attempt for that name in the generation.
    /// </summary>
    /// <param name="name">The request name to reserve.</param>
    /// <param name="expectedGeneration">The scheduler generation captured by the owner.</param>
    /// <param name="ownerSequence">The owner's globally monotonic reservation attempt sequence.</param>
    /// <returns>The reservation, or <see langword="null"/> when shutdown, generation, or sequence ordering rejects it.</returns>
    internal StartupBackgroundTaskReservation Reserve(
        string name,
        long expectedGeneration,
        long ownerSequence)
    {
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        if (isShutdownRequested())
        {
            logInfo("startup_background_task reservation_rejected name=" + normalizedName + " detail=shutdown_requested");
            return null;
        }

        StartupBackgroundTaskReservation reservation = null;
        bool skippedAfterLock = false;
        string rejectionDetail = null;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (shutdownRequested
                    || expectedGeneration != generation)
                {
                    skippedAfterLock = true;
                    rejectionDetail = shutdownRequested
                        ? "shutdown_requested_after_lock"
                        : "generation_mismatch";
                }
                else if (latestReservationSequenceByName.TryGetValue(normalizedName, out long latestOwnerSequence)
                    && ownerSequence <= latestOwnerSequence)
                {
                    skippedAfterLock = true;
                    rejectionDetail = "owner_sequence_not_newer";
                }
                else
                {
                    latestReservationSequenceByName[normalizedName] = ownerSequence;
                    reservation = new StartupBackgroundTaskReservation(
                        normalizedName,
                        ++reservationId,
                        generation,
                        ownerSequence);
                    currentReservationByName[normalizedName] = reservation;
                }
            }
        }
        if (skippedAfterLock)
        {
            logInfo("startup_background_task reservation_rejected name=" + normalizedName
                + " detail=" + rejectionDetail);
            return null;
        }

        logInfo("startup_background_task reservation name=" + reservation.Name
            + " reservationId=" + reservation.ReservationId
            + " generation=" + reservation.ExpectedGeneration
            + " ownerSequence=" + reservation.OwnerSequence);
        return reservation;
    }

    internal bool Queue(
        string name,
        string reason,
        string dependency,
        Func<Task> work,
        Action<string> discard = null)
    {
        if (work == null)
        {
            return false;
        }
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        if (isShutdownRequested())
        {
            logInfo("startup_background_task skipped name=" + normalizedName + " reason=" + normalizedReason + " detail=shutdown_requested");
            return false;
        }
        string normalizedDependency = string.IsNullOrWhiteSpace(dependency) ? null : dependency;
        string normalizedLane = GetLane(normalizedName);
        int priority = GetPriority(normalizedName);
        bool isPostInitialization = IsPostInitializationTask(normalizedName);
        bool shouldStartWorker = false;
        bool skippedAfterLock = false;
        string replacedLog = null;
        string queuedLog = null;
        Request discardedReplacedRequest = null;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (shutdownRequested)
                {
                    skippedAfterLock = true;
                }
                else
                {
                    RecordQueuedUnsafe(normalizedName, normalizedReason, normalizedDependency, normalizedLane);
                    idleRevision++;
                    long requestVersion = ++version;
                    latestRequestVersionByName[normalizedName] = requestVersion;
                    Request existing = queue.LastOrDefault(item => string.Equals(item.CoalesceKey, normalizedName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        if (existing.Reservation != null)
                        {
                            queue.Remove(existing);
                            existing.ReservationState = ReservationStateTerminal;
                            RecordDiscardedUnsafe(existing, "replaced_by_general_queue");
                            discardedReplacedRequest = existing;
                            existing = null;
                        }
                        if (existing != null)
                        {
                            existing.Reason = normalizedReason;
                            existing.Dependency = normalizedDependency;
                            existing.Lane = normalizedLane;
                            existing.IsPostInitialization = isPostInitialization;
                            existing.Priority = priority;
                            existing.Version = requestVersion;
                            existing.Work = work;
                            existing.Discard = discard;
                            replacedLog = "startup_background_task skipped name=" + normalizedName + " version=" + requestVersion + " generation=" + existing.Generation + " reason=" + normalizedReason + " kind=" + FormatRequestKind(isPostInitialization) + " coalesceKey=" + normalizedName + " replaced=true";
                        }
                    }
                    currentReservationByName.Remove(normalizedName);
                    if (existing == null)
                    {
                        queue.Add(new Request
                        {
                            Name = normalizedName,
                            Reason = normalizedReason,
                            Dependency = normalizedDependency,
                            Lane = normalizedLane,
                            IsPostInitialization = isPostInitialization,
                            CoalesceKey = normalizedName,
                            Priority = priority,
                            Version = requestVersion,
                            Generation = generation,
                            Reservation = null,
                            ReservationState = 0,
                            Work = work,
                            Discard = discard
                        });
                    }
                    queuedLog = "startup_background_task queue name=" + normalizedName + " version=" + requestVersion + " generation=" + generation + " reason=" + normalizedReason + " kind=" + FormatRequestKind(isPostInitialization) + " dependency=" + (normalizedDependency ?? "(none)") + " lane=" + normalizedLane + " priority=" + priority;
                    shouldStartWorker = started;
                }
            }
        }
        if (skippedAfterLock)
        {
            logInfo("startup_background_task skipped name=" + normalizedName + " reason=" + normalizedReason + " detail=shutdown_requested_after_lock");
            return false;
        }
        if (replacedLog != null)
        {
            logInfo(replacedLog);
        }
        if (discardedReplacedRequest != null)
        {
            InvokeDiscard(discardedReplacedRequest, "replaced_by_general_queue");
        }
        logInfo(queuedLog);
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        return true;
    }

    /// <summary>
    /// Submits one reserved named request after validating its identity and generation.
    /// A reserved submit may coalesce only the request currently represented by the same name;
    /// a stale reservation cannot replace a newer request.
    /// </summary>
    /// <param name="reservation">The immutable reservation issued by <see cref="Reserve"/>.</param>
    /// <param name="reason">The request reason used for metrics and diagnostics.</param>
    /// <param name="dependency">Optional comma-separated scheduler dependencies.</param>
    /// <param name="work">The asynchronous work to run.</param>
    /// <param name="discard">The callback for an accepted request removed before running.</param>
    /// <returns><see langword="true"/> when the reserved request was accepted.</returns>
    internal bool QueueReserved(
        StartupBackgroundTaskReservation reservation,
        string reason,
        string dependency,
        Func<Task> work,
        Action<string> discard = null)
    {
        if (reservation == null || work == null)
        {
            return false;
        }

        string normalizedName = reservation.Name;
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        if (isShutdownRequested())
        {
            logInfo("startup_background_task reserved_submit_rejected name=" + normalizedName
                + " reservationId=" + reservation.ReservationId
                + " reason=" + normalizedReason
                + " detail=shutdown_requested");
            return false;
        }

        string normalizedDependency = string.IsNullOrWhiteSpace(dependency) ? null : dependency;
        string normalizedLane = GetLane(normalizedName);
        int priority = GetPriority(normalizedName);
        bool isPostInitialization = IsPostInitializationTask(normalizedName);
        bool shouldStartWorker = false;
        bool rejectedAfterLock = false;
        string rejectionDetail = null;
        Request discardedReplacedRequest = null;
        string replacedLog = null;
        string queuedLog = null;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (shutdownRequested
                    || reservation.ExpectedGeneration != generation
                    || !currentReservationByName.TryGetValue(normalizedName, out StartupBackgroundTaskReservation currentReservation)
                    || !ReferenceEquals(currentReservation, reservation))
                {
                    rejectedAfterLock = true;
                    rejectionDetail = shutdownRequested
                        ? "shutdown_requested_after_lock"
                        : reservation.ExpectedGeneration != generation
                            ? "generation_mismatch"
                            : "stale_reservation";
                }
                else
                {
                    Request existing = queue.LastOrDefault(item => string.Equals(item.CoalesceKey, normalizedName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && ReferenceEquals(existing.Reservation, reservation))
                    {
                        rejectedAfterLock = true;
                        rejectionDetail = existing.ReservationState == ReservationStateQueued
                            ? "reservation_already_queued"
                            : "reservation_not_submittable";
                    }
                    else
                    {
                        RecordQueuedUnsafe(normalizedName, normalizedReason, normalizedDependency, normalizedLane);
                        idleRevision++;
                        long requestVersion = ++version;
                        latestRequestVersionByName[normalizedName] = requestVersion;
                        if (existing != null)
                        {
                            queue.Remove(existing);
                            existing.ReservationState = ReservationStateTerminal;
                            RecordDiscardedUnsafe(existing, "replaced_by_reserved_queue");
                            discardedReplacedRequest = existing;
                            replacedLog = "startup_background_task reserved_submit_coalesced name=" + normalizedName
                                + " oldVersion=" + existing.Version
                                + " oldGeneration=" + existing.Generation
                                + " reservationId=" + reservation.ReservationId
                                + " replaced=true";
                            existing = null;
                        }
                        queue.Add(new Request
                        {
                            Name = normalizedName,
                            Reason = normalizedReason,
                            Dependency = normalizedDependency,
                            Lane = normalizedLane,
                            IsPostInitialization = isPostInitialization,
                            CoalesceKey = normalizedName,
                            Priority = priority,
                            Version = requestVersion,
                            Generation = generation,
                            Reservation = reservation,
                            ReservationState = ReservationStateQueued,
                            Work = work,
                            Discard = discard
                        });
                        queuedLog = "startup_background_task reserved_submit name=" + normalizedName
                            + " reservationId=" + reservation.ReservationId
                            + " version=" + requestVersion
                            + " generation=" + generation
                            + " reason=" + normalizedReason
                            + " kind=" + FormatRequestKind(isPostInitialization)
                            + " dependency=" + (normalizedDependency ?? "(none)")
                            + " lane=" + normalizedLane
                            + " priority=" + priority;
                        shouldStartWorker = started;
                    }
                }
            }
        }
        if (rejectedAfterLock)
        {
            logInfo("startup_background_task reserved_submit_rejected name=" + normalizedName
                + " reservationId=" + reservation.ReservationId
                + " reason=" + normalizedReason
                + " detail=" + rejectionDetail);
            return false;
        }
        if (discardedReplacedRequest != null)
        {
            InvokeDiscard(discardedReplacedRequest, "replaced_by_reserved_queue");
        }
        if (replacedLog != null)
        {
            logInfo(replacedLog);
        }
        logInfo(queuedLog);
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        return true;
    }

    /// <summary>Submits a reserved request without a dependency.</summary>
    /// <param name="reservation">The immutable scheduler reservation.</param>
    /// <param name="reason">The request reason.</param>
    /// <param name="work">The asynchronous work to run.</param>
    /// <param name="discard">The callback for a queued request discarded before running.</param>
    /// <returns><see langword="true"/> when the reserved request was accepted.</returns>
    internal bool QueueReserved(
        StartupBackgroundTaskReservation reservation,
        string reason,
        Func<Task> work,
        Action<string> discard = null)
    {
        return QueueReserved(reservation, reason, null, work, discard);
    }

    internal void Start()
    {
        bool shouldStartWorker;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (started)
                {
                    return;
                }
                started = true;
                idleRevision++;
                shouldStartWorker = queue.Count > 0;
            }
        }
        logInfo("startup_background_task scheduler_start");
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        else
        {
            NotifyIdleChanged();
        }
    }

    internal void Reset(bool startImmediately)
    {
        bool shouldStartWorker;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                shutdownRequested = false;
                postInitializationSchedulingComplete = false;
                requiredInitializationSchedulingComplete = false;
                generation++;
                latestReservationSequenceByName.Clear();
                idleRevision++;
                for (int i = 0; i < queue.Count; i++)
                {
                    Request request = queue[i];
                    request.Generation = generation;
                    request.Version = ++version;
                    latestRequestVersionByName[request.Name] = request.Version;
                }
                foreach (string staleReservationName in currentReservationByName
                    .Where(pair => !queue.Any(request => ReferenceEquals(request.Reservation, pair.Value)))
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    currentReservationByName.Remove(staleReservationName);
                }
                metrics.Clear();
                foreach (Request request in queue)
                {
                    RecordQueuedUnsafe(request.Name, request.Reason, request.Dependency, request.Lane);
                }
                started = startImmediately;
                shouldStartWorker = started && queue.Count > 0;
            }
        }
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
    }

    /// <summary>
    /// Cancels the exact queued request represented by a scheduler reservation.
    /// Running work is not interrupted and must be cancelled by its feature owner.
    /// </summary>
    /// <param name="reservation">The reservation that owns the queued request.</param>
    /// <param name="reason">The cancellation reason passed to the discard boundary.</param>
    /// <returns>
    /// <see langword="true"/> when the exact queued request was removed; otherwise
    /// <see langword="false"/>. A matching pre-submit reservation is invalidated
    /// even when no queued request exists, while a running or newer reservation is untouched.
    /// </returns>
    internal bool CancelQueued(StartupBackgroundTaskReservation reservation, string reason)
    {
        if (reservation == null)
        {
            return false;
        }
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "cancelled" : reason;
        Request cancelled = null;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                int index = queue.FindLastIndex(request => ReferenceEquals(request.Reservation, reservation));
                if (index < 0)
                {
                    if (currentReservationByName.TryGetValue(reservation.Name, out StartupBackgroundTaskReservation preSubmitReservation)
                        && ReferenceEquals(preSubmitReservation, reservation))
                    {
                        currentReservationByName.Remove(reservation.Name);
                    }
                    return false;
                }

                cancelled = queue[index];
                queue.RemoveAt(index);
                cancelled.ReservationState = ReservationStateTerminal;
                if (currentReservationByName.TryGetValue(cancelled.Name, out StartupBackgroundTaskReservation currentReservation)
                    && ReferenceEquals(currentReservation, reservation))
                {
                    currentReservationByName.Remove(cancelled.Name);
                }
                RecordDiscardedUnsafe(cancelled, normalizedReason);
                idleRevision++;
            }
        }

        try
        {
            logInfo("startup_background_task cancelled name=" + cancelled.Name
                + " reservationId=" + reservation.ReservationId
                + " version=" + cancelled.Version
                + " reason=" + cancelled.Reason
                + " cancelReason=" + normalizedReason);
        }
        catch
        {
            // Diagnostic failure must not skip the exact discard or scheduler progress.
        }
        InvokeDiscard(cancelled, normalizedReason);
        try
        {
            TryStartWorkers();
        }
        finally
        {
            NotifyIdleChanged();
        }
        return true;
    }

    internal void MarkPostInitializationSchedulingComplete()
    {
        bool notifyIdle = false;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                postInitializationSchedulingComplete = true;
                notifyIdle = started && IsFullyIdleUnsafe();
            }
        }
        if (notifyIdle)
        {
            NotifyIdleChanged();
        }
        TryStartWorkers();
    }

    internal void MarkRequiredInitializationSchedulingComplete()
    {
        MarkRequiredInitializationSchedulingComplete(CurrentGeneration);
    }

    internal bool MarkRequiredInitializationSchedulingComplete(long candidateGeneration)
    {
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (generation != candidateGeneration)
                {
                    return false;
                }
                requiredInitializationSchedulingComplete = true;
            }
        }
        TryStartWorkers();
        return true;
    }

    internal void RequestShutdown(string reason)
    {
        bool shouldStartWorker;
        int originalQueuedCount;
        int discardedCount = 0;
        int drainQueuedCount;
        int activeRunningCount;
        List<Request> discardedRequests = null;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                shutdownRequested = true;
                originalQueuedCount = queue.Count;
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    Request request = queue[i];
                    if (IsRequiredForShutdown(request.Name))
                    {
                        continue;
                    }
                    queue.RemoveAt(i);
                    if (request.Reservation != null
                        && currentReservationByName.TryGetValue(request.Name, out StartupBackgroundTaskReservation currentReservation)
                        && ReferenceEquals(currentReservation, request.Reservation))
                    {
                        currentReservationByName.Remove(request.Name);
                    }
                    RecordDiscardedUnsafe(request, reason);
                    discardedRequests ??= [];
                    discardedRequests.Add(request);
                    discardedCount++;
                }
                drainQueuedCount = queue.Count;
                if (drainQueuedCount > 0)
                {
                    started = true;
                }
                idleRevision++;
                shouldStartWorker = drainQueuedCount > 0;
                activeRunningCount = runningCount;
            }
        }
        logShutdown("startup_background_task drain_queued reason=" + formatTextForLog(reason)
            + " queued=" + originalQueuedCount
            + " drainQueued=" + drainQueuedCount
            + " discarded=" + discardedCount
            + " running=" + activeRunningCount);
        if (discardedRequests != null)
        {
            foreach (Request request in discardedRequests)
            {
                logInfo("startup_background_task discarded name=" + request.Name
                    + " version=" + request.Version
                    + " reason=" + request.Reason
                    + " shutdownReason=" + formatTextForLog(reason));
                try
                {
                    InvokeDiscard(request, reason);
                }
                catch (Exception exception)
                {
                    logWarning("startup_background_task discard cleanup failed name=" + request.Name + " message=" + exception.Message);
                }
            }
        }
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        else
        {
            NotifyIdleChanged();
        }
    }

    internal void Report(string name, string status, long elapsedMs, bool failed, string detail)
    {
        lock (syncRoot)
        {
            Metric metric = GetOrCreateMetricUnsafe(name);
            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                metric.QueuedCount++;
                metric.Reason = detail ?? string.Empty;
                metric.Lane = GetLane(name);
                metric.LastStatus = "queued";
                return;
            }
            if (string.Equals(status, "start", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                metric.StartedCount++;
                if (string.IsNullOrWhiteSpace(metric.Lane))
                {
                    metric.Lane = GetLane(name);
                }
                metric.LastStatus = "running";
                metric.LastDetail = detail ?? string.Empty;
                return;
            }
            if (failed)
            {
                metric.FailedCount++;
            }
            else
            {
                metric.CompletedCount++;
            }
            metric.LastStatus = string.IsNullOrWhiteSpace(status) ? (failed ? "failed" : "done") : status;
            if (string.IsNullOrWhiteSpace(metric.Lane))
            {
                metric.Lane = GetLane(name);
            }
            metric.LastElapsedMs = Math.Max(0L, elapsedMs);
            metric.TotalElapsedMs += Math.Max(0L, elapsedMs);
            metric.LastDetail = detail ?? string.Empty;
        }
    }

    internal string DescribeWaitState()
    {
        lock (syncRoot)
        {
            return "queueCount=" + queue.Count
                + " runningCount=" + runningCount
                + " requiredRunningCount=" + runningRequiredCount
                + " postInitializationRunningCount=" + runningPostInitializationCount;
        }
    }

    internal string BuildSummaryLog(long elapsedMs)
    {
        List<Metric> snapshot;
        lock (syncRoot)
        {
            snapshot = [.. metrics.Values
                .OrderBy(metric => metric.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneMetric)];
        }
        long queued = snapshot.Sum(metric => metric.QueuedCount);
        long startedCount = snapshot.Sum(metric => metric.StartedCount);
        long completed = snapshot.Sum(metric => metric.CompletedCount);
        long failed = snapshot.Sum(metric => metric.FailedCount);
        string taskSummary = snapshot.Count == 0
            ? "(none)"
            : string.Join(";", snapshot.Select(FormatMetric));
        return "startup_background_summary elapsedMs=" + elapsedMs
            + " queued=" + queued
            + " started=" + startedCount
            + " completed=" + completed
            + " failed=" + failed
            + " tasks=" + taskSummary;
    }

    private void TryStartWorkers()
    {
        while (true)
        {
            Request request = null;
            int laneRunningCount = 0;
            int totalRunningCount = 0;
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    if (!started)
                    {
                        return;
                    }
                    int index = FindNextRequestIndexUnsafe();
                    if (index < 0)
                    {
                        return;
                    }
                    request = queue[index];
                    queue.RemoveAt(index);
                    if (request.Reservation != null
                        && currentReservationByName.TryGetValue(request.Name, out StartupBackgroundTaskReservation currentReservation)
                        && ReferenceEquals(currentReservation, request.Reservation))
                    {
                        currentReservationByName.Remove(request.Name);
                    }
                    request.ReservationState = request.Reservation == null
                        ? 0
                        : ReservationStateRunning;
                    idleRevision++;
                    runningCount++;
                    if (request.IsPostInitialization)
                    {
                        runningPostInitializationCount++;
                        if (IsPostInitializationIdleOnly(request.Name))
                        {
                            runningPostInitializationIdleOnlyCount++;
                        }
                    }
                    else
                    {
                        runningRequiredCount++;
                    }
                    runningCountByLane.TryGetValue(request.Lane, out int runningInLane);
                    runningCountByLane[request.Lane] = runningInLane + 1;
                    laneRunningCount = runningInLane + 1;
                    totalRunningCount = runningCount;
                }
            }
            StartWorker(request, laneRunningCount, totalRunningCount);
        }
    }

    private int FindNextRequestIndexUnsafe()
    {
        int index = -1;
        int bestPriority = int.MaxValue;
        long bestVersion = long.MaxValue;
        bool shutdownRequestedSnapshot = isShutdownRequested();
        for (int i = 0; i < queue.Count; i++)
        {
            Request candidate = queue[i];
            if (!shutdownRequestedSnapshot && !AreDependenciesCompletedUnsafe(candidate.Dependency))
            {
                continue;
            }
            if (!CanStartRequestUnsafe(candidate))
            {
                continue;
            }
            if (candidate.Priority < bestPriority || (candidate.Priority == bestPriority && candidate.Version < bestVersion))
            {
                index = i;
                bestPriority = candidate.Priority;
                bestVersion = candidate.Version;
            }
        }
        return index;
    }

    private bool CanStartInLaneUnsafe(string lane)
    {
        string normalizedLane = string.IsNullOrWhiteSpace(lane) ? "default" : lane;
        runningCountByLane.TryGetValue(normalizedLane, out int runningInLane);
        return runningInLane < GetLaneConcurrency(normalizedLane);
    }

    private bool CanStartRequestUnsafe(Request request)
    {
        if (runningCount >= GetTotalConcurrency()
            || !CanStartInLaneUnsafe(request.Lane))
        {
            return false;
        }
        if (!request.IsPostInitialization)
        {
            if (runningPostInitializationIdleOnlyCount > 0)
            {
                return false;
            }
            return runningRequiredCount < GetTotalConcurrency();
        }
        if (runningPostInitializationCount >= GetPostInitializationConcurrency())
        {
            return false;
        }
        if (IsPostInitializationIdleOnly(request.Name)
            && (!postInitializationSchedulingComplete
                || !requiredInitializationSchedulingComplete
                || runningRequiredCount > 0
                || queue.Any(candidate => !candidate.IsPostInitialization)))
        {
            return false;
        }
        return true;
    }

    private void StartWorker(Request request, int laneRunningCount, int totalRunningCount)
    {
        Task.Run(async delegate
        {
            var stopwatch = Stopwatch.StartNew();
            logInfo("startup_background_task start name=" + request.Name + " version=" + request.Version + " generation=" + request.Generation + " reason=" + request.Reason + " kind=" + FormatRequestKind(request.IsPostInitialization) + " dependency=" + (request.Dependency ?? "(none)") + " lane=" + request.Lane + " laneRunning=" + laneRunningCount + " totalRunning=" + totalRunningCount);
            RecordStarted(request);
            try
            {
                await request.Work().ConfigureAwait(false);
                stopwatch.Stop();
                logInfo("startup_background_task done name=" + request.Name + " version=" + request.Version + " generation=" + request.Generation + " reason=" + request.Reason + " kind=" + FormatRequestKind(request.IsPostInitialization) + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                RecordCompleted(request, "done", stopwatch.ElapsedMilliseconds, failed: false, detail: "reason=" + request.Reason);
                StartupMemoryPressureService.LogCheckpoint(logInfo, "startup_background_task", request.Name + "_done");
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                logWarning("startup_background_task failed name=" + request.Name + " version=" + request.Version + " generation=" + request.Generation + " reason=" + request.Reason + " kind=" + FormatRequestKind(request.IsPostInitialization) + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + exception.Message);
                RecordCompleted(request, "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: exception.Message);
                StartupMemoryPressureService.LogCheckpoint(logInfo, "startup_background_task", request.Name + "_failed");
            }
            finally
            {
                lock (progressSynchronization)
                {
                    lock (syncRoot)
                    {
                        if (request.Reservation != null)
                        {
                            request.ReservationState = ReservationStateTerminal;
                        }
                        if (!completedRequestVersionByName.TryGetValue(request.Name, out long completedVersion)
                            || request.Version > completedVersion)
                        {
                            completedRequestVersionByName[request.Name] = request.Version;
                        }
                        runningCount = Math.Max(0, runningCount - 1);
                        if (request.IsPostInitialization)
                        {
                            runningPostInitializationCount = Math.Max(0, runningPostInitializationCount - 1);
                            if (IsPostInitializationIdleOnly(request.Name))
                            {
                                runningPostInitializationIdleOnlyCount = Math.Max(0, runningPostInitializationIdleOnlyCount - 1);
                            }
                        }
                        else
                        {
                            runningRequiredCount = Math.Max(0, runningRequiredCount - 1);
                        }
                        idleRevision++;
                        if (!string.IsNullOrWhiteSpace(request.Lane)
                            && runningCountByLane.TryGetValue(request.Lane, out int runningInLane))
                        {
                            runningInLane = Math.Max(0, runningInLane - 1);
                            if (runningInLane == 0)
                            {
                                runningCountByLane.Remove(request.Lane);
                            }
                            else
                            {
                                runningCountByLane[request.Lane] = runningInLane;
                            }
                        }
                    }
                }
                TryStartWorkers();
                NotifyIdleChanged();
            }
        }).ObserveFault("StartupBackgroundTaskScheduler");
    }

    private void RecordStarted(Request request)
    {
        lock (syncRoot)
        {
            if (request.Generation != generation)
            {
                return;
            }
            Metric metric = GetOrCreateMetricUnsafe(request.Name);
            metric.StartedCount++;
            metric.LastStatus = "running";
        }
    }

    private void RecordCompleted(Request request, string status, long elapsedMs, bool failed, string detail)
    {
        lock (syncRoot)
        {
            if (request.Generation != generation)
            {
                return;
            }
            Metric metric = GetOrCreateMetricUnsafe(request.Name);
            if (failed)
            {
                metric.FailedCount++;
            }
            else
            {
                metric.CompletedCount++;
            }
            metric.LastStatus = status;
            metric.Lane = string.IsNullOrWhiteSpace(metric.Lane) ? GetLane(request.Name) : metric.Lane;
            metric.LastElapsedMs = Math.Max(0L, elapsedMs);
            metric.TotalElapsedMs += Math.Max(0L, elapsedMs);
            metric.LastDetail = detail ?? string.Empty;
        }
    }

    private void RecordQueuedUnsafe(string name, string reason, string dependency, string lane)
    {
        Metric metric = GetOrCreateMetricUnsafe(name);
        metric.QueuedCount++;
        metric.Reason = reason ?? string.Empty;
        metric.Dependency = dependency ?? string.Empty;
        metric.Lane = lane ?? string.Empty;
        metric.LastStatus = "queued";
    }

    private void RecordDiscardedUnsafe(Request request, string reason)
    {
        if (request.Reservation != null)
        {
            request.ReservationState = ReservationStateTerminal;
        }
        if (!completedRequestVersionByName.TryGetValue(request.Name, out long completedVersion)
            || request.Version > completedVersion)
        {
            completedRequestVersionByName[request.Name] = request.Version;
        }
        Metric metric = GetOrCreateMetricUnsafe(request.Name);
        metric.CompletedCount++;
        metric.LastStatus = "discarded";
        metric.LastElapsedMs = 0L;
        metric.LastDetail = "shutdown_requested";
        if (string.IsNullOrWhiteSpace(metric.Reason))
        {
            metric.Reason = request.Reason ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(metric.Dependency))
        {
            metric.Dependency = request.Dependency ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(metric.Lane))
        {
            metric.Lane = request.Lane ?? GetLane(request.Name);
        }
    }

    private void InvokeDiscard(Request request, string reason)
    {
        try
        {
            request.Discard?.Invoke(reason);
        }
        catch (Exception exception)
        {
            try
            {
                logWarning("startup_background_task discard cleanup failed name="
                    + request.Name
                    + " message="
                    + exception.Message);
            }
            catch
            {
                // Diagnostic failure must not escape the discard boundary.
            }
        }
    }

    private Metric GetOrCreateMetricUnsafe(string name)
    {
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        if (!metrics.TryGetValue(normalizedName, out Metric metric))
        {
            metric = new Metric { Name = normalizedName };
            metrics[normalizedName] = metric;
        }
        return metric;
    }

    private bool AreDependenciesCompletedUnsafe(string dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency))
        {
            return true;
        }
        string[] dependencies = dependency.Split([','], StringSplitOptions.RemoveEmptyEntries);
        foreach (string item in dependencies)
        {
            string dependencyName = item.Trim();
            if (dependencyName.Length > 0
                && (!latestRequestVersionByName.TryGetValue(dependencyName, out long latestVersion)
                    || !completedRequestVersionByName.TryGetValue(dependencyName, out long completedVersion)
                    || completedVersion < latestVersion))
            {
                return false;
            }
        }
        return true;
    }

    private void NotifyIdleChanged()
    {
        try
        {
            long notificationGeneration;
            long notificationRevision;
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    notificationGeneration = generation;
                    notificationRevision = idleRevision;
                }
            }
            schedulerIdleChanged(notificationGeneration, notificationRevision);
        }
        catch (Exception exception)
        {
            try
            {
                logWarning("startup_background_task idle notification failed message=" + exception.Message);
            }
            catch
            {
                // Diagnostic failure must not escape idle accounting.
            }
        }
    }

    private static bool IsRequiredForShutdown(string name)
    {
        return string.Equals(name, "score_hydration_deferred", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostInitializationTask(string name)
    {
        return string.Equals(name, "lr2_song_db_sync_enrollment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "external_table_catalog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_library_index_prewarm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_virtual_order_prewarm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "library_folder_tree_refresh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "post_initialize_gc", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("beatoraja_bmt_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostInitializationIdleOnly(string name)
    {
        return string.Equals(name, "post_initialize_gc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatRequestKind(bool isPostInitialization)
    {
        return isPostInitialization ? "post_initialization" : "required";
    }

    private static int GetPriority(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(name, "playlist_library_index_prewarm", StringComparison.OrdinalIgnoreCase)) return 15;
        if (string.Equals(name, "library_folder_tree_refresh", StringComparison.OrdinalIgnoreCase)) return 16;
        if (string.Equals(name, "playlist_virtual_order_prewarm", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)) return 20;
        if (string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)) return 30;
        if (string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase)) return 40;
        if (string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)) return 50;
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)) return 55;
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase)) return 60;
        if (string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase)) return 70;
        if (string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase)) return 90;
        return 100;
    }

    private static string GetLane(string name)
    {
        string baseLane = GetBaseLane(name);
        return IsPostInitializationTask(name)
            ? "post_initialization_" + baseLane
            : baseLane;
    }

    private static string GetBaseLane(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)) return "read_hydration";
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)) return "maintenance_hydration";
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase)) return "playlist_followup";
        if (string.Equals(name, "library_folder_tree_refresh", StringComparison.OrdinalIgnoreCase)) return "folder_tree_refresh";
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase)) return "dependent_maintenance";
        return "default";
    }

    private static int GetLaneConcurrency(string lane)
    {
        return string.Equals(lane, "read_hydration", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static int GetTotalConcurrency() => 4;

    private static int GetPostInitializationConcurrency() => 1;

    private bool IsRequiredWorkIdleUnsafe()
    {
        return runningRequiredCount == 0 && !queue.Any(request => !request.IsPostInitialization);
    }

    private bool IsFullyIdleUnsafe()
    {
        return queue.Count == 0 && runningCount == 0;
    }

    private static Metric CloneMetric(Metric metric)
    {
        return new Metric
        {
            Name = metric.Name,
            Reason = metric.Reason,
            Dependency = metric.Dependency,
            Lane = metric.Lane,
            QueuedCount = metric.QueuedCount,
            StartedCount = metric.StartedCount,
            CompletedCount = metric.CompletedCount,
            FailedCount = metric.FailedCount,
            TotalElapsedMs = metric.TotalElapsedMs,
            LastElapsedMs = metric.LastElapsedMs,
            LastStatus = metric.LastStatus,
            LastDetail = metric.LastDetail
        };
    }

    private static string FormatMetric(Metric metric)
    {
        return Sanitize(metric.Name)
            + "{queued=" + metric.QueuedCount
            + ",started=" + metric.StartedCount
            + ",completed=" + metric.CompletedCount
            + ",failed=" + metric.FailedCount
            + ",lastStatus=" + Sanitize(metric.LastStatus)
            + ",lastMs=" + metric.LastElapsedMs
            + ",totalMs=" + metric.TotalElapsedMs
            + ",reason=" + Sanitize(metric.Reason)
            + ",dependency=" + Sanitize(metric.Dependency)
            + ",lane=" + Sanitize(metric.Lane)
            + ",detail=" + Sanitize(metric.LastDetail)
            + "}";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value
            .Replace(Environment.NewLine, " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", ",")
            .Replace("{", "(")
            .Replace("}", ")")
            .Replace(" ", "_");
    }
}
