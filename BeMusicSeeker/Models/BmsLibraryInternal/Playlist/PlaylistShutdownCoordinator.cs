using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// playlist feature 全体の shutdown signal を所有します。
/// </summary>
internal sealed class PlaylistShutdownCoordinator
{
    private int requested;

    internal bool IsRequested => Volatile.Read(ref requested) != 0;

    /// <summary>
    /// Publishes the shutdown signal and runs each playlist shutdown callback once.
    /// </summary>
    /// <param name="reason">Diagnostic reason for the shutdown request.</param>
    /// <param name="cancelStartupReadiness">Callback that closes startup readiness admission.</param>
    /// <param name="stopBmtOutput">Callback that stops BMT output work.</param>
    /// <param name="clearHydrationPending">Callback that clears pending hydration work.</param>
    /// <param name="log">Callback that records the shutdown request.</param>
    /// <returns><see langword="true"/> for the first request; otherwise <see langword="false"/>.</returns>
    /// <exception cref="Exception">Rethrows the first callback failure after all callbacks have run.</exception>
    internal bool Request(
        string reason,
        Action cancelStartupReadiness,
        Action stopBmtOutput,
        Action clearHydrationPending,
        Action<string> log)
    {
        if (Interlocked.Exchange(ref requested, 1) != 0)
        {
            return false;
        }

        ExceptionDispatchInfo firstFailure = null;
        InvokeIsolated(cancelStartupReadiness, ref firstFailure);
        InvokeIsolated(stopBmtOutput, ref firstFailure);
        InvokeIsolated(clearHydrationPending, ref firstFailure);
        InvokeIsolated(
            () => log?.Invoke("shutdown requested reason=" + FormatTextForLog(reason)),
            ref firstFailure);
        if (firstFailure != null)
        {
            firstFailure.Throw();
        }
        return true;
    }

    private static void InvokeIsolated(Action callback, ref ExceptionDispatchInfo firstFailure)
    {
        try
        {
            callback?.Invoke();
        }
        catch (Exception exception)
        {
            firstFailure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }

    private static string FormatTextForLog(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }
}
