using System;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalPlaylistImportQueueTests
{
    /// <summary>一要求の成功・同URL省略・同名省略・失敗を別件数と通知対象へ分け、原因とURIを保って同URLを失敗や同名へ混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void OneImportRequestSummary_CountsSuccessDuplicateAndFailure()
    {
        var imported = new Uri("https://example.com/imported");
        var skipped = new Uri("https://example.com/skipped");
        var existing = new Uri("https://example.com/existing");
        var failed = new Uri("https://example.com/failed");
        var summary = new ExternalPlaylistImportQueueSummary(
        [
            ExternalPlaylistImportOutcome.Imported(imported, "Imported"),
            ExternalPlaylistImportOutcome.SkippedDuplicateUrl(existing, "Existing"),
            ExternalPlaylistImportOutcome.SkippedDuplicateName(skipped, "Skipped", new InvalidOperationException("duplicate")),
            ExternalPlaylistImportOutcome.Failed(failed, new InvalidOperationException("failed"))
        ]);

        Assert.AreEqual(1, summary.ImportedCount);
        Assert.AreEqual(1, summary.SkippedDuplicateNameCount);
        Assert.AreEqual(1, summary.SkippedDuplicateUrlCount);
        Assert.AreEqual(1, summary.FailedCount);
        Assert.IsTrue(summary.HasNotifiableItems);
        Assert.AreEqual(skipped, summary.SkippedDuplicateNameOutcomes.Single().Uri);
        Assert.AreEqual(existing, summary.SkippedDuplicateUrlOutcomes.Single().Uri);
        Assert.IsNull(summary.SkippedDuplicateUrlOutcomes.Single().Exception);
        Assert.AreEqual(failed, summary.FailedOutcomes.Single().Uri);

        var urlOnly = new ExternalPlaylistImportQueueSummary([ExternalPlaylistImportOutcome.SkippedDuplicateUrl(existing, "Existing")]);
        Assert.IsTrue(urlOnly.HasNotifiableItems, "URL省略だけでも完了集計の通知対象です。");
    }
}
