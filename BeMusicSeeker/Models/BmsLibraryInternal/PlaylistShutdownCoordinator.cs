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

    internal bool Request(
        string reason,
        Action stopBmtOutput,
        Action clearHydrationPending,
        Action<string> log)
    {
        if (Interlocked.Exchange(ref requested, 1) != 0)
        {
            return false;
        }

        Exception firstException = null;
        try
        {
            stopBmtOutput?.Invoke();
        }
        catch (Exception exception)
        {
            firstException = exception;
        }
        try
        {
            clearHydrationPending?.Invoke();
        }
        catch (Exception exception)
        {
            firstException ??= exception;
        }
        try
        {
            log?.Invoke("shutdown requested reason=" + FormatTextForLog(reason));
        }
        catch (Exception exception)
        {
            firstException ??= exception;
        }
        if (firstException != null)
        {
            ExceptionDispatchInfo.Capture(firstException).Throw();
        }
        return true;
    }

    private static string FormatTextForLog(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }
}
