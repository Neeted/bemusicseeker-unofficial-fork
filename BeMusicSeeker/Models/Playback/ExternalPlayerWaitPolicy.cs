using System;
using System.Diagnostics;
using System.Threading;

namespace BeMusicSeeker.Models;

internal sealed class ExternalPlayerWaitPolicy
{
    internal static ExternalPlayerWaitPolicy Default { get; } = new(TimeSpan.FromSeconds(10));

    private readonly TimeSpan timeout;

    internal ExternalPlayerWaitPolicy(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        this.timeout = timeout;
    }

    internal bool WaitUntil(
        Func<bool> completed,
        Func<bool> aborted,
        Action attempt,
        string timeoutMessage,
        int pollMilliseconds = 10)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(attempt);
        var stopwatch = Stopwatch.StartNew();
        while (!completed())
        {
            if (aborted?.Invoke() == true)
            {
                return false;
            }
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(timeoutMessage);
            }
            attempt();
            if (pollMilliseconds > 0)
            {
                Thread.Sleep(pollMilliseconds);
            }
            else
            {
                Thread.Yield();
            }
        }
        return true;
    }

    internal void WaitForProcessExit(
        Func<bool> hasExited,
        Action forceTerminate,
        string timeoutMessage)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(forceTerminate);
        var stopwatch = Stopwatch.StartNew();
        var forceTerminateAt = TimeSpan.FromTicks(timeout.Ticks / 2);
        int pollMilliseconds = Math.Max(
            1,
            Math.Min(100, (int)Math.Ceiling(timeout.TotalMilliseconds / 20d)));
        bool forceTerminateAttempted = false;
        while (!hasExited())
        {
            if (!forceTerminateAttempted && stopwatch.Elapsed >= forceTerminateAt)
            {
                forceTerminateAttempted = true;
                try
                {
                    forceTerminate();
                    if (hasExited())
                    {
                        return;
                    }
                }
                catch
                {
                }
            }
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(timeoutMessage);
            }
            Thread.Sleep(pollMilliseconds);
        }
    }
}
