using System;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalPlaylistImportQueueTests
{
    [TestMethod]
    public void ResolveExternalPlaylistImportQueueProgressTotal_UsesCompletedActiveAndPendingCounts()
    {
        Assert.AreEqual(2, PlaylistWorkspaceViewModel.ResolveExternalPlaylistImportQueueProgressTotal(0, hasActiveImport: true, pendingCount: 1));
        Assert.AreEqual(2, PlaylistWorkspaceViewModel.ResolveExternalPlaylistImportQueueProgressTotal(1, hasActiveImport: false, pendingCount: 1));
        Assert.AreEqual(3, PlaylistWorkspaceViewModel.ResolveExternalPlaylistImportQueueProgressTotal(1, hasActiveImport: true, pendingCount: 1));
        Assert.AreEqual(3, PlaylistWorkspaceViewModel.ResolveExternalPlaylistImportQueueProgressTotal(3, hasActiveImport: false, pendingCount: 0));
        Assert.AreEqual(0, PlaylistWorkspaceViewModel.ResolveExternalPlaylistImportQueueProgressTotal(-1, hasActiveImport: false, pendingCount: -1));
    }

    [TestMethod]
    public void QueueSummary_CountsOutcomesByKind()
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
