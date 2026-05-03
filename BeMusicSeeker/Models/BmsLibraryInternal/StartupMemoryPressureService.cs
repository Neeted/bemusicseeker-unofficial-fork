using System;
using System.Diagnostics;
using System.Runtime;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class StartupMemoryPressureService
{
    internal const long DefaultPrivateBytesThreshold = 2L * 1024L * 1024L * 1024L;

    internal const long DefaultManagedBytesThreshold = 512L * 1024L * 1024L;

    public static StartupMemorySnapshot CaptureSnapshot()
    {
        using Process process = Process.GetCurrentProcess();
        return new StartupMemorySnapshot(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static void LogCheckpoint(Action<string> log, string phase, string point)
    {
        if (log == null)
        {
            return;
        }
        log(BuildCheckpointLog(phase, point, CaptureSnapshot()));
    }

    public static StartupMemoryCleanupResult CleanupIfNeeded(
        Action<string> log,
        string reason,
        bool force,
        Func<StartupMemorySnapshot> captureSnapshot = null,
        Action collect = null,
        long privateBytesThreshold = DefaultPrivateBytesThreshold,
        long managedBytesThreshold = DefaultManagedBytesThreshold)
    {
        Func<StartupMemorySnapshot> capture = captureSnapshot ?? CaptureSnapshot;
        StartupMemorySnapshot before = capture();
        bool shouldCollect = ShouldCleanup(force, before, privateBytesThreshold, managedBytesThreshold);
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (shouldCollect)
        {
            if (collect != null)
            {
                collect();
            }
            else
            {
                CollectAndCompact();
            }
        }
        stopwatch.Stop();
        StartupMemorySnapshot after = capture();
        StartupMemoryCleanupResult result = new StartupMemoryCleanupResult(before, after, shouldCollect, stopwatch.ElapsedMilliseconds);
        log?.Invoke(BuildCleanupLog(reason, force, shouldCollect, result));
        return result;
    }

    internal static bool ShouldCleanup(bool force, StartupMemorySnapshot snapshot, long privateBytesThreshold = DefaultPrivateBytesThreshold, long managedBytesThreshold = DefaultManagedBytesThreshold)
    {
        if (force)
        {
            return true;
        }
        if (snapshot == null)
        {
            return false;
        }
        return snapshot.PrivateBytes >= privateBytesThreshold || snapshot.ManagedBytes >= managedBytesThreshold;
    }

    internal static string BuildCheckpointLog(string phase, string point, StartupMemorySnapshot snapshot)
    {
        StartupMemorySnapshot safeSnapshot = snapshot ?? new StartupMemorySnapshot(0L, 0L, 0L, 0, 0, 0);
        return "startup_memory_checkpoint"
            + " phase=" + (string.IsNullOrWhiteSpace(phase) ? "unknown" : phase)
            + " point=" + (string.IsNullOrWhiteSpace(point) ? "unknown" : point)
            + " privateBytes=" + safeSnapshot.PrivateBytes
            + " workingSet=" + safeSnapshot.WorkingSet
            + " managedBytes=" + safeSnapshot.ManagedBytes
            + " gen0=" + safeSnapshot.Gen0Count
            + " gen1=" + safeSnapshot.Gen1Count
            + " gen2=" + safeSnapshot.Gen2Count;
    }

    internal static string BuildCleanupLog(string reason, bool force, bool collected, StartupMemoryCleanupResult result)
    {
        StartupMemoryCleanupResult safeResult = result ?? new StartupMemoryCleanupResult(
            new StartupMemorySnapshot(0L, 0L, 0L, 0, 0, 0),
            new StartupMemorySnapshot(0L, 0L, 0L, 0, 0, 0),
            collected: false,
            elapsedMs: 0L);
        return "startup_memory_cleanup"
            + " reason=" + (string.IsNullOrWhiteSpace(reason) ? "unknown" : reason)
            + " forced=" + force.ToString().ToLowerInvariant()
            + " collected=" + collected.ToString().ToLowerInvariant()
            + " compactLoh=" + collected.ToString().ToLowerInvariant()
            + " elapsedMs=" + safeResult.ElapsedMs
            + " privateBefore=" + safeResult.Before.PrivateBytes
            + " privateAfter=" + safeResult.After.PrivateBytes
            + " workingSetBefore=" + safeResult.Before.WorkingSet
            + " workingSetAfter=" + safeResult.After.WorkingSet
            + " managedBefore=" + safeResult.Before.ManagedBytes
            + " managedAfter=" + safeResult.After.ManagedBytes
            + " gen0Before=" + safeResult.Before.Gen0Count
            + " gen0After=" + safeResult.After.Gen0Count
            + " gen1Before=" + safeResult.Before.Gen1Count
            + " gen1After=" + safeResult.After.Gen1Count
            + " gen2Before=" + safeResult.Before.Gen2Count
            + " gen2After=" + safeResult.After.Gen2Count;
    }

    private static void CollectAndCompact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}

internal sealed class StartupMemorySnapshot
{
    public StartupMemorySnapshot(long privateBytes, long workingSet, long managedBytes, int gen0Count, int gen1Count, int gen2Count)
    {
        PrivateBytes = Math.Max(0L, privateBytes);
        WorkingSet = Math.Max(0L, workingSet);
        ManagedBytes = Math.Max(0L, managedBytes);
        Gen0Count = Math.Max(0, gen0Count);
        Gen1Count = Math.Max(0, gen1Count);
        Gen2Count = Math.Max(0, gen2Count);
    }

    public long PrivateBytes { get; }

    public long WorkingSet { get; }

    public long ManagedBytes { get; }

    public int Gen0Count { get; }

    public int Gen1Count { get; }

    public int Gen2Count { get; }
}

internal sealed class StartupMemoryCleanupResult
{
    public StartupMemoryCleanupResult(StartupMemorySnapshot before, StartupMemorySnapshot after, bool collected, long elapsedMs)
    {
        Before = before ?? new StartupMemorySnapshot(0L, 0L, 0L, 0, 0, 0);
        After = after ?? new StartupMemorySnapshot(0L, 0L, 0L, 0, 0, 0);
        Collected = collected;
        ElapsedMs = Math.Max(0L, elapsedMs);
    }

    public StartupMemorySnapshot Before { get; }

    public StartupMemorySnapshot After { get; }

    public bool Collected { get; }

    public long ElapsedMs { get; }
}
