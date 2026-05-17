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
        var queue = new ExternalPlaylistImportQueue();
        var first = new Uri("https://example.com/first");
        var second = new Uri("https://example.com/second");
        var third = new Uri("https://example.com/third");

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
    public void EnqueueRange_StartsOnlyOneDrainAndDequeuesInFifoOrder()
    {
        var queue = new ExternalPlaylistImportQueue();
        var first = new Uri("https://example.com/range-first");
        var second = new Uri("https://example.com/range-second");
        var third = new Uri("https://example.com/range-third");

        Assert.IsTrue(queue.EnqueueRange(new[] { first, second }));
        Assert.AreEqual(2, queue.PendingCount);
        Assert.IsFalse(queue.EnqueueRange(new[] { third }));
        Assert.AreEqual(3, queue.PendingCount);

        Assert.IsTrue(queue.TryDequeue(out Uri dequeuedFirst));
        Assert.AreEqual(first, dequeuedFirst);
        Assert.AreEqual(2, queue.PendingCount);
        Assert.IsTrue(queue.TryDequeue(out Uri dequeuedSecond));
        Assert.AreEqual(second, dequeuedSecond);
        Assert.IsTrue(queue.TryDequeue(out Uri dequeuedThird));
        Assert.AreEqual(third, dequeuedThird);
        Assert.AreEqual(0, queue.PendingCount);
        Assert.IsFalse(queue.TryDequeue(out _));
    }

    [TestMethod]
    public void EnqueueRange_IgnoresEmptyAndNullEntries()
    {
        var queue = new ExternalPlaylistImportQueue();
        var valid = new Uri("https://example.com/valid");

        Assert.IsFalse(queue.EnqueueRange(null));
        Assert.IsFalse(queue.EnqueueRange([]));
        Assert.IsTrue(queue.EnqueueRange(new[] { null, valid, null }));
        Assert.AreEqual(1, queue.PendingCount);
        Assert.IsTrue(queue.TryDequeue(out Uri dequeued));
        Assert.AreEqual(valid, dequeued);
    }

    [TestMethod]
    public void ResolveExternalPlaylistImportQueueProgressTotal_UsesCompletedActiveAndPendingCounts()
    {
        Assert.AreEqual(2, MainWindowViewModel.ResolveExternalPlaylistImportQueueProgressTotal(0, hasActiveImport: true, pendingCount: 1));
        Assert.AreEqual(2, MainWindowViewModel.ResolveExternalPlaylistImportQueueProgressTotal(1, hasActiveImport: false, pendingCount: 1));
        Assert.AreEqual(3, MainWindowViewModel.ResolveExternalPlaylistImportQueueProgressTotal(1, hasActiveImport: true, pendingCount: 1));
        Assert.AreEqual(3, MainWindowViewModel.ResolveExternalPlaylistImportQueueProgressTotal(3, hasActiveImport: false, pendingCount: 0));
        Assert.AreEqual(0, MainWindowViewModel.ResolveExternalPlaylistImportQueueProgressTotal(-1, hasActiveImport: false, pendingCount: -1));
    }

    [TestMethod]
    public void QueueSummary_CountsOutcomesByKind()
    {
        var imported = new Uri("https://example.com/imported");
        var skipped = new Uri("https://example.com/skipped");
        var failed = new Uri("https://example.com/failed");
        var summary = new ExternalPlaylistImportQueueSummary(new[]
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
