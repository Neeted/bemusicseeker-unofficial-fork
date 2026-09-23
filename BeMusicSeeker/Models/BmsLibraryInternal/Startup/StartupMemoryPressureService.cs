using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class StartupMemoryPressureService
{
    public static StartupMemorySnapshot CaptureSnapshot()
    {
        using var process = Process.GetCurrentProcess();
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
}

internal sealed class StartupMemorySnapshot(long privateBytes, long workingSet, long managedBytes, int gen0Count, int gen1Count, int gen2Count)
{
    public long PrivateBytes { get; } = Math.Max(0L, privateBytes);

    public long WorkingSet { get; } = Math.Max(0L, workingSet);

    public long ManagedBytes { get; } = Math.Max(0L, managedBytes);

    public int Gen0Count { get; } = Math.Max(0, gen0Count);

    public int Gen1Count { get; } = Math.Max(0, gen1Count);

    public int Gen2Count { get; } = Math.Max(0, gen2Count);
}
