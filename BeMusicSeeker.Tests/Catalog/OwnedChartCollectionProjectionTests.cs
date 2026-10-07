using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionProjectionTests
{
    [TestMethod]
    public void FromStorageRows_BuildsBmsAndBmsonOwnedChartSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('d', 64),
            RawTitle = "bmson title"
        };

        List<ChartFile> snapshot = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]))
            .CreateSnapshot(includeWarningSnapshot: false, includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        ChartFile bmsChart = snapshot.Single(chart => chart.Kind == ChartFileKind.Bms);
        ChartFile bmsonChart = snapshot.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(bmsFile.Path, bmsChart.Path);
        Assert.AreEqual(bmsFile.Md5, bmsChart.Md5);
        Assert.AreEqual(bmsFile.Sha256, bmsChart.Sha256);
        Assert.AreSame(bmsFile.Token, bmsChart.Token);
        Assert.AreEqual(bmsonSong.Path, bmsonChart.Path);
        Assert.AreEqual(bmsonSong.Md5, bmsonChart.Md5);
        Assert.AreEqual(bmsonSong.Sha256, bmsonChart.Sha256);
        Assert.AreSame(bmsonSong.Token, bmsonChart.Token);
    }

    /// <summary>未所持identityと同じexact keyの重複だけを除外し、別caseの行は保持します。</summary>
    [TestMethod]
    public void FromStorageRows_FiltersPathlessMd5lessAndExactDuplicateRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        ChartFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"), new string('1', 64));
        ChartFile duplicateBms = CreateFile("22222222222222222222222222222222", pathfulBms.Path, new string('2', 64));
        ChartFile pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ChartFile pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");
        ChartFile md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        ChartFile duplicateBmson = CreateBmsonSong(pathfulBms.Path.ToUpperInvariant(), "33333333333333333333333333333333");

        List<ChartFile> snapshot = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([pathfulBms, pathlessBms, md5lessBms, duplicateBms], [pathfulBmson, pathlessBmson, md5lessBmson, duplicateBmson]), out OwnedChartStorageRowFilterSummary filterSummary)
            .CreateSnapshot(includeWarningSnapshot: false, includeScoreSnapshot: false);

        Assert.AreEqual(3, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, pathfulBms.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, pathfulBmson.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, duplicateBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, pathlessBms.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, pathlessBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, md5lessBms.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, md5lessBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, duplicateBms.Token)));
        Assert.AreEqual(1, filterSummary.PathlessBmsCount);
        Assert.AreEqual(1, filterSummary.PathlessBmsonCount);
        Assert.AreEqual(1, filterSummary.Md5lessBmsCount);
        Assert.AreEqual(1, filterSummary.Md5lessBmsonCount);
        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
        Assert.AreEqual(0, filterSummary.DuplicatePathBmsonCount);
    }

    [TestMethod]
    public void ChartStorageTargetSet_FromChartsRejectsInvalidBmsAndBmsonIdentities()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        ChartFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"), new string('1', 64));
        ChartFile pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ChartFile pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");
        ChartFile md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);

        var targets = ChartStorageTargetSet.FromCharts(ChartTestValues.Combine([pathfulBms], [pathfulBmson]));

        CollectionAssert.AreEqual(new[] { pathfulBms.Path }, targets.BmsCharts.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(pathfulBms.Md5, targets.BmsCharts.Single().Md5);
        CollectionAssert.AreEqual(new[] { pathfulBmson.Path }, targets.BmsonCharts.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(pathfulBmson.Md5, targets.BmsonCharts.Single().Md5);
        Assert.AreEqual(2, targets.Charts.Count);
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromCharts([(pathfulBms with { Path = null })]));
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromCharts([(pathfulBms with { Md5 = null })]));
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromCharts([(pathfulBmson with { Path = null })]));
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromCharts([(pathfulBmson with { Md5 = null })]));
    }

    [TestMethod]
    public void CreateSnapshot_MatchesDirectStorageRowProjection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('d', 64),
            RawTitle = "bmson title"
        };

        List<ChartFile> ownedSnapshot = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]))
            .CreateSnapshot(includeWarningSnapshot: false, includeScoreSnapshot: false);
        var projectionSnapshot = ChartTestValues.Combine([bmsFile], [bmsonSong]).ToList();

        AssertChartSnapshotParity(projectionSnapshot, ownedSnapshot);
    }

    [TestMethod]
    public void CurrentApplicationKeepsCapturedValuesAndReprojectsCurrentIndexes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        ChartFile capture = state.CreateSnapshot().Single();
        state.ApplyPathChanges([new LibraryChartPathChange { Chart = capture, OldPath = capture.Path, NewPath = newPath }]);
        bmsFile = state.ResolveCurrentChart(LibraryChartRef.FromChartFile(capture)) with
        { Md5 = "cccccccccccccccccccccccccccccccc", Sha256 = new string('d', 64) };
        Assert.IsTrue(state.ApplyCurrentChartValue(bmsFile));
        Assert.AreNotEqual(newPath, capture.Path);
        Assert.AreNotEqual(bmsFile.Md5, capture.Md5);

        ChartFile snapshot = state.CreateSnapshot(
            includeWarningSnapshot: false,
            includeScoreSnapshot: false).Single();

        Assert.AreEqual(newPath, snapshot.Path);
        Assert.AreEqual(bmsFile.Md5, snapshot.Md5);
        Assert.AreEqual(bmsFile.Sha256, snapshot.Sha256);
        Assert.AreSame(bmsFile.Token, snapshot.Token);
    }

    [TestMethod]
    public void CreateCollectionView_ReturnsCommonValuesAndExactPathLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        ChartFile pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, pathlessBms], [bmsonSong, pathlessBmson]));

        OwnedChartCollectionView view = state.CreateCollectionView();

        Assert.AreEqual(2, view.Count);
        Assert.AreEqual(2, view.OwnerPathCount);
        Assert.AreEqual(bmsFile.Path, view.BmsCharts.Single().Path);
        Assert.IsNotNull(view.BmsCharts.Single().Token);
        CollectionAssert.AreEqual(new[] { bmsonSong.Path }, view.BmsonCharts.Select(chart => chart.Path).ToArray());
        Assert.IsTrue(view.ContainsOwnerPath(bmsFile.Path));
        Assert.IsTrue(view.ContainsOwnerPath(bmsonSong.Path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBms.Path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBmson.Path));
    }

    [TestMethod]
    public void CreateNormalLibrarySourceChartView_SortsBmsonRowsAndExcludesPathlessRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile lateBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "z.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile earlyBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "a.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        ChartFile pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, pathlessBms], [lateBmson, pathlessBmson, earlyBmson]));

        OwnedChartCollectionView view = state.CreateNormalLibrarySourceChartView();

        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(bmsFile.Path, view.BmsCharts.Single().Path);
        Assert.IsNotNull(view.BmsCharts.Single().Token);
        CollectionAssert.AreEqual(new[] { earlyBmson.Path, lateBmson.Path }, view.BmsonCharts.Select(chart => chart.Path).ToArray());
        Assert.IsTrue(view.ContainsOwnerPath(lateBmson.Path));
        Assert.IsTrue(view.ContainsOwnerPath(earlyBmson.Path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBms.Path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBmson.Path));
    }

    [TestMethod]
    public void CreateFileScanRemovedCharts_UsesOwnedCurrentOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        ChartFile deletedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "deleted.bms"));
        ChartFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty);
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        ChartFile oldBmson = CreateBmsonSong(bmsonPath, "dddddddddddddddddddddddddddddddd");
        ChartFile newBmson = CreateBmsonSong(bmsonPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ChartFile pathlessBmson = CreateBmsonSong(string.Empty, "ffffffffffffffffffffffffffffffff");
        var owner = new CatalogOwnedCollectionOwner();
        owner.ReplaceCharts([keptBms, deletedBms, pathlessBms], [oldBmson, pathlessBmson]);

        owner.TryCaptureFileScanRemovedCharts(
            [deletedBms.Path],
            [],
            [keptBms],
            [newBmson], out List<ChartFile> removedCharts);

        Assert.AreEqual(2, removedCharts.Count);
        Assert.IsTrue(removedCharts.Any(chart => ReferenceEquals(chart.Token, deletedBms.Token)));
        Assert.IsTrue(removedCharts.Any(chart => ReferenceEquals(chart.Token, oldBmson.Token)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.Token, keptBms.Token)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.Token, pathlessBms.Token)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.Token, newBmson.Token)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.Token, pathlessBmson.Token)));
    }


}
