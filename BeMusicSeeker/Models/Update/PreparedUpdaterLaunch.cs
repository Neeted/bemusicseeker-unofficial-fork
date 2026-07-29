using System;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdaterLaunchReceipt
{
    private readonly Action abort;
    private readonly Action proceed;
    private readonly object syncRoot = new();
    private DecisionState decisionState;

    internal UpdaterLaunchReceipt(Action abort = null, Action proceed = null)
    {
        this.abort = abort;
        this.proceed = proceed;
    }

    internal void Abort()
    {
        lock (syncRoot)
        {
            if (decisionState != DecisionState.Available)
            {
                return;
            }
            decisionState = DecisionState.PublishingAbort;
        }
        try
        {
            abort?.Invoke();
            lock (syncRoot)
            {
                decisionState = DecisionState.Published;
            }
        }
        catch
        {
            lock (syncRoot)
            {
                decisionState = DecisionState.Available;
            }
            throw;
        }
    }

    internal void Proceed()
    {
        lock (syncRoot)
        {
            if (decisionState != DecisionState.Available)
            {
                return;
            }
            decisionState = DecisionState.PublishingProceed;
        }

        try
        {
            proceed?.Invoke();
            lock (syncRoot)
            {
                decisionState = DecisionState.Published;
            }
        }
        catch (Exception proceedFailure)
        {
            try
            {
                abort?.Invoke();
                lock (syncRoot)
                {
                    decisionState = DecisionState.Published;
                }
            }
            catch (Exception abortFailure)
            {
                lock (syncRoot)
                {
                    decisionState = DecisionState.Available;
                }
                throw new AggregateException(
                    "Updater proceed failed and the compensating abort also failed.",
                    proceedFailure,
                    abortFailure);
            }
            ExceptionDispatchInfo.Capture(proceedFailure).Throw();
            throw;
        }
    }

    private enum DecisionState
    {
        Available,
        PublishingAbort,
        PublishingProceed,
        Published
    }
}

[Serializable]
internal sealed class UpdaterLaunchFailureException : InvalidOperationException
{
    public UpdaterLaunchFailureException()
    {
    }

    public UpdaterLaunchFailureException(string message)
        : base(message)
    {
    }

    public UpdaterLaunchFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private UpdaterLaunchFailureException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}

internal interface IPreparedUpdaterLaunch
{
    UpdaterLaunchReceipt Start();
}

/// <summary>
/// Opaque updater launch prepared after package validation and updater copy.
/// </summary>
internal sealed class PreparedUpdaterLaunch : IPreparedUpdaterLaunch
{
    private readonly Func<UpdaterLaunchReceipt> start;

    internal PreparedUpdaterLaunch(Func<UpdaterLaunchReceipt> start)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public UpdaterLaunchReceipt Start()
    {
        return start();
    }
}
