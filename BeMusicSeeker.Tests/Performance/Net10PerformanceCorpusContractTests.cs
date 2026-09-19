using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    [TestMethod]
    public void ConfiguredCorpus_UsesRequestedSeedAndScaleWithStableGolden()
    {
        int seed = Net10PerformanceCorpus.GetConfiguredSeed();
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            IReadOnlyList<Net10PerformanceCorpusRow> rows =
                Net10PerformanceCorpus.CreateRows(scale, seed);
            string fingerprint = Net10PerformanceCorpus.CreateFingerprint(rows);

            Assert.AreEqual(Net10PerformanceCorpus.GetRowCount(scale), rows.Count);
            if (seed == Net10PerformanceCorpus.DefaultSeed)
            {
                Assert.AreEqual(GetDefaultGoldenFingerprint(scale), fingerprint);
            }
            else
            {
                Assert.AreEqual(
                    fingerprint,
                    Net10PerformanceCorpus.CreateFingerprint(
                        Net10PerformanceCorpus.CreateRows(scale, seed)));
            }
        }
    }

    [TestMethod]
    public void DifferentSeedRows_ProduceDifferentFingerprint()
    {
        string first = Net10PerformanceCorpus.CreateFingerprint(
            Net10PerformanceCorpus.CreateRows(Net10PerformanceCorpusScale.Small, 1));
        string second = Net10PerformanceCorpus.CreateFingerprint(
            Net10PerformanceCorpus.CreateRows(Net10PerformanceCorpusScale.Small, 2));

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void TemporaryWorkspace_IsRemovedAfterCompletion()
    {
        string capturedPath = Net10PerformanceCorpus.WithTemporaryWorkspace(path =>
        {
            File.WriteAllText(Path.Combine(path, "fixture.txt"), "fixture");
            return path;
        });

        Assert.IsFalse(Directory.Exists(capturedPath));
    }

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

    [TestMethod]
    public async Task BoundedWriter_AggregatesOverflowWithoutBlockingProducer()
    {
        using var sinkEntered = new ManualResetEventSlim();
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
                    sinkEntered.Set();
                    releaseSink.Wait();
                }
                events.Enqueue(performanceEvent);
            });
        var interaction = PerformanceInteraction.Existing(
            "normal_library",
            interactionId: 42,
            generation: 7);

        Assert.IsTrue(writer.TryWrite(new Net10PerformanceEvent(
            DateTime.Now,
            interaction,
            "owner_started",
            null)));
        Assert.IsTrue(sinkEntered.Wait(TimeSpan.FromSeconds(5)));
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
        releaseSink.Set();
        await writer.StopAsync();

        Assert.AreEqual(2, events.Count(item => item.Stage != "queue_overflow"));
        Assert.AreEqual(1, events.Count(item =>
            item.Stage == "queue_overflow"
            && string.Equals(item.Fields, "droppedCount=1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BatchWriter_InvokesSinkOnBackgroundReader()
    {
        using var sinkEntered = new ManualResetEventSlim();
        using var releaseSink = new ManualResetEventSlim();
        int producerThreadId = Environment.CurrentManagedThreadId;
        int sinkThreadId = producerThreadId;
        var writer = new Net10PerformanceBatchWriter(
            4,
            2,
            _ =>
            {
                sinkThreadId = Environment.CurrentManagedThreadId;
                sinkEntered.Set();
                releaseSink.Wait();
            });

        Assert.IsTrue(writer.TryWrite(new Net10PerformanceEvent(
            DateTime.Now,
            PerformanceInteraction.Existing("startup", 1, 1),
            "owner_started",
            null)));
        Assert.IsTrue(sinkEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreNotEqual(producerThreadId, sinkThreadId);
        releaseSink.Set();
        await writer.StopAsync();
    }

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

    private static string GetDefaultGoldenFingerprint(Net10PerformanceCorpusScale scale) => scale switch
    {
        Net10PerformanceCorpusScale.Small =>
            "51123503FA90BE44DE780C23D1F74795BE9785B66054869568C71341B727E0D5",
        Net10PerformanceCorpusScale.Medium =>
            "3CB33510F211A727BA3494A3F47F0ABE91D65666B06C247D5F5569B5448CA43A",
        Net10PerformanceCorpusScale.Large =>
            "F31E0CDECFB662740F3558F54BA02086C444D1BD33F9B35F1F65B3AB330C2CAC",
        _ => throw new ArgumentOutOfRangeException(nameof(scale))
    };
}
