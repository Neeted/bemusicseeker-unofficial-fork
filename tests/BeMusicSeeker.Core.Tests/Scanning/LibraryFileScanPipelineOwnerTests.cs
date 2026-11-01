using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryFileScanPipelineOwnerTests
{
    /// <summary>両形式の集合交換後も先に捕捉したsnapshotを保持し、現在snapshotと状態件数だけを新しい集合へ更新することを確認します。</summary>
    [TestMethod]
    public void CatalogChartCollectionSnapshot_CapturesBothKindsAndRemainsStableAcrossReplacement()
    {
        ChartFile firstBms = (ChartTestValues.Empty() with { Path = "first.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile secondBms = (ChartTestValues.Empty() with { Path = "second.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile firstBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = "first.bmson", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        ChartFile secondBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = "second.bmson", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        ChartFile replacementBms = (ChartTestValues.Empty() with { Path = "replacement.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile replacementBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = "replacement.bmson", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        var owner = new CatalogOwnedCollectionOwner();

        CatalogChartCollectionSnapshot captured = owner.ReplaceChartsAndCaptureSnapshot(
            [firstBms, secondBms],
            [firstBmson, secondBmson]);
        owner.ReplaceCharts([replacementBms], null, replaceBmson: false);
        owner.ReplaceCharts(null, [replacementBmson], replaceBms: false);

        CollectionAssert.AreEqual(new[] { firstBms, secondBms }.Select(chart => chart.Path).ToArray(), captured.BmsRows.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(new[] { firstBmson, secondBmson }.Select(chart => chart.Path).ToArray(), captured.BmsonRows.Select(chart => chart.Path).ToArray());

        CatalogChartCollectionSnapshot current = owner.CaptureSnapshot();
        CatalogChartCollectionStateSnapshot state = owner.CaptureStateSnapshot();
        Assert.AreEqual(current.CatalogRevision, state.CatalogRevision);
        Assert.AreEqual(current.BmsRows.Count, state.BmsRowCount);
        Assert.AreEqual(current.BmsonRows.Count, state.BmsonRowCount);
        CollectionAssert.AreEqual(new[] { replacementBms }.Select(chart => chart.Path).ToArray(), current.BmsRows.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(new[] { replacementBmson }.Select(chart => chart.Path).ToArray(), current.BmsonRows.Select(chart => chart.Path).ToArray());
    }

}
