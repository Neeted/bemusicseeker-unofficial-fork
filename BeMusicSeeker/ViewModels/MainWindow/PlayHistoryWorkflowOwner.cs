using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryWorkflowOwner
{
    private long keywordQueuedRevision;
    private int keywordActiveCount;
    private long displayTargetQueuedRevision;
    private int displayTargetActiveCount;

    internal PlayHistoryPresentationState PresentationState { get; } = new();

    internal PlayHistoryReadCache ReadCache { get; } = new();

    internal PlayHistoryViewRequest ActiveRequest { get; private set; }

    internal PlayHistoryViewRequest BeginRequest(
        PlayHistoryPeriodRequest periodRequest,
        string keywordIdentity,
        string displayTargetIdentity,
        long displayTargetRevision,
        Action<PlayHistoryViewRequest> activateRequest)
    {
        CancellationTokenSource previousCancellation;
        CancellationTokenSource failedCancellation = null;
        PlayHistoryViewRequest request;
        ExceptionDispatchInfo activationException = null;
        lock (PresentationState.SyncRoot)
        {
            previousCancellation = InvalidateRequestUnsafe();
            PresentationState.RequestCancellation = new CancellationTokenSource();
            PresentationState.CurrentKeywordIdentity = keywordIdentity ?? string.Empty;
            PresentationState.CurrentDisplayTargetIdentity = displayTargetIdentity ?? string.Empty;
            PresentationState.DisplayTargetRevision = displayTargetRevision;
            request = new PlayHistoryViewRequest(
                periodRequest ?? PlayHistoryPeriodRequest.All(),
                PresentationState.RequestGeneration,
                PresentationState.KeywordRevision,
                displayTargetRevision);
            ActiveRequest = request;
            try
            {
                activateRequest?.Invoke(request);
            }
            catch (Exception ex)
            {
                activationException = ExceptionDispatchInfo.Capture(ex);
                failedCancellation = InvalidateRequestUnsafe();
            }
        }
        Cancel(previousCancellation);
        Cancel(failedCancellation);
        if (activationException != null)
        {
            activationException.Throw();
        }
        return request;
    }

    internal long RegisterRequest(
        PlayHistoryPeriodRequest periodRequest,
        string keywordIdentity,
        string displayTargetIdentity,
        long displayTargetRevision,
        Action<PlayHistoryViewRequest> activateRequest)
    {
        return BeginRequest(periodRequest, keywordIdentity, displayTargetIdentity, displayTargetRevision, activateRequest).RequestId;
    }

    internal bool IsCurrentRequest(long requestId)
    {
        lock (PresentationState.SyncRoot)
        {
            return IsCurrentRequestUnsafe(requestId);
        }
    }

    internal PlayHistoryViewRequest SnapshotActiveRequest()
    {
        lock (PresentationState.SyncRoot)
        {
            return ActiveRequest;
        }
    }

    internal long CurrentRequestId
    {
        get
        {
            lock (PresentationState.SyncRoot)
            {
                return PresentationState.RequestGeneration;
            }
        }
    }

    internal long KeywordRevision => Interlocked.Read(ref PresentationState.KeywordRevision);

    internal long DisplayTargetRevision => Interlocked.Read(ref PresentationState.DisplayTargetRevision);

    internal long UpdateKeywordIdentity(string identity, bool advanceRevision)
    {
        lock (PresentationState.SyncRoot)
        {
            PresentationState.CurrentKeywordIdentity = identity ?? string.Empty;
            return advanceRevision
                ? Interlocked.Increment(ref PresentationState.KeywordRevision)
                : Interlocked.Read(ref PresentationState.KeywordRevision);
        }
    }

    internal long AdvanceDisplayTargetRevision(string identity)
    {
        lock (PresentationState.SyncRoot)
        {
            long revision = Interlocked.Increment(ref PresentationState.DisplayTargetRevision);
            PresentationState.CurrentDisplayTargetIdentity = identity ?? string.Empty;
            return revision;
        }
    }

    internal void SetDisplayTargetIdentity(string identity)
    {
        lock (PresentationState.SyncRoot)
        {
            PresentationState.CurrentDisplayTargetIdentity = identity ?? string.Empty;
        }
    }

    internal bool TryQueueKeywordRevision(long revision)
    {
        return TryReserveRevision(ref keywordQueuedRevision, revision);
    }

    internal bool TryQueueDisplayTargetRevision(long revision)
    {
        return TryReserveRevision(ref displayTargetQueuedRevision, revision);
    }

    internal void BeginKeywordRefresh()
    {
        Interlocked.Increment(ref keywordActiveCount);
    }

    internal void CompleteKeywordRefresh(long revision)
    {
        DecrementActiveCount(ref keywordActiveCount, "keyword");
        Interlocked.CompareExchange(ref keywordQueuedRevision, 0L, revision);
    }

    internal void BeginDisplayTargetRefresh()
    {
        Interlocked.Increment(ref displayTargetActiveCount);
    }

    internal void CompleteDisplayTargetRefresh(long revision)
    {
        DecrementActiveCount(ref displayTargetActiveCount, "display-target");
        Interlocked.CompareExchange(ref displayTargetQueuedRevision, 0L, revision);
    }

    internal void ClearQueuedRefreshes()
    {
        Interlocked.Exchange(ref keywordQueuedRevision, 0L);
        Interlocked.Exchange(ref displayTargetQueuedRevision, 0L);
    }

    internal bool AreRefreshQueuesIdle =>
        Interlocked.Read(ref keywordQueuedRevision) == 0L
        && Volatile.Read(ref keywordActiveCount) == 0
        && Interlocked.Read(ref displayTargetQueuedRevision) == 0L
        && Volatile.Read(ref displayTargetActiveCount) == 0;

    internal string DescribeRefreshQueues()
    {
        return "keywordQueuedRevision=" + Interlocked.Read(ref keywordQueuedRevision)
            + " keywordActiveCount=" + Volatile.Read(ref keywordActiveCount)
            + " displayTargetQueuedRevision=" + Interlocked.Read(ref displayTargetQueuedRevision)
            + " displayTargetActiveCount=" + Volatile.Read(ref displayTargetActiveCount);
    }

    private static bool TryReserveRevision(ref long queuedRevision, long revision)
    {
        while (true)
        {
            long current = Interlocked.Read(ref queuedRevision);
            if (current >= revision)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref queuedRevision, revision, current) == current)
            {
                return true;
            }
        }
    }

    private static void DecrementActiveCount(ref int activeCount, string queueName)
    {
        while (true)
        {
            int current = Volatile.Read(ref activeCount);
            if (current <= 0)
            {
                throw new InvalidOperationException("The " + queueName + " refresh queue was completed without an active worker.");
            }
            if (Interlocked.CompareExchange(ref activeCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    internal CancellationToken GetCancellationToken(long requestId)
    {
        lock (PresentationState.SyncRoot)
        {
            return IsCurrentRequestUnsafe(requestId)
                ? PresentationState.RequestCancellation?.Token ?? new CancellationToken(canceled: true)
                : new CancellationToken(canceled: true);
        }
    }

    internal void Deactivate(Action deactivateSelection = null)
    {
        CancellationTokenSource cancellation;
        ExceptionDispatchInfo deactivationException = null;
        lock (PresentationState.SyncRoot)
        {
            cancellation = InvalidateRequestUnsafe();
            try
            {
                deactivateSelection?.Invoke();
            }
            catch (Exception ex)
            {
                deactivationException = ExceptionDispatchInfo.Capture(ex);
            }
        }
        Cancel(cancellation);
        if (deactivationException != null)
        {
            deactivationException.Throw();
        }
    }

    private bool IsCurrentRequestUnsafe(long requestId)
    {
        return requestId > 0
            && PresentationState.RequestGeneration == requestId
            && ActiveRequest?.RequestId == requestId
            && PresentationState.RequestCancellation?.IsCancellationRequested != true;
    }

    private CancellationTokenSource InvalidateRequestUnsafe()
    {
        PresentationState.RequestGeneration++;
        CancellationTokenSource cancellation = PresentationState.RequestCancellation;
        PresentationState.RequestCancellation = null;
        ActiveRequest = null;
        return cancellation;
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class PlayHistoryViewRequest
{
    internal PlayHistoryViewRequest(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        long keywordFilterRevision = 0L,
        long displayTargetRevision = 0L)
    {
        PeriodRequest = periodRequest ?? PlayHistoryPeriodRequest.All();
        RequestId = requestId;
        KeywordFilterRevision = keywordFilterRevision;
        DisplayTargetRevision = displayTargetRevision;
    }

    internal PlayHistoryPeriodRequest PeriodRequest { get; }

    internal long RequestId { get; }

    internal long KeywordFilterRevision { get; }

    internal long DisplayTargetRevision { get; }
}
