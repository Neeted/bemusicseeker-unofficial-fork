using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('d', 64),
            title = "bmson title"
        };

        List<ChartFile> snapshot = OwnedChartCollectionState
            .FromStorageRows([bmsFile], [bmsonSong])
            .CreateSnapshot(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        ChartFile bmsChart = snapshot.Single(chart => chart.Kind == ChartFileKind.Bms);
        ChartFile bmsonChart = snapshot.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(bmsFile.path, bmsChart.Path);
        Assert.AreEqual(bmsFile.hash, bmsChart.Md5);
        Assert.AreEqual(bmsFile.sha256, bmsChart.Sha256);
        Assert.AreSame(bmsFile, bmsChart.GetBmsStorageOwner());
        Assert.AreEqual(bmsonSong.path, bmsonChart.Path);
        Assert.AreEqual(bmsonSong.md5, bmsonChart.Md5);
        Assert.AreEqual(bmsonSong.sha256, bmsonChart.Sha256);
        Assert.AreSame(bmsonSong, bmsonChart.GetBmsonStorageOwner());
    }

    /// <summary>未所持identityと同じexact keyの重複だけを除外し、別caseの行は保持します。</summary>
    [TestMethod]
    public void FromStorageRows_FiltersPathlessMd5lessAndExactDuplicateRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        TestableBmsFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        TestableBmsFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"), new string('1', 64));
        TestableBmsFile duplicateBms = CreateFile("22222222222222222222222222222222", pathfulBms.path, new string('2', 64));
        LR2SongDBExtended.bmson_song pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");
        LR2SongDBExtended.bmson_song md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        LR2SongDBExtended.bmson_song duplicateBmson = CreateBmsonSong(pathfulBms.path.ToUpperInvariant(), "33333333333333333333333333333333");

        List<ChartFile> snapshot = OwnedChartCollectionState
            .FromStorageRows([pathfulBms, pathlessBms, md5lessBms, duplicateBms], [pathfulBmson, pathlessBmson, md5lessBmson, duplicateBmson], out OwnedChartStorageRowFilterSummary filterSummary)
            .CreateSnapshot(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false);

        Assert.AreEqual(3, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathfulBms)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathfulBmson)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), duplicateBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathlessBms)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), md5lessBms)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), md5lessBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), duplicateBms)));
        Assert.AreEqual(1, filterSummary.PathlessBmsCount);
        Assert.AreEqual(1, filterSummary.PathlessBmsonCount);
        Assert.AreEqual(1, filterSummary.Md5lessBmsCount);
        Assert.AreEqual(1, filterSummary.Md5lessBmsonCount);
        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
        Assert.AreEqual(0, filterSummary.DuplicatePathBmsonCount);
    }

    [TestMethod]
    public void ChartFileProjection_FromStorageRowsRequirePathFiltersBmsAndBmsonRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        TestableBmsFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        LR2SongDBExtended.bmson_song pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");

        List<ChartFile> requirePath = ChartFileProjection.FromStorageRows(
            [pathfulBms, pathlessBms],
            [pathfulBmson, pathlessBmson],
            requirePath: true,
            includeResourceReferences: false);
        List<ChartFile> projectionOnly = ChartFileProjection.FromStorageRows(
            [pathfulBms, pathlessBms],
            [pathfulBmson, pathlessBmson],
            requirePath: false,
            includeResourceReferences: false);

        Assert.AreEqual(2, requirePath.Count);
        Assert.IsFalse(requirePath.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathlessBms)));
        Assert.IsFalse(requirePath.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
        Assert.AreEqual(4, projectionOnly.Count);
        Assert.IsTrue(projectionOnly.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathlessBms)));
        Assert.IsTrue(projectionOnly.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
    }

    [TestMethod]
    public void ChartStorageTargetSet_FromRowsRejectsInvalidBmsAndBmsonRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        TestableBmsFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        TestableBmsFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"), new string('1', 64));
        LR2SongDBExtended.bmson_song pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");
        LR2SongDBExtended.bmson_song md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);

        var targets = ChartStorageTargetSet.FromRows(
            [pathfulBms],
            [pathfulBmson]);

        CollectionAssert.AreEqual(new[] { pathfulBms }, targets.BmsFiles.ToArray());
        CollectionAssert.AreEqual(new[] { pathfulBmson }, targets.BmsonSongs.ToArray());
        Assert.AreEqual(2, targets.Charts.Count);
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromRows([pathlessBms], []));
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromRows([md5lessBms], []));
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromRows([], [pathlessBmson]));
        Assert.ThrowsException<InvalidOperationException>(() => ChartStorageTargetSet.FromRows([], [md5lessBmson]));
    }

    [TestMethod]
    public void CreateSnapshot_MatchesDirectStorageRowProjection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('d', 64),
            title = "bmson title"
        };

        List<ChartFile> ownedSnapshot = OwnedChartCollectionState
            .FromStorageRows([bmsFile], [bmsonSong])
            .CreateSnapshot(includeWarningSnapshot: false, includeResourceReferences: true, includeScoreSnapshot: false);
        List<ChartFile> projectionSnapshot = ChartFileProjection.FromStorageRows(
            [bmsFile],
            [bmsonSong],
            includeWarningSnapshot: false,
            includeResourceReferences: true,
            includeScoreSnapshot: false);

        AssertChartSnapshotParity(projectionSnapshot, ownedSnapshot);
    }

    [TestMethod]
    public void CreateSnapshot_ReprojectsCurrentStorageOwnerValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        bmsFile.path = newPath;
        bmsFile.SetHash("cccccccccccccccccccccccccccccccc");
        bmsFile.SetSha256(new string('d', 64));

        ChartFile snapshot = state.CreateSnapshot(
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false).Single();

        Assert.AreEqual(newPath, snapshot.Path);
        Assert.AreEqual(bmsFile.hash, snapshot.Md5);
        Assert.AreEqual(bmsFile.sha256, snapshot.Sha256);
        Assert.AreSame(bmsFile, snapshot.GetBmsStorageOwner());
    }

    [TestMethod]
    public void CreateStorageOwnerView_ReturnsOwnersAndOwnerPathLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        TestableBmsFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms], [bmsonSong, pathlessBmson]);

        OwnedChartStorageOwnerView view = state.CreateStorageOwnerView();

        Assert.AreEqual(2, view.Count);
        Assert.AreEqual(2, view.OwnerPathCount);
        Assert.AreSame(bmsFile, view.BmsFiles.Single());
        CollectionAssert.AreEqual(new[] { bmsonSong }, view.BmsonSongs.ToArray());
        Assert.IsTrue(view.ContainsOwnerPath(bmsFile.path));
        Assert.IsTrue(view.ContainsOwnerPath(bmsonSong.path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBms.path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBmson.path));
    }

    [TestMethod]
    public void CreateNormalLibrarySourceStorageOwnerView_SortsBmsonRowsAndExcludesPathlessRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        LR2SongDBExtended.bmson_song lateBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "z.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        LR2SongDBExtended.bmson_song earlyBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "a.bmson"), "cccccccccccccccccccccccccccccccc");
        TestableBmsFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms], [lateBmson, pathlessBmson, earlyBmson]);

        OwnedChartStorageOwnerView view = state.CreateNormalLibrarySourceStorageOwnerView();

        Assert.AreEqual(3, view.Count);
        Assert.AreSame(bmsFile, view.BmsFiles.Single());
        CollectionAssert.AreEqual(new[] { earlyBmson, lateBmson }, view.BmsonSongs.ToArray());
        Assert.IsTrue(view.ContainsOwnerPath(lateBmson.path));
        Assert.IsTrue(view.ContainsOwnerPath(earlyBmson.path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBms.path));
        Assert.IsFalse(view.ContainsOwnerPath(pathlessBmson.path));
    }

    [TestMethod]
    public void CreateFileScanRemovedStorageOwnerIdentityCharts_UsesOwnedCurrentOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        TestableBmsFile deletedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "deleted.bms"));
        TestableBmsFile pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty);
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        LR2SongDBExtended.bmson_song oldBmson = CreateBmsonSong(bmsonPath, "dddddddddddddddddddddddddddddddd");
        LR2SongDBExtended.bmson_song newBmson = CreateBmsonSong(bmsonPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(string.Empty, "ffffffffffffffffffffffffffffffff");
        var state = OwnedChartCollectionState.FromStorageRows(
            [keptBms, deletedBms, pathlessBms],
            [oldBmson, pathlessBmson]);

        List<ChartFile> removedCharts = state.CreateFileScanRemovedStorageOwnerIdentityCharts(
            [deletedBms.path],
            [],
            [keptBms],
            [newBmson]);

        Assert.AreEqual(2, removedCharts.Count);
        Assert.IsTrue(removedCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), deletedBms)));
        Assert.IsTrue(removedCharts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), oldBmson)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), keptBms)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathlessBms)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), newBmson)));
        Assert.IsFalse(removedCharts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
    }


}
