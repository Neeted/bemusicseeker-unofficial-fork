using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
[TestCategory("contract")]
public sealed class Net10PerformanceCorpusContractTests
{
    /// <summary>
    /// producerが渡した時刻とroute・interaction・generationを出力へ保持し、reader時刻への置換や相関情報の欠落を検出する。
    /// </summary>
    [TestMethod]
    public async Task BatchWriter_PreservesProducerTimestampAndCorrelationSchema()
    {
        var events = new ConcurrentQueue<Net10PerformanceEvent>();
        var writer = new Net10PerformanceBatchWriter(8, 4, events.Enqueue);
        var interaction = PerformanceInteraction.Existing(
            "playlist_summary",
            interactionId: 19,
            generation: 23);
        var timestamp = new DateTime(2026, 7, 31, 12, 34, 56, DateTimeKind.Local);

        Assert.IsTrue(writer.TryWrite(
            new Net10PerformanceEvent(
                timestamp,
                interaction,
                "ui_applied",
                "rows=100")));
        await writer.StopAsync();

        Net10PerformanceEvent captured = events.Single();
        Assert.AreEqual(timestamp, captured.Timestamp);
        Assert.AreEqual(
            "net10_perf route=playlist_summary interactionId=19 generation=23 stage=ui_applied rows=100",
            captured.FormatMessage());
    }

    /// <summary>
    /// 実sinkを保持して容量を埋めてもproducerが投入/overflow結果を返し、拒否件数を一回の診断へ集約して停止時までに出力することを確かめる。
    /// </summary>
    [TestMethod]
    public async Task BoundedWriter_AggregatesOverflowWithoutBlockingProducer()
    {
        var sinkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSink = new ManualResetEventSlim();
        var events = new ConcurrentQueue<Net10PerformanceEvent>();
        int sinkCalls = 0;
        var writer = new Net10PerformanceBatchWriter(
            capacity: 1,
            batchSize: 1,
            performanceEvent =>
            {
                if (Interlocked.Increment(ref sinkCalls) == 1)
                {
                    sinkEntered.TrySetResult();
                    releaseSink.Wait();
                }
                events.Enqueue(performanceEvent);
            });
        var interaction = PerformanceInteraction.Existing(
            "normal_library",
            interactionId: 42,
            generation: 7);

        Exception? primaryFailure = null;
        try
        {
            Assert.IsTrue(writer.TryWrite(new Net10PerformanceEvent(
                DateTime.Now,
                interaction,
                "owner_started",
                null)));
            await sinkEntered.Task;
            Assert.IsTrue(writer.TryWrite(new Net10PerformanceEvent(
                DateTime.Now,
                interaction,
                "ui_queued",
                null)));
            Assert.IsFalse(writer.TryWrite(new Net10PerformanceEvent(
                DateTime.Now,
                interaction,
                "ui_applied",
                null)));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            releaseSink.Set();
            try
            {
                await writer.StopAsync();
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["WriterSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        Assert.AreEqual(2, events.Count(item => item.Stage != "queue_overflow"));
        Assert.AreEqual(1, events.Count(item =>
            item.Stage == "queue_overflow"
            && string.Equals(item.Fields, "droppedCount=1", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 実sink到達を保持してproducerと別threadで処理されることを観測し、投入処理上でsinkを同期実行する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task BatchWriter_InvokesSinkOnBackgroundReader()
    {
        var sinkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSink = new ManualResetEventSlim();
        int producerThreadId = Environment.CurrentManagedThreadId;
        int sinkThreadId = producerThreadId;
        var writer = new Net10PerformanceBatchWriter(
            4,
            2,
            _ =>
            {
                sinkThreadId = Environment.CurrentManagedThreadId;
                sinkEntered.TrySetResult();
                releaseSink.Wait();
            });

        Exception? primaryFailure = null;
        try
        {
            Assert.IsTrue(writer.TryWrite(new Net10PerformanceEvent(
                DateTime.Now,
                PerformanceInteraction.Existing("startup", 1, 1),
                "owner_started",
                null)));
            await sinkEntered.Task;
            Assert.AreNotEqual(producerThreadId, sinkThreadId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            releaseSink.Set();
            try
            {
                await writer.StopAsync();
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["WriterSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>
    /// sinkのI/O失敗でもproducerの投入を受理し、停止終端後に失敗件数を残して故障を黙って捨てる誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task BatchWriter_RecordsSinkFailuresWithoutFailingProducers()
    {
        var writer = new Net10PerformanceBatchWriter(
            4,
            2,
            _ => throw new IOException("sink failed"));

        Assert.IsTrue(writer.TryWrite(new Net10PerformanceEvent(
            DateTime.Now,
            PerformanceInteraction.Existing("startup", 1, 1),
            "owner_started",
            null)));

        await writer.StopAsync();

        Assert.AreEqual(1L, writer.SinkFailureCount);
    }

    /// <summary>
    /// routeを段階的に切り替えてもinteractionとgenerationを保持し、同じ操作の計測を別操作へ分断する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void InteractionRouteTransition_PreservesCorrelationIdentity()
    {
        var startup = PerformanceInteraction.Existing(
            "startup",
            interactionId: 31,
            generation: 4);

        PerformanceInteraction library = startup.ForRoute("startup_library");
        PerformanceInteraction estimation = library.ForRoute("install_estimation");

        Assert.AreEqual("startup_library", library.Route);
        Assert.AreEqual(31L, library.InteractionId);
        Assert.AreEqual(4L, library.Generation);
        Assert.AreEqual("install_estimation", estimation.Route);
        Assert.AreEqual(31L, estimation.InteractionId);
        Assert.AreEqual(4L, estimation.Generation);
    }

}
