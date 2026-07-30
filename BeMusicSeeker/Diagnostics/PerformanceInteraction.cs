using System;
using System.Threading;

namespace BeMusicSeeker.Diagnostics;

/// <summary>
/// Correlates one current-runtime performance interaction across owner and UI stages.
/// </summary>
internal readonly record struct PerformanceInteraction(
    string Route,
    long InteractionId,
    long Generation)
{
    private static long nextInteractionId;

    internal static PerformanceInteraction Start(string route, long generation = 0L)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            throw new ArgumentException("A performance interaction route is required.", nameof(route));
        }
        return new PerformanceInteraction(
            route,
            Interlocked.Increment(ref nextInteractionId),
            generation);
    }

    internal static PerformanceInteraction Existing(
        string route,
        long interactionId,
        long generation = 0L)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            throw new ArgumentException("A performance interaction route is required.", nameof(route));
        }
        if (interactionId <= 0L)
        {
            throw new ArgumentOutOfRangeException(nameof(interactionId));
        }
        return new PerformanceInteraction(route, interactionId, generation);
    }

    internal PerformanceInteraction ForRoute(string route, long? generation = null)
    {
        return Existing(route, InteractionId, generation ?? Generation);
    }
}
