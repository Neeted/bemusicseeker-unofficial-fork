namespace BeMusicSeeker.Models;

/// <summary>
/// Captures the current startup background scheduler backlog.
/// </summary>
internal readonly record struct StartupBackgroundWorkSnapshot(
    int QueuedCount,
    int RunningCount)
{
    internal int BacklogCount => QueuedCount + RunningCount;
}
