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
    /// <summary>
    /// BMSとbmsonの捕捉値から所持集合を作るとき、種別・パス・両hashと所有tokenを取り違えず射影する。
    /// </summary>
    [TestMethod]
    public void FromStorageRows_BuildsBmsAndBmsonOwnedChartSnapshot()
    {

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

    /// <summary>
    /// BMSとbmsonのパス・MD5欠落と完全一致パスの重複を除外し、大文字小文字だけが異なるパスは別所有として保持する。
    /// </summary>
    [TestMethod]
    public void FromStorageRows_FiltersPathlessMd5lessAndExactDuplicateRows()
    {

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

    /// <summary>
    /// 保存対象は正常なBMS・bmsonを種別ごとに保持し、どちらの種別でもパスまたはMD5欠落を拒否する。
    /// </summary>
    [TestMethod]
    public void ChartStorageTargetSet_FromChartsRejectsInvalidBmsAndBmsonIdentities()
    {

        ChartFile pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

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

    /// <summary>
    /// 移転と基本値の適用後は新しいパス・hashを射影し、以前の捕捉値と同じ所有tokenを維持する。
    /// </summary>
    [TestMethod]
    public void CurrentApplicationKeepsCapturedValuesAndReprojectsCurrentIndexes()
    {

        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        ChartFile capture = state.CreateSnapshot().Single();
        state.ApplyPathChanges([new LibraryChartPathChange { Chart = capture, OldPath = capture.Path, NewPath = newPath }]);
        bmsFile = state.ResolveCurrentChart(LibraryChartRef.FromChartFile(capture)) with
        { Md5 = "cccccccccccccccccccccccccccccccc", Sha256 = new string('d', 64) };
        Assert.AreEqual(CatalogCurrentValueApplyResult.Changed, state.ApplyCurrentChartValue(bmsFile));
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

    /// <summary>
    /// 共通一覧と完全一致パス索引はBMS・bmsonを含め、パスのない所有を一覧にも索引にも混入させない。
    /// </summary>
    [TestMethod]
    public void CreateCollectionView_ReturnsCommonValuesAndExactPathLookup()
    {

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

    /// <summary>
    /// 通常一覧では形式別の捕捉順とtokenを保ち、移転後の新値と捕捉済み旧値を混在させず、BMS専用ではBMSONを含めない。
    /// </summary>
    [TestMethod]
    public void CaptureSnapshot_PreservesCatalogOrderAndExcludesPathlessRows()
    {

        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile secondBms = CreateFile("ffffffffffffffffffffffffffffffff", Path.Combine("C:\\Installed", "Bms", "a.bms"), new string('1', 64));
        ChartFile lateBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "z.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile earlyBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "a.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        ChartFile pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        var owner = new CatalogOwnedCollectionOwner();
        owner.ReplaceCharts([bmsFile, pathlessBms, secondBms], [lateBmson, pathlessBmson, earlyBmson]);
        OwnedChartCollectionState state = owner.Collection;

        CatalogChartCollectionSnapshot view = owner.CaptureSnapshot();

        Assert.AreEqual(4, view.BmsRows.Count + view.BmsonRows.Count);
        CollectionAssert.AreEqual(new[] { bmsFile.Path, secondBms.Path }, view.BmsRows.Select(chart => chart.Path).ToArray());
        Assert.IsTrue(view.BmsRows.All(chart => chart.Token != null));
        CollectionAssert.AreEqual(new[] { lateBmson.Path, earlyBmson.Path }, view.BmsonRows.Select(chart => chart.Path).ToArray());


        ChartFile capturedEarly = view.BmsonRows[1];
        string movedPath = Path.Combine("C:\\Installed", "Bmson", "zz.bmson");
        state.ApplyPathChanges([new LibraryChartPathChange
        {
            Chart = capturedEarly,
            OldPath = capturedEarly.Path,
            NewPath = movedPath
        }]);
        CatalogChartCollectionSnapshot movedView = owner.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { lateBmson.Path, movedPath }, movedView.BmsonRows.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(new[] { bmsFile.Path, secondBms.Path }, movedView.BmsRows.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(earlyBmson.Path, capturedEarly.Path);
        Assert.AreSame(capturedEarly.Token, movedView.BmsonRows[1].Token);
        CollectionAssert.AreEqual(new[] { lateBmson.Path, earlyBmson.Path }, view.BmsonRows.Select(chart => chart.Path).ToArray());

        CatalogChartCollectionSnapshot bmsOnlyView = owner.CaptureSnapshot(includeBmsonRows: false);
        CollectionAssert.AreEqual(new[] { bmsFile.Path, secondBms.Path }, bmsOnlyView.BmsRows.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(0, bmsOnlyView.BmsonRows.Count);
    }

    /// <summary>
    /// 走査で削除・同パス置換された現在の所有だけを削除対象に捕捉し、新譜面やパスのない所有を巻き込まない。
    /// </summary>
    [TestMethod]
    public void CreateFileScanRemovedCharts_UsesOwnedCurrentOwners()
    {

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
