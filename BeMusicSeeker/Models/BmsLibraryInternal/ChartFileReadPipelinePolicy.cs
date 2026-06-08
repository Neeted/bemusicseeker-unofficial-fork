using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ChartFileReadPipelinePolicy
{
    public const int DefaultMaxReaderDegree = 2;

    public static int ResolveReaderDegree(
        int processorCount,
        int targetCount,
        int maxReaderDegree = DefaultMaxReaderDegree)
    {
        if (targetCount <= 1 || processorCount < 6)
        {
            return 1;
        }

        return Math.Max(1, Math.Min(Math.Min(maxReaderDegree, targetCount), DefaultMaxReaderDegree));
    }

    public static int ResolveCpuWorkerDegree(int processorCount, int maxWorkerDegree)
    {
        int availableWorkerCount = Math.Max(1, processorCount - 1);
        return Math.Max(1, Math.Min(availableWorkerCount, Math.Max(1, maxWorkerDegree)));
    }

    public static int ResolveReadQueueCapacity(int workerDegree, int readerDegree)
    {
        return Math.Max(1, Math.Max(1, workerDegree) * Math.Max(2, Math.Max(1, readerDegree) * 2));
    }
}
