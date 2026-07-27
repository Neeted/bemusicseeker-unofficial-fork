using System;
using System.Runtime.Serialization;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdaterLaunchReceipt
{
    private readonly Action abort;
    private readonly Action proceed;
    private readonly object syncRoot = new();
    private bool decisionPublished;

    internal UpdaterLaunchReceipt(Action abort = null, Action proceed = null)
    {
        this.abort = abort;
        this.proceed = proceed;
    }

    internal void Abort()
    {
        lock (syncRoot)
        {
            if (decisionPublished)
            {
                return;
            }

            try
            {
                abort?.Invoke();
                decisionPublished = true;
            }
            catch
            {
                // Keep the receipt unpublished so a later failure path can retry the abort.
            }
        }
    }

    internal void Proceed()
    {
        lock (syncRoot)
        {
            if (decisionPublished)
            {
                return;
            }

            try
            {
                proceed?.Invoke();
                decisionPublished = true;
            }
            catch
            {
                try
                {
                    abort?.Invoke();
                    decisionPublished = true;
                }
                catch
                {
                    // Leave the receipt unpublished so the owning workflow can retry the abort.
                }
                throw;
            }
        }
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
