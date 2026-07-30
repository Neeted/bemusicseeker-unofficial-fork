using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using NLog;
using Ribbit.Logging;

namespace BeMusicSeeker.Diagnostics;

/// <summary>
/// Queues aggregate current-.NET performance stages without blocking producer lanes on file I/O.
/// </summary>
internal static class Net10PerformanceLog
{
    private const int QueueCapacity = 1024;
    private const int BatchSize = 64;
    private static readonly Logger logger = NLogWrapper.GetLogger("InstallPerformance.Net10");
    private static readonly object lifecycleSyncRoot = new();
    private static Net10PerformanceBatchWriter writer;
    private static Task stopTask = Task.CompletedTask;

    internal static bool IsEnabled => Volatile.Read(ref writer)?.IsAccepting == true;

    internal static void Start()
    {
        if (!CommandLineSwitches.IsInfoLoggingEnabled
            || logger?.IsInfoEnabled != true)
        {
            return;
        }

        lock (lifecycleSyncRoot)
        {
            if (writer != null || !stopTask.IsCompleted)
            {
                return;
            }

            writer = new Net10PerformanceBatchWriter(
                QueueCapacity,
                BatchSize,
                WriteLogEvent);
            stopTask = Task.CompletedTask;
        }
    }

    internal static void Write(
        in PerformanceInteraction interaction,
        string stage,
        string fields = null)
    {
        Volatile.Read(ref writer)?.TryWrite(
            new Net10PerformanceEvent(
                DateTime.Now,
                interaction,
                stage,
                fields));
    }

    internal static Task StopAsync()
    {
        lock (lifecycleSyncRoot)
        {
            if (writer == null)
            {
                return stopTask;
            }

            Net10PerformanceBatchWriter current = writer;
            writer = null;
            stopTask = StopWriterAsync(current);
            return stopTask;
        }
    }

    private static async Task StopWriterAsync(Net10PerformanceBatchWriter current)
    {
        await current.StopAsync().ConfigureAwait(false);
        if (current.SinkFailureCount > 0L)
        {
            NLogWrapper.FileLogger?.Warn(
                "net10_performance_sink_failure count=" + current.SinkFailureCount);
        }
    }

    private static void WriteLogEvent(Net10PerformanceEvent performanceEvent)
    {
        var logEvent = new LogEventInfo(
            LogLevel.Info,
            logger.Name,
            performanceEvent.FormatMessage())
        {
            TimeStamp = performanceEvent.Timestamp,
        };
        logger.Log(logEvent);
    }
}

internal readonly struct Net10PerformanceEvent
{
    internal Net10PerformanceEvent(
        DateTime timestamp,
        in PerformanceInteraction interaction,
        string stage,
        string fields)
    {
        Timestamp = timestamp;
        Interaction = interaction;
        Stage = stage;
        Fields = fields;
    }

    internal DateTime Timestamp { get; }

    internal PerformanceInteraction Interaction { get; }

    internal string Stage { get; }

    internal string Fields { get; }

    internal string FormatMessage()
    {
        string message = "net10_perf"
            + " route=" + Interaction.Route
            + " interactionId=" + Interaction.InteractionId
            + " generation=" + Interaction.Generation
            + " stage=" + (Stage ?? "unknown");
        if (!string.IsNullOrWhiteSpace(Fields))
        {
            message += " " + Fields;
        }
        return message;
    }

    internal static Net10PerformanceEvent Dropped(DateTime timestamp, long count)
    {
        return new Net10PerformanceEvent(
            timestamp,
            default,
            "queue_overflow",
            "droppedCount=" + count);
    }
}

/// <summary>
/// Owns the bounded producer queue and the single background performance-log writer.
/// </summary>
internal sealed class Net10PerformanceBatchWriter
{
    private readonly Channel<Net10PerformanceEvent> channel;
    private readonly int batchSize;
    private readonly Action<Net10PerformanceEvent> sink;
    private readonly Task readerTask;
    private readonly object stopSyncRoot = new();
    private int accepting = 1;
    private int activeProducers;
    private long droppedCount;
    private long sinkFailureCount;
    private Task stopTask;

    internal Net10PerformanceBatchWriter(
        int capacity,
        int batchSize,
        Action<Net10PerformanceEvent> sink)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        this.batchSize = batchSize;
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        channel = Channel.CreateBounded<Net10PerformanceEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        readerTask = Task.Run(ReadAsync);
    }

    internal bool IsAccepting => Volatile.Read(ref accepting) != 0;

    internal long SinkFailureCount => Interlocked.Read(ref sinkFailureCount);

    internal bool TryWrite(in Net10PerformanceEvent performanceEvent)
    {
        if (!IsAccepting)
        {
            return false;
        }

        Interlocked.Increment(ref activeProducers);
        try
        {
            if (!IsAccepting)
            {
                return false;
            }
            if (channel.Writer.TryWrite(performanceEvent))
            {
                return true;
            }
            Interlocked.Increment(ref droppedCount);
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref activeProducers);
        }
    }

    internal Task StopAsync()
    {
        lock (stopSyncRoot)
        {
            return stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref accepting, 0);
        while (Volatile.Read(ref activeProducers) != 0)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        channel.Writer.TryComplete();
        await readerTask.ConfigureAwait(false);
    }

    private async Task ReadAsync()
    {
        ChannelReader<Net10PerformanceEvent> reader = channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            int count = 0;
            while (count < batchSize && reader.TryRead(out Net10PerformanceEvent performanceEvent))
            {
                TryWriteToSink(performanceEvent);
                count++;
            }
            FlushDroppedCount();
        }
        FlushDroppedCount();
    }

    private void FlushDroppedCount()
    {
        long count = Interlocked.Exchange(ref droppedCount, 0L);
        if (count > 0L)
        {
            TryWriteToSink(Net10PerformanceEvent.Dropped(DateTime.Now, count));
        }
    }

    private void TryWriteToSink(in Net10PerformanceEvent performanceEvent)
    {
        try
        {
            sink(performanceEvent);
        }
        catch
        {
            Interlocked.Increment(ref sinkFailureCount);
        }
    }
}
