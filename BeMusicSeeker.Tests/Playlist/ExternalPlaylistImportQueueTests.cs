using System;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalPlaylistImportQueueTests
{
    [TestMethod]
    public void OneImportRequestSummary_CountsSuccessDuplicateAndFailure()
    {
        var imported = new Uri("https://example.com/imported");
        var skipped = new Uri("https://example.com/skipped");
        var failed = new Uri("https://example.com/failed");
        var summary = new ExternalPlaylistImportQueueSummary(
        [
            ExternalPlaylistImportOutcome.Imported(imported, "Imported"),
            ExternalPlaylistImportOutcome.SkippedDuplicateName(skipped, "Skipped", new InvalidOperationException("duplicate")),
            ExternalPlaylistImportOutcome.Failed(failed, new InvalidOperationException("failed"))
        ]);

        Assert.AreEqual(1, summary.ImportedCount);
        Assert.AreEqual(1, summary.SkippedDuplicateNameCount);
        Assert.AreEqual(1, summary.FailedCount);
        Assert.IsTrue(summary.HasNotifiableItems);
        Assert.AreEqual(skipped, summary.SkippedDuplicateNameOutcomes.Single().Uri);
        Assert.AreEqual(failed, summary.FailedOutcomes.Single().Uri);
    }
}
