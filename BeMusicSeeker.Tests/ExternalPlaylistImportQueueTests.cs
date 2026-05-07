using System;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalPlaylistImportQueueTests
{
    [TestMethod]
    public void Enqueue_StartsOnlyOneDrainAndDequeuesInFifoOrder()
    {
        ExternalPlaylistImportQueue queue = new ExternalPlaylistImportQueue();
        Uri first = new Uri("https://example.com/first");
        Uri second = new Uri("https://example.com/second");
        Uri third = new Uri("https://example.com/third");

        Assert.IsTrue(queue.Enqueue(first));
        Assert.IsFalse(queue.Enqueue(second));

        Assert.IsTrue(queue.TryDequeue(out Uri dequeuedFirst));
        Assert.AreEqual(first, dequeuedFirst);

        Assert.IsFalse(queue.Enqueue(third));

        Assert.IsTrue(queue.TryDequeue(out Uri dequeuedSecond));
        Assert.AreEqual(second, dequeuedSecond);
        Assert.IsTrue(queue.TryDequeue(out Uri dequeuedThird));
        Assert.AreEqual(third, dequeuedThird);
        Assert.IsFalse(queue.TryDequeue(out _));

        Assert.IsTrue(queue.Enqueue(first));
    }

    [TestMethod]
    public void QueueSummary_CountsOutcomesByKind()
    {
        Uri imported = new Uri("https://example.com/imported");
        Uri skipped = new Uri("https://example.com/skipped");
        Uri failed = new Uri("https://example.com/failed");
        ExternalPlaylistImportQueueSummary summary = new ExternalPlaylistImportQueueSummary(new[]
        {
            ExternalPlaylistImportOutcome.Imported(imported, "Imported"),
            ExternalPlaylistImportOutcome.SkippedDuplicateName(skipped, "Skipped", new InvalidOperationException("duplicate")),
            ExternalPlaylistImportOutcome.Failed(failed, new InvalidOperationException("failed"))
        });

        Assert.AreEqual(1, summary.ImportedCount);
        Assert.AreEqual(1, summary.SkippedDuplicateNameCount);
        Assert.AreEqual(1, summary.FailedCount);
        Assert.IsTrue(summary.HasNotifiableItems);
        Assert.AreEqual(skipped, summary.SkippedDuplicateNameOutcomes.Single().Uri);
        Assert.AreEqual(failed, summary.FailedOutcomes.Single().Uri);
    }
}
