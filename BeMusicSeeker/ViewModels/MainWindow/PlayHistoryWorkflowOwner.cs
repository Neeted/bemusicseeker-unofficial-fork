using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryWorkflowOwner
{
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
