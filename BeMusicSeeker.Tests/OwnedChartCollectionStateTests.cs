using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionStateTests
{
    [TestMethod]
    public void FromStorageRows_BuildsBmsAndBmsonOwnedChartSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
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

    [TestMethod]
    public void FromStorageRows_FiltersPathlessMd5lessAndExactDuplicateRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        var md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"), new string('1', 64));
        var duplicateBms = CreateFile("22222222222222222222222222222222", Path.Combine("C:\\Installed", "Bms", ".", "chart.bms"), new string('2', 64));
        var pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");
        var md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        var duplicateBmson = CreateBmsonSong(pathfulBms.path.ToUpperInvariant(), "33333333333333333333333333333333");

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
        var pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        var pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");

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
        var pathfulBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty, new string('d', 64));
        var md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"), new string('1', 64));
        var pathfulBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var pathlessBmson = CreateBmsonSong(null, "ffffffffffffffffffffffffffffffff");
        var md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);

        ChartStorageTargetSet targets = ChartStorageTargetSet.FromRows(
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
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
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
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
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
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        var pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        var pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms], [bmsonSong, pathlessBmson]);

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
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var lateBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "z.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var earlyBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "a.bmson"), "cccccccccccccccccccccccccccccccc");
        var pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty, new string('f', 64));
        var pathlessBmson = CreateBmsonSong(null, "dddddddddddddddddddddddddddddddd");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms], [lateBmson, pathlessBmson, earlyBmson]);

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
        var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        var deletedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "deleted.bms"));
        var pathlessBms = CreateFile("cccccccccccccccccccccccccccccccc", string.Empty);
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        var oldBmson = CreateBmsonSong(bmsonPath, "dddddddddddddddddddddddddddddddd");
        var newBmson = CreateBmsonSong(bmsonPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var pathlessBmson = CreateBmsonSong(string.Empty, "ffffffffffffffffffffffffffffffff");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows(
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

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_ReprojectsCurrentStorageOwnerValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "BmsOld", "chart.bms"), new string('b', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "BmsonOld", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        string newBmsPath = Path.Combine("C:\\Installed", "BmsNew", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "BmsonNew", "chart.bmson");
        bmsFile.path = newBmsPath;
        bmsFile.SetHash("dddddddddddddddddddddddddddddddd");
        bmsFile.SetSha256(new string('e', 64));
        bmsonSong.path = newBmsonPath;
        bmsonSong.md5 = "ffffffffffffffffffffffffffffffff";
        bmsonSong.sha256 = new string('1', 64);

        CanonicalChartResolveResult resolveResult = state.CreateLibraryChartRefIndexSnapshot().ResolveCanonicalCharts([
            LibraryChartRef.FromPath(LibraryChartKind.Bms, newBmsPath, bmsFile.hash, bmsFile.sha256),
            LibraryChartRef.FromPath(LibraryChartKind.Bmson, newBmsonPath, bmsonSong.md5, bmsonSong.sha256)
        ]);

        List<LibraryChartRef> refs = resolveResult.CanonicalCharts;
        List<LibraryChartRef> pathRefs = state.CreateLibraryChartRefIndexSnapshot().GetChartRefsByPaths([newBmsPath, newBmsonPath]);
        Assert.AreEqual(2, refs.Count);
        Assert.AreEqual(2, pathRefs.Count);
        LibraryChartRef bmsRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bms);
        LibraryChartRef bmsonRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bmson);
        Assert.IsTrue(pathRefs.Any(chart => chart.Kind == LibraryChartKind.Bms && string.Equals(chart.Path, newBmsPath, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(pathRefs.Any(chart => chart.Kind == LibraryChartKind.Bmson && string.Equals(chart.Path, newBmsonPath, StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(newBmsPath, bmsRef.Path);
        Assert.AreEqual(bmsFile.hash, bmsRef.Md5);
        Assert.AreEqual(bmsFile.sha256, bmsRef.Sha256);
        Assert.AreSame(bmsFile, bmsRef.GetBmsStorageOwner());
        Assert.AreEqual(newBmsonPath, bmsonRef.Path);
        Assert.AreEqual(bmsonSong.md5, bmsonRef.Md5);
        Assert.AreEqual(bmsonSong.sha256, bmsonRef.Sha256);
        Assert.AreSame(bmsonSong, bmsonRef.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_CachedRefsUseCurrentOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        bmsFile.SetHash("cccccccccccccccccccccccccccccccc");
        bmsFile.SetSha256(new string('d', 64));
        bmsonSong.md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        bmsonSong.sha256 = new string('f', 64);

        List<LibraryChartRef> refs = index.GetChartRefsByPaths([bmsFile.path, bmsonSong.path]);
        LibraryChartRef bmsRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bms);
        LibraryChartRef bmsonRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bmson);
        Assert.AreEqual(bmsFile.hash, bmsRef.Md5);
        Assert.AreEqual(bmsFile.sha256, bmsRef.Sha256);
        Assert.AreEqual(bmsonSong.md5, bmsonRef.Md5);
        Assert.AreEqual(bmsonSong.sha256, bmsonRef.Sha256);
    }

    [TestMethod]
    public void CreateChartDirectoriesUnderRealPath_ReturnsDistinctSubtreeDirectoriesWithoutChartSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string rootPath = Path.Combine("C:\\Installed", "Root");
        string childPath = Path.Combine(rootPath, "Child");
        string nestedPath = Path.Combine(childPath, "Nested");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(childPath, "chart.bms"));
        var sameDirectoryBmson = CreateBmsonSong(Path.Combine(childPath, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var nestedBmson = CreateBmsonSong(Path.Combine(nestedPath, "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [sameDirectoryBmson, nestedBmson]);

        List<string> directories = state.CreateChartDirectoriesUnderRealPath(rootPath);

        CollectionAssert.AreEqual(new[] { childPath, nestedPath }, directories.ToArray());
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_PathlessRowsAreNotPathLookupTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, new string('b', 64));
        var bmsonSong = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");

        LibraryChartRefIndexSnapshot index = LibraryChartRefIndexSnapshot.FromLibraryChartRefs([
            LibraryChartRef.FromBmsFile(bmsFile),
            LibraryChartRef.FromBmsonSong(bmsonSong)
        ]);

        Assert.AreEqual(0, index.GetChartRefsByPaths([bmsFile.path, bmsonSong.path]).Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath("C:\\Installed", null));
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromBmsFile(bmsFile),
            LibraryChartRef.FromBmsonSong(bmsonSong)
        ]);
        Assert.AreEqual(0, resolveResult.CanonicalCharts.Count);
    }

    [TestMethod]
    public void CreateSnapshotForMd5Hashes_ProjectsOnlyMatchingCurrentOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        bmsFile.SetHash("dddddddddddddddddddddddddddddddd");

        List<ChartFile> snapshot = state.CreateSnapshotForMd5Hashes(
            new HashSet<string>(["dddddddddddddddddddddddddddddddd"], StringComparer.OrdinalIgnoreCase),
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(bmsFile, snapshot[0].GetBmsStorageOwner());
        Assert.AreEqual("dddddddddddddddddddddddddddddddd", snapshot[0].Md5);
    }

    [TestMethod]
    public void CreateSnapshotForMd5Hashes_ExcludesMatchingPathlessOwnedRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var pathlessBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", string.Empty, new string('d', 64));
        var pathlessBmson = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms], [pathlessBmson]);

        List<ChartFile> snapshot = state.CreateSnapshotForMd5Hashes(
            new HashSet<string>([pathlessBms.hash, pathlessBmson.md5], StringComparer.OrdinalIgnoreCase),
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(0, snapshot.Count);
    }

    [TestMethod]
    public void CreateSnapshotForPaths_ProjectsOnlyRequestedPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var firstBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Target", "first.bms"), new string('b', 64));
        var secondBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "second.bms"), new string('d', 64));
        var targetBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Target", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([firstBms, secondBms], [targetBmson]);

        List<ChartFile> snapshot = state.CreateSnapshotForPaths(
            [Path.Combine("C:\\Installed", "Target", ".", "first.bms"), targetBmson.path, targetBmson.path],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), firstBms)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), targetBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), secondBms)));
    }

    [TestMethod]
    public void CreateSnapshotForPaths_ReturnsOnlyFirstOwnedChartForDuplicateStoragePaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], [], out OwnedChartStorageRowFilterSummary filterSummary);

        List<ChartFile> snapshot = state.CreateSnapshotForPaths(
            [sharedPath],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(first, snapshot[0].GetBmsStorageOwner());
        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
    }

    [TestMethod]
    public void CreateBmsSnapshot_ProjectsOnlyCurrentBmsOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        string newBmsPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        bmsFile.path = newBmsPath;
        bmsFile.SetHash("dddddddddddddddddddddddddddddddd");

        List<ChartFile> snapshot = state.CreateBmsSnapshot(
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual(ChartFileKind.Bms, snapshot[0].Kind);
        Assert.AreSame(bmsFile, snapshot[0].GetBmsStorageOwner());
        Assert.AreEqual(newBmsPath, snapshot[0].Path);
        Assert.AreEqual("dddddddddddddddddddddddddddddddd", snapshot[0].Md5);
    }

    [TestMethod]
    public void CreateSnapshotForDirectChildDirectories_FiltersBeforeProjectionUsingCurrentOwnerPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        var movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"));
        var directBmson = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var nestedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([movedBms, nestedBms], [directBmson]);
        movedBms.path = Path.Combine(targetDirectory, "chart.bms");

        List<ChartFile> snapshot = state.CreateSnapshotForDirectChildDirectories(
            [targetDirectory],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), movedBms)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), directBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), nestedBms)));
    }

    [TestMethod]
    public void CreateSnapshotForDirectChildDirectories_UsesUpdatedDirectoryIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldDirectory = Path.Combine("C:\\Installed", "Old");
        string newDirectory = Path.Combine("C:\\Installed", "New");
        string oldPath = Path.Combine(oldDirectory, "chart.bms");
        string newPath = Path.Combine(newDirectory, "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = newPath;
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        List<ChartFile> oldSnapshot = state.CreateSnapshotForDirectChildDirectories(
            [oldDirectory],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);
        List<ChartFile> newSnapshot = state.CreateSnapshotForDirectChildDirectories(
            [newDirectory],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(0, oldSnapshot.Count);
        Assert.AreEqual(1, newSnapshot.Count);
        Assert.AreSame(bmsFile, newSnapshot[0].GetBmsStorageOwner());
        Assert.AreEqual(newPath, newSnapshot[0].Path);
    }

    [TestMethod]
    public void CreateSnapshotForSubtreeDirectory_UsesOwnedRefIndexAndCurrentOwnerPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        var movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"));
        var nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        var siblingPrefixBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "TargetPrefix", "prefix.bms"));
        var directBmson = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "dddddddddddddddddddddddddddddddd");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([movedBms, nestedBms, siblingPrefixBms], [directBmson]);
        movedBms.path = Path.Combine(targetDirectory, "chart.bms");

        List<ChartFile> snapshot = state.CreateSnapshotForSubtreeDirectory(
            targetDirectory,
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(3, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), movedBms)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), nestedBms)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), directBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), siblingPrefixBms)));
    }

    [TestMethod]
    public void CreateStorageTargetsForSubtreeDirectory_UsesOwnedRefIndexAndStorageOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        var directBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(targetDirectory, "direct.bms"));
        var nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        var siblingPrefixBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "TargetPrefix", "prefix.bms"));
        var directBmson = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "dddddddddddddddddddddddddddddddd");
        var pathlessBmson = CreateBmsonSong(null, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([directBms, nestedBms, siblingPrefixBms], [directBmson, pathlessBmson]);

        ChartStorageTargetSet targets = state.CreateStorageTargetsForSubtreeDirectory(targetDirectory);

        CollectionAssert.AreEquivalent(new[] { directBms, nestedBms }, targets.BmsFiles);
        CollectionAssert.AreEqual(new[] { directBmson }, targets.BmsonSongs);
        Assert.AreEqual(3, targets.Charts.Count);
        Assert.IsTrue(targets.Charts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), directBms)));
        Assert.IsTrue(targets.Charts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), nestedBms)));
        Assert.IsTrue(targets.Charts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), directBmson)));
        Assert.IsFalse(targets.Charts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), siblingPrefixBms)));
        Assert.IsFalse(targets.Charts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
    }

    [TestMethod]
    public void ChartStorageTargetSetFromCharts_ReprojectsCurrentOwnerValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath, new string('b', 64));
        var bmsonSong = CreateBmsonSong(bmsonPath, "cccccccccccccccccccccccccccccccc");
        ChartFile staleBmsChart = ChartFileProjection.FromBmsFile(bmsFile, includeResourceReferences: true);
        ChartFile staleBmsonChart = ChartFileProjection.FromBmsonSong(bmsonSong, includeResourceReferences: true);
        bmsFile.SetHash("dddddddddddddddddddddddddddddddd");
        bmsFile.SetSha256(new string('e', 64));
        bmsonSong.md5 = "ffffffffffffffffffffffffffffffff";
        bmsonSong.sha256 = new string('1', 64);

        ChartStorageTargetSet targets = ChartStorageTargetSet.FromCharts([staleBmsChart, staleBmsonChart]);

        Assert.AreEqual(2, targets.Charts.Count);
        ChartFile currentBmsChart = targets.Charts.Single(chart => chart.Kind == ChartFileKind.Bms);
        ChartFile currentBmsonChart = targets.Charts.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(bmsFile.hash, currentBmsChart.Md5);
        Assert.AreEqual(bmsFile.sha256, currentBmsChart.Sha256);
        Assert.AreEqual(bmsonSong.md5, currentBmsonChart.Md5);
        Assert.AreEqual(bmsonSong.sha256, currentBmsonChart.Sha256);
        Assert.AreSame(bmsFile, currentBmsChart.GetBmsStorageOwner());
        Assert.AreSame(bmsonSong, currentBmsonChart.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void ChartStorageTargetSetFromCharts_RejectsInvalidOwnerCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var pathlessBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string.Empty);
        var md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        var pathlessBmson = CreateBmsonSong(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);

        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([ChartFileProjection.FromBmsStorageOwnerIdentity(pathlessBms)]));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([ChartFileProjection.FromBmsStorageOwnerIdentity(md5lessBms)]));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([ChartFileProjection.FromBmsonStorageOwnerIdentity(pathlessBmson)]));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([ChartFileProjection.FromBmsonStorageOwnerIdentity(md5lessBmson)]));
    }

    [TestMethod]
    public void ChartStorageTargetSet_ReturnsDistinctChartDirectoriesFromChartView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedDirectory = Path.Combine("C:\\Installed", "Shared");
        string nestedDirectory = Path.Combine(sharedDirectory, "Nested");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sharedDirectory, "chart.bms"));
        var sameDirectoryBmson = CreateBmsonSong(Path.Combine(sharedDirectory.ToUpperInvariant(), "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var nestedBmson = CreateBmsonSong(Path.Combine(nestedDirectory, "nested.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartStorageTargetSet targets = ChartStorageTargetSet.FromCharts([
            ChartFileProjection.FromBmsFile(bmsFile),
            ChartFileProjection.FromBmsonSong(sameDirectoryBmson),
            ChartFileProjection.FromBmsonSong(nestedBmson)
        ]);

        List<string> directories = targets.GetDistinctChartDirectories();

        CollectionAssert.AreEqual(new[] { sharedDirectory, nestedDirectory }, directories);
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_ResolvesPathOnlyAndCountsRealPathSubtree()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        string nestedDirectory = Path.Combine(targetDirectory, "Nested");
        var directBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(targetDirectory, "direct.bms"));
        var nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(nestedDirectory, "nested.bms"));
        var otherBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "other.bms"));
        var bmsonSong = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "dddddddddddddddddddddddddddddddd");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([directBms, nestedBms, otherBms], [bmsonSong]);

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(LibraryChartKind.Bms, Path.Combine(targetDirectory, ".", "direct.bms"), directBms.hash, directBms.sha256),
            LibraryChartRef.FromPath(LibraryChartKind.Bmson, bmsonSong.path, bmsonSong.md5, bmsonSong.sha256)
        ]);

        Assert.AreEqual(2, resolveResult.CanonicalCharts.Count);
        Assert.AreSame(directBms, resolveResult.CanonicalCharts.Single(chart => chart.Kind == LibraryChartKind.Bms).GetBmsStorageOwner());
        Assert.AreSame(bmsonSong, resolveResult.CanonicalCharts.Single(chart => chart.Kind == LibraryChartKind.Bmson).GetBmsonStorageOwner());
        Assert.AreEqual(3, index.CountChartRefsUnderRealPath(targetDirectory, null));
        Assert.AreEqual(2, index.CountChartRefsUnderRealPath(targetDirectory, new HashSet<string>([directBms.path], StringComparer.OrdinalIgnoreCase)));
        Assert.AreEqual(3, index.GetChartRefsUnderRealPath(targetDirectory).Count);
        Assert.AreEqual(2, index.CountBmsChartRefsUnderRealPath(targetDirectory));
        CollectionAssert.AreEquivalent(new[] { directBms.path, nestedBms.path }, index.GetBmsChartPathsUnderRealPath(targetDirectory));
        Assert.AreEqual(0, index.GetChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Missing")).Count);
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_RealPathSubtreeUsesDirectoryBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        var directBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(targetDirectory, "direct.bms"));
        var nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        var siblingPrefixBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "TargetPrefix", "prefix.bms"));
        var siblingSuffixBms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "TargetSuffix", "suffix.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([directBms, nestedBms, siblingPrefixBms, siblingSuffixBms], []);

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        List<LibraryChartRef> refs = index.GetChartRefsUnderRealPath(targetDirectory);

        Assert.AreEqual(2, refs.Count);
        Assert.IsTrue(refs.Any(chart => ReferenceEquals(directBms, chart.GetBmsStorageOwner())));
        Assert.IsTrue(refs.Any(chart => ReferenceEquals(nestedBms, chart.GetBmsStorageOwner())));
        Assert.IsFalse(refs.Any(chart => ReferenceEquals(siblingPrefixBms, chart.GetBmsStorageOwner())));
        Assert.IsFalse(refs.Any(chart => ReferenceEquals(siblingSuffixBms, chart.GetBmsStorageOwner())));
        Assert.AreEqual(4, index.GetChartRefsUnderRealPath("C:\\").Count);
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_ResolvesFirstOwnedChartWhenStorageRowsHaveDuplicatePath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], [], out OwnedChartStorageRowFilterSummary filterSummary);

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(LibraryChartKind.Bms, sharedPath, first.hash, first.sha256)
        ]);

        Assert.AreEqual(1, resolveResult.CanonicalCharts.Count);
        Assert.AreSame(first, resolveResult.CanonicalCharts[0].GetBmsStorageOwner());
        Assert.AreEqual(1, index.GetChartRefsByPaths([sharedPath]).Count);
        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
    }

    [TestMethod]
    public void ApplyPathChanges_UpdatesCachedLibraryChartRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        bmsFile.path = newPath;
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath]).Count);
        List<LibraryChartRef> newPathRefs = index.GetChartRefsByPaths([newPath]);
        Assert.AreEqual(1, newPathRefs.Count);
        Assert.AreSame(bmsFile, newPathRefs[0].GetBmsStorageOwner());
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(oldPath), null));
        Assert.AreEqual(1, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(oldPath)));
        Assert.AreEqual(1, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(newPath)));
        CollectionAssert.AreEqual(new[] { newPath }, index.GetBmsChartPathsUnderRealPath(Path.GetDirectoryName(newPath)));
    }

    [TestMethod]
    public void ApplyPathChanges_DoesNotReAddRemovedChartsToCachedIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromOwnerReference(bmsFile)]);
        bmsFile.path = newPath;
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath, newPath]).Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(oldPath), null));
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(oldPath)));
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(newPath)));
    }

    [TestMethod]
    public void RemoveChartRequests_RemovesMovedOwnerFromCachedIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = newPath;
        ChartFile movedChartSnapshot = ChartFileProjection.FromBmsFile(bmsFile);
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = movedChartSnapshot,
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromOwnerReference(bmsFile)]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath, newPath]).Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(oldPath), null));
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(oldPath)));
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(newPath)));
    }

    [TestMethod]
    public void ApplyPathChanges_IgnoresDuplicateMoveAfterOldPathWasRemoved()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = newPath;

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            },
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath]).Count);
        Assert.AreEqual(1, index.GetChartRefsByPaths([newPath]).Count);
        Assert.AreEqual(1, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        Assert.AreEqual(1, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(newPath)));
    }

    [TestMethod]
    public void ApplyPathChanges_UsesCachedOwnerPathWhenOldPathIsMissing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = newPath;

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = null,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath]).Count);
        Assert.AreEqual(1, index.GetChartRefsByPaths([newPath]).Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(oldPath), null));
        Assert.AreEqual(1, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(oldPath)));
        Assert.AreEqual(1, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(newPath)));
    }

    [TestMethod]
    public void ApplyPathChanges_CachedRefsProjectCurrentStorageOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = newPath;

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        LibraryChartRef movedRef = index.GetChartRefsByPaths([newPath]).Single();
        ChartFile currentChart = movedRef.ToChartFile();
        Assert.AreEqual(newPath, movedRef.Path);
        Assert.AreEqual(newPath, currentChart.Path);
        Assert.AreSame(bmsFile, currentChart.GetBmsStorageOwner());
    }

    [TestMethod]
    public void ApplyPathChanges_DoesNotAddPathlessOwnerWhenPathAppears()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = newPath;

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = null,
                NewPath = newPath
            },
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = null,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        List<LibraryChartRef> refs = index.GetChartRefsByPaths([newPath]);
        Assert.AreEqual(0, refs.Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([LibraryChartRef.FromBmsFile(bmsFile)]);
        Assert.AreEqual(0, resolveResult.CanonicalCharts.Count);
    }

    [TestMethod]
    public void ApplyPathChanges_RejectsPathDisappearingOwnedMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = null;

        Assert.ThrowsException<InvalidOperationException>(() => state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = null
            }
        ]));

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
    }

    [TestMethod]
    public void ApplyPathChanges_RejectsPathCollisionWithAnotherOwnedChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string existingPath = Path.Combine("C:\\Installed", "Existing", "chart.bms");
        var movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var existingBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", existingPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([movedBms, existingBms], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        movedBms.path = existingPath;

        Assert.ThrowsException<InvalidOperationException>(() => state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = oldPath,
                NewPath = existingPath
            }
        ]));

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(1, index.GetChartRefsByPaths([oldPath]).Count);
        Assert.AreEqual(1, index.GetChartRefsByPaths([existingPath]).Count);
    }

    [TestMethod]
    public void LibraryChartRefIndexSnapshot_RemoveCharts_UpdatesCachedAmbiguity()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        LibraryChartRefIndexSnapshot index = LibraryChartRefIndexSnapshot.FromLibraryChartRefs([
            LibraryChartRef.FromBmsFile(first),
            LibraryChartRef.FromBmsFile(second)
        ]);

        index.RemoveCharts([ChartFileProjection.FromBmsFile(first)]);
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(LibraryChartKind.Bms, sharedPath, second.hash, second.sha256)
        ]);

        Assert.AreEqual(1, resolveResult.CanonicalCharts.Count);
        Assert.AreSame(second, resolveResult.CanonicalCharts[0].GetBmsStorageOwner());
        Assert.AreEqual(1, index.GetChartRefsByPaths([sharedPath]).Count);
    }

    [TestMethod]
    public void UpsertStorageRows_UpdatesCachedLibraryChartRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string replacedPath = Path.Combine("C:\\Installed", "Bms", "replace.bms");
        var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        var replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedPath);
        var newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([keptBms, replacedBms], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertStorageRows([newBms], []);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        List<LibraryChartRef> refs = index.GetChartRefsByPaths([replacedPath]);
        Assert.AreEqual(1, refs.Count);
        Assert.AreSame(newBms, refs[0].GetBmsStorageOwner());
        Assert.AreEqual(2, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(replacedPath), null));
        Assert.AreEqual(2, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(replacedPath)));
    }

    [TestMethod]
    public void UpsertStorageRows_RejectsInvalidBmsAndBmsonRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string existingBmsPath = Path.Combine("C:\\Installed", "Bms", "existing.bms");
        string existingBmsonPath = Path.Combine("C:\\Installed", "Bmson", "existing.bmson");
        var existingBms = CreateFile("11111111111111111111111111111111", existingBmsPath);
        var existingBmson = CreateBmsonSong(existingBmsonPath, "22222222222222222222222222222222");
        var state = OwnedChartCollectionState.FromStorageRows([existingBms], [existingBmson]);
        var pathlessBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string.Empty);
        var md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        var pathlessBmson = CreateBmsonSong(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        var duplicateBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "duplicate.bms"));
        var samePathBms = CreateFile("dddddddddddddddddddddddddddddddd", duplicateBms.path);
        var caseOnlyPathBms = CreateFile("99999999999999999999999999999999", duplicateBms.path.ToUpperInvariant());
        var crossKindBmson = CreateBmsonSong(existingBmsPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var crossKindBms = CreateFile("ffffffffffffffffffffffffffffffff", existingBmsonPath);

        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([pathlessBms], []));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([md5lessBms], []));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([], [pathlessBmson]));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([], [md5lessBmson]));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([duplicateBms, samePathBms], []));
        state.UpsertStorageRows([caseOnlyPathBms], []);
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([], [crossKindBmson]));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertStorageRows([crossKindBms], []));
    }

    [TestMethod]
    public void UpsertStorageRows_PreservesFullBuildSubtreeOrder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directoryPath = Path.Combine("C:\\Installed", "Mixed");
        var bmsonSong = CreateBmsonSong(Path.Combine(directoryPath, "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var bmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(directoryPath, "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([], [bmsonSong]);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertStorageRows([bmsFile], []);
        List<ChartFile> cachedSnapshot = state.CreateSnapshotForSubtreeDirectory(
            directoryPath,
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);
        List<ChartFile> rebuiltSnapshot = OwnedChartCollectionState
            .FromStorageRows([bmsFile], [bmsonSong])
            .CreateSnapshotForSubtreeDirectory(
                directoryPath,
                includeWarningSnapshot: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false);

        List<string> rebuiltPaths = rebuiltSnapshot.Select(chart => chart.Path).ToList();
        List<string> cachedPaths = cachedSnapshot.Select(chart => chart.Path).ToList();
        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(1, index.CountBmsChartRefsUnderRealPath(directoryPath));
        CollectionAssert.AreEqual(new[] { bmsFile.path }, index.GetBmsChartPathsUnderRealPath(directoryPath));
        Assert.AreEqual(
            string.Join("|", rebuiltPaths),
            string.Join("|", cachedPaths));
    }

    [TestMethod]
    public void CreateOwnedHashIndexSnapshot_ReprojectsCurrentStorageOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        bmsonSong.sha256 = new string('d', 64);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        bmsFile.SetHash("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        bmsFile.SetSha256(new string('f', 64));
        bmsonSong.md5 = "11111111111111111111111111111111";
        bmsonSong.sha256 = new string('2', 64);

        OwnedChartHashIndexSnapshot snapshot = state.CreateOwnedHashIndexSnapshot();

        CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), bmsFile.hash);
        CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), bmsonSong.md5);
        CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), bmsFile.sha256);
        CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), bmsonSong.sha256);
    }

    [TestMethod]
    public void CreateLibraryChartRefsForHashes_ReturnsOnlyMatchingCurrentOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var md5Bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Md5", "chart.bms"), new string('b', 64));
        var shaBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Sha", "chart.bms"), new string('d', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        bmsonSong.sha256 = new string('f', 64);
        var unmatchedBms = CreateFile("11111111111111111111111111111111", Path.Combine("C:\\Installed", "Other", "chart.bms"), new string('2', 64));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([md5Bms, shaBms, unmatchedBms], [bmsonSong]);
        md5Bms.SetHash("33333333333333333333333333333333");
        shaBms.SetSha256(new string('4', 64));
        bmsonSong.md5 = "55555555555555555555555555555555";

        List<LibraryChartRef> refs = state.CreateLibraryChartRefsForHashes(
            new HashSet<string>([md5Bms.hash, bmsonSong.md5], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([shaBms.sha256], StringComparer.OrdinalIgnoreCase));

        Assert.AreEqual(3, refs.Count);
        Assert.IsTrue(refs.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), md5Bms)));
        Assert.IsTrue(refs.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), shaBms)));
        Assert.IsTrue(refs.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), bmsonSong)));
        Assert.IsFalse(refs.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), unmatchedBms)));
    }

    [TestMethod]
    public void CreateChartInfoHydrationOwnerSummary_ReprojectsCurrentStorageOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var currentChartInfoFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Info", "chart.bms"), new string('b', 64));
        var parseFailureSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Failure", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        var backfillFile = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "Backfill", "chart.bms"), new string('e', 64));
        parseFailureSong.sha256 = new string('f', 64);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([currentChartInfoFile, backfillFile], [parseFailureSong]);
        currentChartInfoFile.SetSha256(new string('1', 64));
        parseFailureSong.md5 = "22222222222222222222222222222222";

        ChartInfoHydrationOwnerSummary summary = state.CreateChartInfoHydrationOwnerSummary(
            new HashSet<string>([currentChartInfoFile.sha256], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([parseFailureSong.md5], StringComparer.OrdinalIgnoreCase));

        Assert.AreEqual(3, summary.OwnerCount);
        Assert.AreEqual(1, summary.CurrentChartInfoOwnerCount);
        Assert.AreEqual(1, summary.CurrentParseFailureOwnerCount);
        Assert.AreEqual(1, summary.BackfillCandidateOwnerCount);
        Assert.AreEqual(1, summary.OwnerApplySkippedCount);
    }

    [TestMethod]
    public void CreateFullResourceMaintenanceTargetSnapshot_FiltersInvalidOwnedChartsBeforeProjection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        var pathlessBms = CreateFile("dddddddddddddddddddddddddddddddd", string.Empty);
        var md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var pathlessBmson = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");
        var md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms, md5lessBms], [bmsonSong, pathlessBmson, md5lessBmson]);

        List<ChartFile> snapshot = state.CreateFullResourceMaintenanceTargetSnapshot();

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), bmsFile)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), bmsonSong)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathlessBms)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), md5lessBms)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), md5lessBmson)));
    }

    [TestMethod]
    public void CreatePathSnapshot_ReprojectsCurrentStorageOwnerPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "OldBms", "chart.bms"));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "OldBmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        string newBmsPath = Path.Combine("C:\\Installed", "NewBms", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "NewBmson", "chart.bmson");
        bmsFile.path = newBmsPath;
        bmsonSong.path = newBmsonPath;

        List<string> paths = state.CreatePathSnapshot();

        CollectionAssert.Contains(paths, newBmsPath);
        CollectionAssert.Contains(paths, newBmsonPath);
        CollectionAssert.DoesNotContain(paths, Path.Combine("C:\\Installed", "OldBms", "chart.bms"));
        CollectionAssert.DoesNotContain(paths, Path.Combine("C:\\Installed", "OldBmson", "chart.bmson"));
    }

    [TestMethod]
    public void CreateInstallDestinationRuntimeStateKeySnapshot_ReprojectsCurrentStorageOwnerValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldBmsPath = Path.Combine("C:\\Installed", "OldBms", "chart.bms");
        string oldBmsonPath = Path.Combine("C:\\Installed", "OldBmson", "chart.bmson");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath, new string('b', 64));
        var bmsonSong = CreateBmsonSong(oldBmsonPath, "cccccccccccccccccccccccccccccccc");
        bmsonSong.sha256 = new string('d', 64);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        string newBmsPath = Path.Combine("C:\\Installed", "NewBms", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "NewBmson", "chart.bmson");
        bmsFile.path = newBmsPath;
        bmsFile.SetHash("11111111111111111111111111111111");
        bmsFile.SetSha256(new string('2', 64));
        bmsonSong.path = newBmsonPath;
        bmsonSong.md5 = "33333333333333333333333333333333";
        bmsonSong.sha256 = new string('4', 64);

        HashSet<string> keys = state.CreateInstallDestinationRuntimeStateKeySnapshot();

        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, newBmsPath, bmsFile.hash, bmsFile.sha256)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, newBmsPath)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, newBmsonPath, bmsonSong.md5, bmsonSong.sha256)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, newBmsonPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, oldBmsPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64))));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, oldBmsPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, oldBmsonPath, "cccccccccccccccccccccccccccccccc", new string('d', 64))));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, oldBmsonPath)));
    }

    [TestMethod]
    public void CreateChartRuntimeStatePrimaryKeySnapshot_ExcludesInstallDestinationPathAliases()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldBmsPath = Path.Combine("C:\\Installed", "OldBms", "chart.bms");
        string oldBmsonPath = Path.Combine("C:\\Installed", "OldBmson", "chart.bmson");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath, new string('b', 64));
        var bmsonSong = CreateBmsonSong(oldBmsonPath, "cccccccccccccccccccccccccccccccc");
        bmsonSong.sha256 = new string('d', 64);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        string newBmsPath = Path.Combine("C:\\Installed", "NewBms", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "NewBmson", "chart.bmson");
        bmsFile.path = newBmsPath;
        bmsFile.SetHash("11111111111111111111111111111111");
        bmsFile.SetSha256(new string('2', 64));
        bmsonSong.path = newBmsonPath;
        bmsonSong.md5 = "33333333333333333333333333333333";
        bmsonSong.sha256 = new string('4', 64);

        HashSet<string> keys = state.CreateChartRuntimeStatePrimaryKeySnapshot();

        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, newBmsPath, bmsFile.hash, bmsFile.sha256)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, newBmsonPath, bmsonSong.md5, bmsonSong.sha256)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, newBmsPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, newBmsonPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, oldBmsPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64))));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, oldBmsonPath, "cccccccccccccccccccccccccccccccc", new string('d', 64))));
    }

    [TestMethod]
    public void RemoveChartRequests_UpdateOwnedCollectionMembership()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc"
        };
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], [bmsonSong]);

        Assert.AreEqual(2, state.RemoveChartRequests([
            OwnedChartRemoveRequest.FromOwnerReference(first),
            OwnedChartRemoveRequest.FromOwnerReference(bmsonSong)
        ]));
        List<ChartFile> afterRemove = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, afterRemove.Count);
        Assert.AreSame(second, afterRemove[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void RemoveChartRequests_StaleOwnerReferenceDoesNotRemoveSamePathChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = bmsonPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        var staleSamePathBmsOwner = CreateFile("cccccccccccccccccccccccccccccccc", bmsonPath);

        Assert.AreEqual(0, state.RemoveChartRequests([
            OwnedChartRemoveRequest.FromOwnerReference(staleSamePathBmsOwner)
        ]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), bmsFile)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), bmsonSong)));
    }

    [TestMethod]
    public void RemoveChartRequests_PathCleanupRemovesUniqueOwnedPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        var other = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile, other], []);

        Assert.AreEqual(1, state.RemoveChartRequests([
            OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, bmsPath)
        ]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(other, snapshot[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void RemoveChartRequests_OwnerReferenceRemovesOnlyRequestedOwnerWhenDuplicatePathWasSkipped()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var other = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, samePath, other], [], out OwnedChartStorageRowFilterSummary filterSummary);

        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
        Assert.AreEqual(1, state.RemoveChartRequests([OwnedChartRemoveRequest.FromOwnerReference(first)]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(other, snapshot[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void ContainsKnownChart_UsesOwnedReferenceAndKindPathExactLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = bmsonPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        var bmsonPathOnlyChart = new ChartFile(
            ChartFileKind.Bmson,
            bmsonPath,
            bmsonSong.md5,
            null,
            "title",
            "raw",
            "artist",
            string.Empty,
            Path.GetDirectoryName(bmsonPath),
            string.Empty,
            string.Empty,
            null,
            0,
            null,
            null,
            null);

        Assert.IsTrue(state.ContainsKnownChart(ChartFileProjection.FromBmsStorageOwnerIdentity(bmsFile)));
        Assert.IsTrue(state.ContainsKnownChart(bmsonPathOnlyChart));
        Assert.IsFalse(state.ContainsKnownChart(new ChartFile(
            ChartFileKind.Bmson,
            bmsPath,
            "cccccccccccccccccccccccccccccccc",
            null,
            "title",
            "raw",
            "artist",
            string.Empty,
            Path.GetDirectoryName(bmsPath),
            string.Empty,
            string.Empty,
            null,
            0,
            null,
            null,
            null)));
    }

    [TestMethod]
    public void ContainsKnownChart_PathExactLookupUsesOwnedUniquePath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, samePath], [], out OwnedChartStorageRowFilterSummary filterSummary);
        var pathOnlyChart = new ChartFile(
            ChartFileKind.Bms,
            sharedPath,
            "cccccccccccccccccccccccccccccccc",
            null,
            "title",
            "raw",
            "artist",
            string.Empty,
            Path.GetDirectoryName(sharedPath),
            string.Empty,
            string.Empty,
            null,
            0,
            null,
            null,
            null);

        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
        Assert.IsTrue(state.ContainsKnownChart(pathOnlyChart));
        Assert.AreEqual(1, state.CreateSnapshot(includeResourceReferences: false).Count);
    }

    [TestMethod]
    public void ContainsKnownChart_DoesNotMatchPathlessOwnerBackedChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);

        Assert.IsFalse(state.ContainsKnownChart(ChartFileProjection.FromBmsStorageOwnerIdentity(bmsFile)));
    }

    [TestMethod]
    public void ContainsKnownChart_DoesNotMatchOwnerWhoseCurrentPathDisappeared()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string oldDirectory = Path.GetDirectoryName(oldPath);
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile.path = null;

        Assert.IsFalse(state.ContainsKnownChart(ChartFileProjection.FromBmsStorageOwnerIdentity(bmsFile)));
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath]).Count);
        Assert.AreEqual(0, index.GetChartRefsUnderRealPath(oldDirectory).Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(oldDirectory, null));
    }

    [TestMethod]
    public void UpsertStorageRows_ReplacesExistingSameKindPathRowsAndPreservesStorageOrder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string replacedBmsPath = Path.Combine("C:\\Installed", "Bms", "replace.bms");
        string replacedBmsonPath = Path.Combine("C:\\Installed", "Bmson", "replace.bmson");
        var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        var replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedBmsPath);
        var newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedBmsPath);
        var addedBms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "Bms", "added.bms"));
        var keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "aaa.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var replacedBmson = CreateBmsonSong(replacedBmsonPath, "ffffffffffffffffffffffffffffffff");
        var newBmson = CreateBmsonSong(replacedBmsonPath, "11111111111111111111111111111111");
        var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "zzz.bmson"), "22222222222222222222222222222222");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([keptBms, replacedBms], [replacedBmson, keptBmson]);

        state.UpsertStorageRows([newBms, addedBms], [newBmson, addedBmson]);
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(6, snapshot.Count);
        Assert.AreSame(keptBms, snapshot[0].GetBmsStorageOwner());
        Assert.AreSame(newBms, snapshot[1].GetBmsStorageOwner());
        Assert.AreSame(addedBms, snapshot[2].GetBmsStorageOwner());
        Assert.AreSame(keptBmson, snapshot[3].GetBmsonStorageOwner());
        Assert.AreSame(newBmson, snapshot[4].GetBmsonStorageOwner());
        Assert.AreSame(addedBmson, snapshot[5].GetBmsonStorageOwner());
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_UsesCachedIndexWithoutCopyingRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);

        OwnedDuplicateChartRowSnapshot first = state.CreateDuplicateChartRowSnapshot();
        OwnedDuplicateChartRowSnapshot second = state.CreateDuplicateChartRowSnapshot();
        DuplicateChartRow bmsRow = first.Rows.Single(row => row.BmsFile != null);
        ChartFile materialized = bmsRow.CreateChart();

        Assert.IsTrue(state.IsDuplicateChartRowSnapshotInitialized);
        Assert.AreSame(first, second);
        Assert.AreEqual(2, first.Rows.Count);
        Assert.AreEqual(1, first.DuplicateHashCount);
        Assert.AreEqual(2, first.DuplicateHashRowCount);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", first.DuplicateHashBuckets.Single().LookupHash);
        Assert.AreEqual(2, first.DuplicateHashBuckets.Single().Rows.Count);
        CollectionAssert.AreEquivalent(new[] { bmsFile }, first.BmsStorageRows.ToArray());
        Assert.IsNotNull(materialized);
        Assert.IsNull(bmsRow.Chart);
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_TracksRemovePathChangeAndUpsert()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "move.bms");
        string newPath = Path.Combine("C:\\Installed", "Moved", "move.bms");
        var movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var removedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "removed.bms"));
        var addedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "added.bms"));
        var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "dddddddddddddddddddddddddddddddd");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([movedBms, removedBms], []);
        OwnedDuplicateChartRowSnapshot originalSnapshot = state.CreateDuplicateChartRowSnapshot();

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromOwnerReference(removedBms)]);
        movedBms.path = newPath;
        state.ApplyPathChanges(
        [
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);
        state.UpsertStorageRows([addedBms], [addedBmson]);

        OwnedDuplicateChartRowSnapshot snapshot = state.CreateDuplicateChartRowSnapshot();
        Assert.AreEqual(2, originalSnapshot.Rows.Count);
        Assert.AreNotSame(originalSnapshot, snapshot);
        CollectionAssert.AreEqual(new[] { movedBms, addedBms }, snapshot.BmsStorageRows.ToArray());
        Assert.IsFalse(snapshot.Rows.Any(row => ReferenceEquals(row.BmsFile, removedBms)));
        Assert.AreEqual(newPath, snapshot.Rows.Single(row => ReferenceEquals(row.BmsFile, movedBms)).Path);
        Assert.IsTrue(snapshot.Rows.Any(row => ReferenceEquals(row.BmsFile, addedBms)));
        Assert.IsTrue(snapshot.Rows.Any(row => ReferenceEquals(row.BmsonSong, addedBmson)));
        Assert.AreEqual(0, snapshot.DuplicateHashCount);
        Assert.AreEqual(0, snapshot.DuplicateHashRowCount);
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_BmsonOnlyRemoveReusesBmsStorageRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        var removedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "removed.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "kept.bmson"), "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [removedBmson, keptBmson]);
        OwnedDuplicateChartRowSnapshot originalSnapshot = state.CreateDuplicateChartRowSnapshot();

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromOwnerReference(removedBmson)]);

        OwnedDuplicateChartRowSnapshot snapshot = state.CreateDuplicateChartRowSnapshot();
        Assert.AreNotSame(originalSnapshot, snapshot);
        Assert.AreSame(originalSnapshot.BmsStorageRows, snapshot.BmsStorageRows);
        Assert.IsFalse(snapshot.Rows.Any(row => ReferenceEquals(row.BmsonSong, removedBmson)));
        Assert.IsTrue(snapshot.Rows.Any(row => ReferenceEquals(row.BmsonSong, keptBmson)));
        Assert.AreEqual(0, snapshot.DuplicateHashCount);
        CollectionAssert.AreEqual(new[] { bmsFile }, snapshot.BmsStorageRows.ToArray());
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_TracksMd5DigestChangesOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string path = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", path, new string('1', 64));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        OwnedDuplicateChartRowSnapshot snapshot = state.CreateDuplicateChartRowSnapshot();
        DuplicateChartRow originalRow = snapshot.Rows.Single();

        state.ApplyDigestChanges(
        [
            new LibraryChartDigestChange(
                LibraryChartKind.Bms,
                path,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('1', 64),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('2', 64))
        ]);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", originalRow.LookupHash);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", state.CreateDuplicateChartRowSnapshot().Rows.Single().LookupHash);
        Assert.AreEqual(0, state.CreateDuplicateChartRowSnapshot().DuplicateHashCount);

        state.ApplyDigestChanges(
        [
            new LibraryChartDigestChange(
                LibraryChartKind.Bms,
                path,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('2', 64),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                new string('2', 64))
        ]);
        Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", state.CreateDuplicateChartRowSnapshot().Rows.Single().LookupHash);
        Assert.AreEqual(0, state.CreateDuplicateChartRowSnapshot().DuplicateHashCount);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", originalRow.LookupHash);
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var library = new BMSLibrary(songDbPath);
            using var initialBmsFilesNotification = new ManualResetEventSlim(false);
            System.ComponentModel.PropertyChangedEventHandler initialHandler = delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    initialBmsFilesNotification.Set();
                }
            };
            library.PropertyChanged += initialHandler;
            library.BMSFiles = [first, second];
            library.BmsonSongs = [];
            library.DuplicateChartGroups = [];
            Assert.IsTrue(initialBmsFilesNotification.Wait(TimeSpan.FromSeconds(5)));
            library.PropertyChanged -= initialHandler;
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    bmsFilesChanged++;
                }
            };
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true,
                InvalidateParentFolderCache = true,
                ClearDuplicatedCache = true
            };
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReferenceChart(initialSnapshot[0]));

            InvokeApplyLibraryMutationDelta(library, delta);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(second, library.BMSFiles[0]);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void BMSFilesReplacement_InvalidatesOwnedCollectionVersionAndRebuildsOnNextView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var replacement = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [first],
                BmsonSongs = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            library.BMSFiles = [replacement];
            List<ChartFile> rebuiltSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, rebuiltSnapshot.Count);
            Assert.AreSame(replacement, rebuiltSnapshot[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void StorageRowPropertiesExposeReadOnlyViews()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            Assert.IsFalse(library.BMSFiles is List<BMSFile>);
            Assert.IsFalse(library.BmsonSongs is List<LR2SongDBExtended.bmson_song>);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<BMSFile>)library.BMSFiles).Add(CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"))));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<LR2SongDBExtended.bmson_song>)library.BmsonSongs).Clear());
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
        });
    }

    [TestMethod]
    public void StorageRowSettersCopyInputLists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            List<BMSFile> inputBmsFiles = [bmsFile];
            List<LR2SongDBExtended.bmson_song> inputBmsonSongs = [bmsonSong];
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = inputBmsFiles,
                BmsonSongs = inputBmsonSongs
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);

            inputBmsFiles.Clear();
            inputBmsonSongs.Clear();
            List<ChartFile> afterInputMutationSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreEqual(2, afterInputMutationSnapshot.Count);
            Assert.AreSame(bmsFile, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bms).GetBmsStorageOwner());
            Assert.AreSame(bmsonSong, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void HasOwnedChartUnderRealPath_UsesOwnedCollectionForBmsAndBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bms")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bmson")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath("C:\\Installed"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath("C:\\Install"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(null));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregistersBmsonStorageRowsInLibraryBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateBmsonSong(Path.Combine("C:\\Installed", "First", "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var second = CreateBmsonSong(Path.Combine("C:\\Installed", "Second", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [first, second]);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsonSongsChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                {
                    bmsonSongsChanged++;
                }
            };
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(first));

            InvokeApplyLibraryMutationDelta(library, delta);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreSame(second, library.BmsonSongs.Single());
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
            Assert.IsTrue(batch.NotifiesBmsonSongs);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterPathlessStorageRowIsNoOpForOwnedBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var pathless = CreateBmsonSong(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var kept = CreateBmsonSong(Path.Combine("C:\\Installed", "Kept", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = [pathless, kept]
            };
            Assert.AreEqual(1, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(pathless));

            InvokeApplyLibraryMutationDelta(library, delta);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(2, library.BmsonSongs.Count);
            Assert.IsTrue(library.BmsonSongs.Contains(pathless));
            Assert.IsTrue(library.BmsonSongs.Contains(kept));
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(kept, afterSnapshot[0].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_RoutesUnregisterThroughOwnedMutationAndInstalledLookupDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "DeleteTarget");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService())
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [],
                DuplicateChartGroups = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsFile.hash));

            library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(bmsFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.AreEqual(0, library.BMSFiles.Count);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(0, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(bmsFile.hash));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterAppliesCurrentResourceHealthIndexDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Resource", "chart.bms"));
            bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
            {
                hash = bmsFile.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            }, suppressPropertyChanged: true);
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(bmsFile)]);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsFile));

            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsFalse(IsResourceHealthIndexInvalidated(library));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PathChangeInvalidatesCurrentResourceHealthIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ResourceMutation_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(newBmsPath, "#PLAYER 1");
            try
            {
                var oldSnapshotFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                oldSnapshotFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(oldSnapshotFile)
                {
                    hash = oldSnapshotFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                TestableBmsFile bmsFile = CreateFile(oldSnapshotFile.hash, newBmsPath);
                bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
                {
                    hash = bmsFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                var library = new BMSLibrary(songDbPath);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, []);
                SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(oldSnapshotFile)]);
                Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);

                Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                Assert.IsTrue(IsResourceHealthIndexInvalidated(library));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_DispatchesParentFolderOnceAndClearsDuplicateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedMutationDispatch_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(newBmsPath, "#PLAYER 1");
            File.WriteAllText(newBmsonPath, "{}");
            try
            {
                TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", newBmsPath);
                LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(oldBmsonPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                var library = new BMSLibrary(songDbPath);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
                SetDuplicateChartGroupsWithoutNotification(library, []);
                int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int ownedCollectionVersionChanged = 0;
                int bmsFilesChanged = 0;
                int bmsonSongsChanged = 0;
                string? firstChange = null;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int parentFolderVersionChanged = 0;
                library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    firstChange ??= args.PropertyName;
                    if (args.PropertyName == "OwnedChartCollectionVersion")
                    {
                        ownedCollectionVersionChanged++;
                    }
                    if (args.PropertyName == "BMSFiles")
                    {
                        bmsFilesChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        bmsonSongsChanged++;
                    }
                    if (args.PropertyName == "BMSParentFolderListCacheVersion")
                    {
                        parentFolderVersionChanged++;
                    }
                };
                var delta = new LibraryMutationDelta
                {
                    InvalidateParentFolderCache = true,
                    ClearDuplicatedCache = true,
                    NotifyStorageRowPathChanges = true
                };
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
                Assert.AreEqual(1, ownedCollectionVersionChanged);
                Assert.AreEqual(0, bmsFilesChanged);
                Assert.AreEqual(0, bmsonSongsChanged);
                Assert.IsTrue(batch.NotifiesStorageRows);
                Assert.IsTrue(batch.NotifiesBmsFiles);
                Assert.IsTrue(batch.NotifiesBmsonSongs);
                Assert.AreEqual("OwnedChartCollectionVersion", firstChange);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, parentFolderVersionChanged);
                Assert.IsNull(library.DuplicateChartGroups);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_FallbackInvalidatesParentFolderAndDuplicateCacheOnFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedMutationFailure_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            try
            {
                TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                var library = new BMSLibrary(songDbPath)
                {
                    BMSFiles = [bmsFile],
                    BmsonSongs = [],
                    DuplicateChartGroups = []
                };
                SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(bmsFile)]);
                Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int parentFolderVersionChanged = 0;
                library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == "BMSParentFolderListCacheVersion")
                    {
                        parentFolderVersionChanged++;
                    }
                };
                var delta = new LibraryMutationDelta
                {
                    InvalidateParentFolderCache = true,
                    ClearDuplicatedCache = true
                };
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });

                FileNotFoundException exception = Assert.ThrowsException<FileNotFoundException>(() =>
                    InvokeApplyLibraryMutationDelta(library, delta));

                Assert.IsNotNull(exception);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, parentFolderVersionChanged);
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_UpsertsOwnedCollectionWithoutRebuild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string replacedBmsPath = Path.Combine("C:\\Installed", "Bms", "replace.bms");
            string replacedBmsonPath = Path.Combine("C:\\Installed", "Bmson", "replace.bmson");
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedBmsPath);
            var newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedBmsPath);
            var keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "aaa.bmson"), "dddddddddddddddddddddddddddddddd");
            var replacedBmson = CreateBmsonSong(replacedBmsonPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var newBmson = CreateBmsonSong(replacedBmsonPath, "ffffffffffffffffffffffffffffffff");
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [keptBms, replacedBms]);
            SetLibraryBmsonSongsWithoutNotification(library, [replacedBmson, keptBmson]);
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(replacedBms)]);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int ownedCollectionVersionChanged = 0;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            int bmsonSongsChanged = 0;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "OwnedChartCollectionVersion")
                {
                    ownedCollectionVersionChanged++;
                }
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                {
                    bmsonSongsChanged++;
                }
            };
            SetDuplicateChartGroupsWithoutNotification(library, []);

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newBms], [newBmson]));
            List<ChartFile> snapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
            Assert.AreEqual(1, ownedCollectionVersionChanged);
            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsTrue(batch.NotifiesBmsonSongs);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(2, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsFalse(IsResourceHealthIndexInvalidated(library));
            Assert.AreEqual(4, snapshot.Count);
            Assert.AreSame(keptBms, snapshot[0].GetBmsStorageOwner());
            Assert.AreSame(newBms, snapshot[1].GetBmsStorageOwner());
            Assert.AreSame(keptBmson, snapshot[2].GetBmsonStorageOwner());
            Assert.AreSame(newBmson, snapshot[3].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_InvalidatedResourceHealthIndexDoesNotRebuildForDeltaFallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [keptBms]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(keptBms)]);
            SetPrivateField(library, "resourceHealthIndexInvalidated", true);

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms], []));

            Assert.IsTrue(IsResourceHealthIndexInvalidated(library));
            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BuiltLookupUpsertUsesOwnedPathExactView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string bmsPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
            string canonicalVariantBmsPath = Path.Combine("C:\\Installed", "Shared", ".", "chart.bms");
            string bmsonPath = Path.Combine("C:\\Installed", "Shared", "chart.bmson");
            var oldBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
            var newBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", canonicalVariantBmsPath);
            var bmsonSong = CreateBmsonSong(bmsonPath, "cccccccccccccccccccccccccccccccc");
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [oldBms]);
            SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);

            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(oldBms.hash));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsonSong.md5));

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newBms], []));
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            List<ChartFile> snapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(oldBms.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(newBms.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(bmsonSong.md5));
            Assert.AreEqual(1, updatedLookup.GetPrimaryHashCount(newBms.hash));
            Assert.AreEqual(2, snapshot.Count);
            Assert.AreSame(newBms, snapshot.Single(chart => chart.Kind == ChartFileKind.Bms).GetBmsStorageOwner());
            Assert.AreSame(bmsonSong, snapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void AutoRenameAllChartFolders_RootOnlyChartsAreNotActionableTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath), "LibraryRoot");
            Directory.CreateDirectory(rootPath);
            string chartPath = Path.Combine(rootPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new BMSLibrary(songDbPath)
            {
                SearchTargets = [rootPath]
            };
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);

            Assert.IsFalse(library.HasAutoRenameAllChartFolderTargets(rootPath));
            Assert.IsFalse(library.AutoRenameAllChartFolders(rootPath));
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_InvalidatesOwnedCollectionOnFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var staleOverlayBms = CreateFile("ffffffffffffffffffffffffffffffff", Path.Combine("C:\\Installed", "Bms", "stale.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var duplicateAddedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", ".", "added.bms"));
            var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [keptBms],
                BmsonSongs = []
            };
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            SetDuplicateChartGroupsWithoutNotification(library, []);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int parentFolderVersionChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
            };
            ApplyInstallDestinationChange(library, staleOverlayBms, Path.Combine("C:\\Install", "Stale"));
            Assert.IsTrue(GetInstallDestinationRuntimeStateCount(library) > 0);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms, duplicateAddedBms], [addedBmson])));

            Assert.IsNotNull(exception);
            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(0, GetInstallDestinationRuntimeStateCount(library));
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_ForceInvalidatesResourceHealthIndexOnFailureDuringSuppression()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var duplicateAddedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", ".", "added.bms"));
            var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [keptBms],
                BmsonSongs = []
            };
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(keptBms)]);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);

            InvalidOperationException exception;
            using (InvokeSuppressResourceHealthIndexInvalidation(library))
            {
                exception = Assert.ThrowsException<InvalidOperationException>(() =>
                    InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms, duplicateAddedBms], [addedBmson])));
            }

            Assert.IsNotNull(exception);
            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
        });
    }

    [TestMethod]
    public void ApplyLibraryFileScanStorageMutation_UpdatesAdjacentIndexesThroughOwnedMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var removedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "removed.bms"));
            var addedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var pathlessBms = CreateFile("ffffffffffffffffffffffffffffffff", string.Empty);
            string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "same.bmson");
            var oldBmson = CreateBmsonSong(bmsonPath, "dddddddddddddddddddddddddddddddd");
            var updatedBmson = CreateBmsonSong(bmsonPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var pathlessBmson = CreateBmsonSong(string.Empty, "11111111111111111111111111111111");
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [keptBms, removedBms, pathlessBms]);
            SetLibraryBmsonSongsWithoutNotification(library, [oldBmson, pathlessBmson]);
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(removedBms.hash));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(oldBmson.md5));
            SetCurrentResourceHealthIndex(library, ChartFileProjection.FromStorageRows(
                [keptBms, removedBms, pathlessBms],
                [oldBmson, pathlessBmson],
                includeWarningSnapshot: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false));
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsFilesChanged = 0;
            int bmsonSongsChanged = 0;
            int normalLibraryRefreshNotifications = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == "BmsonSongs")
                {
                    bmsonSongsChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    normalLibraryRefreshNotifications++;
                }
            };
            var result = new SongTableFileCheckResult
            {
                HasDbDiff = true
            };
            result.DeletedPaths.Add(removedBms.path);
            result.AddedFiles.Add(addedBms);
            result.AddedBmsonSongs.Add(updatedBmson);
            result.NextFiles.AddRange([keptBms, addedBms]);
            result.NextBmsonSongs.Add(updatedBmson);

            InvokeApplyLibraryFileScanStorageMutation(library, result, "test");
            List<ChartFile> snapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            CollectionAssert.AreEqual(new[] { keptBms, addedBms }, library.BMSFiles.ToArray());
            CollectionAssert.AreEqual(new[] { updatedBmson }, library.BmsonSongs.ToArray());
            Assert.AreEqual(3, snapshot.Count);
            Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), keptBms)));
            Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), addedBms)));
            Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), updatedBmson)));
            Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), removedBms)));
            Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), pathlessBms)));
            Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), oldBmson)));
            Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), pathlessBmson)));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(removedBms.hash));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(oldBmson.md5));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(pathlessBms.hash));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(pathlessBmson.md5));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(addedBms.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(updatedBmson.md5));
            Assert.IsTrue(IsResourceHealthIndexInvalidated(library));
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.AreEqual(1, normalLibraryRefreshNotifications);
            Assert.IsFalse(batch.ResetsPriorNotifications);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsTrue(batch.NotifiesBmsonSongs);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
        });
    }

    [TestMethod]
    public void ApplyLibraryFileScanStorageMutation_ReplacesOwnedCollectionFromNextRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var lastByPath = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "z.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var firstByPath = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "a.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var middleByPath = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "m.bmson"), "cccccccccccccccccccccccccccccccc");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = [lastByPath, firstByPath]
            };
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            var result = new SongTableFileCheckResult
            {
                HasDbDiff = true
            };
            result.AddedBmsonSongs.Add(middleByPath);
            result.NextBmsonSongs.AddRange([lastByPath, firstByPath, middleByPath]);

            InvokeApplyLibraryFileScanStorageMutation(library, result, "order");
            List<LR2SongDBExtended.bmson_song> snapshotOwners = [.. InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library)
                .Where(chart => chart.Kind == ChartFileKind.Bmson)
                .Select(chart => chart.GetBmsonStorageOwner())];

            CollectionAssert.AreEqual(new[] { lastByPath, firstByPath, middleByPath }, library.BmsonSongs.ToArray());
            CollectionAssert.AreEqual(new[] { lastByPath, firstByPath, middleByPath }, snapshotOwners);
        });
    }

    [TestMethod]
    public void ApplyLibraryFileScanStorageMutation_DoesNotBuildOwnedCollectionWhenUninitialized()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var removedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "removed.bms"));
            var keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "keep.bmson"), "cccccccccccccccccccccccccccccccc");
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [keptBms, removedBms]);
            SetLibraryBmsonSongsWithoutNotification(library, [keptBmson]);
            int bmsFilesChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
            };
            var result = new SongTableFileCheckResult
            {
                HasDbDiff = true
            };
            result.DeletedPaths.Add(removedBms.path);
            result.NextFiles.Add(keptBms);
            result.NextBmsonSongs.Add(keptBmson);

            InvokeApplyLibraryFileScanStorageMutation(library, result, "uninitialized");

            Assert.AreEqual(0, bmsFilesChanged);
            CollectionAssert.AreEqual(new[] { keptBms }, library.BMSFiles.ToArray());
            CollectionAssert.AreEqual(new[] { keptBmson }, library.BmsonSongs.ToArray());
            List<ChartFile> rebuiltSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, rebuiltSnapshot.Count);
            Assert.IsTrue(rebuiltSnapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), keptBms)));
            Assert.IsTrue(rebuiltSnapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), keptBmson)));
            Assert.IsFalse(rebuiltSnapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), removedBms)));
        });
    }

    [TestMethod]
    public void ApplyLibraryFileScanStorageMutation_NoDiffAndNoCurrentResourceHealthSkipsRefreshNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsRowsVersion = library.CatalogStorageRowsVersion.BmsRowsVersion;
            int bmsonRowsVersion = library.CatalogStorageRowsVersion.BmsonRowsVersion;
            int bmsFilesChanged = 0;
            int bmsonSongsChanged = 0;
            int normalLibraryRefreshNotifications = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == "BmsonSongs")
                {
                    bmsonSongsChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    normalLibraryRefreshNotifications++;
                }
            };
            var result = new SongTableFileCheckResult();
            result.NextFiles.Add(bmsFile);
            result.NextBmsonSongs.Add(bmsonSong);

            InvokeApplyLibraryFileScanStorageMutation(library, result, "no_diff");
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.IsFalse(batch.HasRefreshNotification);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.AreEqual(0, normalLibraryRefreshNotifications);
            Assert.AreEqual(bmsRowsVersion, library.CatalogStorageRowsVersion.BmsRowsVersion);
            Assert.AreEqual(bmsonRowsVersion, library.CatalogStorageRowsVersion.BmsonRowsVersion);
            Assert.IsTrue(IsResourceHealthIndexInvalidated(library));
            Assert.AreEqual(2, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryFileScanStorageMutation_NoDiffInvalidatesCurrentResourceHealthOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(bmsFile)]);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsRowsVersion = library.CatalogStorageRowsVersion.BmsRowsVersion;
            int bmsFilesChanged = 0;
            int normalLibraryRefreshNotifications = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    normalLibraryRefreshNotifications++;
                }
            };
            var result = new SongTableFileCheckResult();
            result.NextFiles.Add(bmsFile);

            InvokeApplyLibraryFileScanStorageMutation(library, result, "resource_rescan");
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.IsTrue(IsResourceHealthIndexInvalidated(library));
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(bmsRowsVersion, library.CatalogStorageRowsVersion.BmsRowsVersion);
            Assert.AreEqual(1, normalLibraryRefreshNotifications);
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
        });
    }

    [TestMethod]
    public void CreateInstallDestinationOverlayChartRefSnapshotUnsafe_DeduplicatesRuntimeStateKeys()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Library", "Bms", "chart.bms"), new string('b', 64));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = []
            };
            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine("C:\\Install", "Bms")
            });
            InvokeApplyLibraryMutationDelta(library, delta);

            InstallDestinationOverlayChartRefSnapshot snapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            List<LibraryChartRef> refs = snapshot.GetChartRefsUnderInstallDestination(Path.Combine("C:\\Install", "Bms"));

            Assert.AreEqual(1, refs.Count);
            Assert.AreSame(bmsFile, refs[0].GetBmsStorageOwner());
            Assert.AreEqual(Path.Combine("C:\\Install", "Bms"), refs[0].ToChartFile().InstallDestination);
        });
    }

    [TestMethod]
    public void CreateInstallDestinationOverlayChartRefSnapshotUnsafe_InvalidatesCachedSnapshotOnOverlayUpdate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Library", "Bms", "chart.bms"), new string('b', 64));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = []
            };
            string oldInstallDestination = Path.Combine("C:\\Install", "Old");
            string newInstallDestination = Path.Combine("C:\\Install", "New");
            ApplyInstallDestinationChange(library, bmsFile, oldInstallDestination);
            InstallDestinationOverlayChartRefSnapshot oldSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            Assert.AreSame(oldSnapshot, InvokeCreateInstallDestinationOverlayChartRefSnapshot(library));

            ApplyInstallDestinationChange(library, bmsFile, newInstallDestination);
            InstallDestinationOverlayChartRefSnapshot newSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreNotSame(oldSnapshot, newSnapshot);
            Assert.AreEqual(0, newSnapshot.GetChartRefsUnderInstallDestination(oldInstallDestination).Count);
            List<LibraryChartRef> refs = newSnapshot.GetChartRefsUnderInstallDestination(newInstallDestination);
            Assert.AreEqual(1, refs.Count);
            Assert.AreSame(bmsFile, refs[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void WarmInstallDestinationOverlaySnapshot_BuildsAndReusesOverlaySnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Library", "Bms", "chart.bms"), new string('b', 64));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = []
            };
            string installDestination = Path.Combine("C:\\Install", "WarmOverlay");
            ApplyInstallDestinationChange(library, bmsFile, installDestination);

            BMSLibrary.OwnedAdjacentIndexWarmupResult first = library.WarmInstallDestinationOverlaySnapshot("test");
            BMSLibrary.OwnedAdjacentIndexWarmupResult second = library.WarmInstallDestinationOverlaySnapshot("test");
            InstallDestinationOverlayChartRefSnapshot snapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreEqual("install_destination_overlay", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(1, first.ChartRefCount);
            Assert.AreEqual(1, first.DirectoryCount);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.ChartRefCount, second.ChartRefCount);
            Assert.AreEqual(1, snapshot.GetChartRefsUnderInstallDestination(installDestination).Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterDoesNotPruneSamePathDifferentOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string sharedPath = Path.Combine("C:\\Library", "Bms", "chart.bms");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath, new string('b', 64));
            var duplicateOwner = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath, new string('c', 64));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile, duplicateOwner],
                BmsonSongs = []
            };
            string installDestination = Path.Combine("C:\\Install", "Bms");
            ApplyInstallDestinationChange(library, duplicateOwner, installDestination);
            InstallDestinationOverlayChartRefSnapshot initialSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.GetChartRefsUnderInstallDestination(installDestination).Count);

            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsFile));
            InvokeApplyLibraryMutationDelta(library, delta);
            InstallDestinationOverlayChartRefSnapshot afterSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreEqual(1, afterSnapshot.GetChartRefsUnderInstallDestination(installDestination).Count);
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(duplicateOwner, library.BMSFiles[0]);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterBmsonDoesNotPruneSamePathDifferentOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string sharedPath = Path.Combine("C:\\Library", "Bmson", "chart.bmson");
            var bmsonSong = CreateBmsonSong(sharedPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var duplicateOwner = CreateBmsonSong(sharedPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = [bmsonSong, duplicateOwner]
            };

            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsonSong));
            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreSame(duplicateOwner, library.BmsonSongs[0]);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_OverlayOnlyClearsMetadataCacheAndPublishesOverlayRefreshWithoutLookupRebuild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, new string('b', 64));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = []
            };
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            InvokeResolveInstallDestinationMetadataProfile(library, chartDirectory);
            Assert.AreEqual(1, GetInstallEstimationMetadataProfileCacheCount(library));
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsFilesChanged = 0;
            int normalLibraryRefreshNotifications = 0;
            int ownedCollectionVersionChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    normalLibraryRefreshNotifications++;
                }
                if (args.PropertyName == "OwnedChartCollectionVersion")
                {
                    ownedCollectionVersionChanged++;
                }
            };
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true
            };
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine(chartDirectory, "Overlay")
            });

            InvokeApplyLibraryMutationDelta(library, delta);
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);

            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(bmsFile.hash));
            CollectionAssert.AreEqual(initialLookup.Md5Directories[bmsFile.hash].ToArray(), updatedLookup.Md5Directories[bmsFile.hash].ToArray());
            Assert.AreEqual(0, GetInstallEstimationMetadataProfileCacheCount(library));
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(1, normalLibraryRefreshNotifications);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsFalse(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.AreEqual(0, ownedCollectionVersionChanged);
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_BuildsPrimaryLookupWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            IPrimaryHashLookup lookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsonSong.md5));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_ExcludesOnlyPrimaryHashCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var firstBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var secondBmsFile = CreateFile(firstBmsFile.hash, Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var otherBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [firstBmsFile, secondBmsFile, otherBmsFile],
                BmsonSongs = []
            };

            IPrimaryHashLookup excludingOne = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [ChartFileProjection.FromBmsFile(firstBmsFile, includeWarningSnapshot: false, includeResourceReferences: false)]);
            IPrimaryHashLookup excludingBoth = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [
                    ChartFileProjection.FromBmsFile(firstBmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                    ChartFileProjection.FromBmsFile(secondBmsFile, includeWarningSnapshot: false, includeResourceReferences: false)
                ]);

            Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(firstBmsFile.hash));
            Assert.IsTrue(excludingOne.ContainsPrimaryHash(firstBmsFile.hash));
            Assert.AreEqual(0, excludingBoth.GetPrimaryHashCount(firstBmsFile.hash));
            Assert.IsFalse(excludingBoth.ContainsPrimaryHash(firstBmsFile.hash));
            Assert.IsTrue(excludingBoth.ContainsPrimaryHash(otherBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UpdatesPrimaryLookupWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Removed", "chart.bms"));
            var keptBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Kept", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [removedBmsFile, keptBmsFile],
                BmsonSongs = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(removedBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBmsFile));

            InvokeApplyLibraryMutationDelta(library, delta);
            IPrimaryHashLookup updatedLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(removedBmsFile.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(keptBmsFile.hash));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_UpdatesPrimaryLookupWithoutBuildingOwnedRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var initialBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Initial", "chart.bms"));
            var addedBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Added", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [initialBmsFile],
                BmsonSongs = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(initialBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBmsFile], []));
            IPrimaryHashLookup updatedLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(initialBmsFile.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(addedBmsFile.hash));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void WarmOwnedRealPathDirectoryView_BuildsAndReusesOwnedRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.Combine("C:\\Installed", "Warmup");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(root, "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine(root, "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            BMSLibrary.OwnedAdjacentIndexWarmupResult first = library.WarmOwnedRealPathDirectoryView("test");
            BMSLibrary.OwnedAdjacentIndexWarmupResult second = library.WarmOwnedRealPathDirectoryView("test");

            Assert.AreEqual("real_path", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(2, first.ChartRefCount);
            Assert.IsTrue(first.DirectDirectoryCount >= 2);
            Assert.IsTrue(first.SubtreeDirectoryCount >= 1);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.ChartRefCount, second.ChartRefCount);
        });
    }

    [TestMethod]
    public void WarmInstalledPrimaryHashLookup_BuildsWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "PrimaryWarmup", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "PrimaryWarmup", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            Assert.IsFalse(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));

            BMSLibrary.InstalledPrimaryHashWarmupResult first = library.WarmInstalledPrimaryHashLookup("test");
            BMSLibrary.InstalledPrimaryHashWarmupResult second = library.WarmInstalledPrimaryHashLookup("test");

            Assert.AreEqual("installed_primary_hash", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(2, first.PrimaryHashCount);
            Assert.AreEqual(1, first.BmsCount);
            Assert.AreEqual(1, first.BmsonCount);
            Assert.IsFalse(first.FullDirectoryLookupInitialized);
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.PrimaryHashCount, second.PrimaryHashCount);
            Assert.AreEqual(0, second.BmsCount);
            Assert.AreEqual(0, second.BmsonCount);
            Assert.IsFalse(second.FullDirectoryLookupInitialized);
        });
    }

    [TestMethod]
    public void DispatchOwnedPotentialDigestChanges_InvalidatesPrimaryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "PotentialDigest", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));

            InvokeDispatchOwnedPotentialDigestChanges(
                library,
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false)],
                "test_potential_digest");

            Assert.IsFalse(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_DoesNotHideOverlayOnlyRefreshBehindOtherStorageRowNotifications()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed");
            Directory.CreateDirectory(chartDirectory);
            string overlayChartPath = Path.Combine(chartDirectory, "overlay.bms");
            string unregisterChartPath = Path.Combine(chartDirectory, "unregister.bms");
            File.WriteAllText(overlayChartPath, "#PLAYER 1");
            File.WriteAllText(unregisterChartPath, "#PLAYER 1");
            var overlayBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", overlayChartPath);
            var unregisterBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", unregisterChartPath);
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [overlayBms, unregisterBms]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            var overlayDelta = new LibraryMutationDelta();
            overlayDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(overlayBms, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine(chartDirectory, "Overlay")
            });
            var unregisterDelta = new LibraryMutationDelta();
            unregisterDelta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(unregisterBms));

            InvokeApplyLibraryMutationDelta(library, overlayDelta);
            InvokeApplyLibraryMutationDelta(library, unregisterDelta);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_DoesNotHideOverlayRefreshInSameStorageRowNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed");
            Directory.CreateDirectory(chartDirectory);
            string overlayChartPath = Path.Combine(chartDirectory, "overlay.bms");
            string movedChartPath = Path.Combine(chartDirectory, "moved.bms");
            string oldMovedChartPath = Path.Combine(chartDirectory, "old", "moved.bms");
            File.WriteAllText(overlayChartPath, "#PLAYER 1");
            File.WriteAllText(movedChartPath, "#PLAYER 1");
            var overlayBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", overlayChartPath);
            var movedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", movedChartPath);
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [overlayBms, movedBms]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            var delta = new LibraryMutationDelta
            {
                NotifyStorageRowPathChanges = true
            };
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(overlayBms, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine(chartDirectory, "Overlay")
            });
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(movedBms, includeWarningSnapshot: false, includeResourceReferences: false),
                OldPath = oldMovedChartPath,
                NewPath = movedChartPath
            });

            InvokeApplyLibraryMutationDelta(library, delta);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
        });
    }

    [TestMethod]
    public void BuildInlineChartInfo_DispatchesDigestMutationToOwnedAdjacentIndexes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "InlineDigest");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE hash update\r\n#BPM 120\r\n#00111:01\r\n", System.Text.Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, null);
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetDuplicateChartGroupsWithoutNotification(library, []);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            BMSLibrary.PlaylistSummaryOwnedHashSnapshot initialSummary = library.GetPlaylistSummaryOwnedHashSnapshot();
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsFilesChanged = 0;
            int ownedCollectionVersionChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == "OwnedChartCollectionVersion")
                {
                    ownedCollectionVersionChanged++;
                }
            };

            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_digest",
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);

            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            BMSLibrary.PlaylistSummaryOwnedHashSnapshot updatedSummary = library.GetPlaylistSummaryOwnedHashSnapshot();
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, result.DigestChanges.Count);
            Assert.AreEqual(snapshot.Md5, bmsFile.hash);
            Assert.AreEqual(snapshot.Sha256, bmsFile.sha256);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(snapshot.Md5));
            Assert.IsTrue(initialSummary.Md5Hashes.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsFalse(updatedSummary.Md5Hashes.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(updatedSummary.Md5Hashes.Contains(snapshot.Md5));
            Assert.AreNotEqual(initialSummary.Version, updatedSummary.Version);
            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(1, ownedCollectionVersionChanged);
        });
    }

    [TestMethod]
    public void BuildInlineChartInfo_UpsertsSongRowsWithAppliedChartInfoColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "InlineChartInfo");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE inline chart info\r\n#ARTIST artist\r\n#BPM 180\r\n#PLAYLEVEL 12\r\n#DIFFICULTY 4\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var bmsFile = CreateFile(snapshot.Md5, chartPath, snapshot.Sha256);
            var library = new BMSLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetDuplicateChartGroupsWithoutNotification(library, []);

            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_chart_info_song_row",
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);

            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(12, bmsFile.level);
            Assert.AreEqual(4, bmsFile.difficulty);
            Assert.AreEqual(180, bmsFile.maxbpm);
            Assert.AreEqual(180, bmsFile.minbpm);
            Assert.IsTrue(bmsFile.karinotes > 0);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == chartPath);
            Assert.AreEqual(12, row.level);
            Assert.AreEqual(4, row.difficulty);
            Assert.AreEqual(180, row.maxbpm);
            Assert.AreEqual(180, row.minbpm);
            Assert.IsTrue(row.karinotes > 0);
        });
    }

    [TestMethod]
    public void DispatchOwnedChartDigestChanges_ShaOnlyBmsChangeKeepsPrimaryHashCaches()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine("C:\\Installed", "ShaOnly", "chart.bms");
            string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string oldSha256 = new('b', 64);
            string newSha256 = new('c', 64);
            var bmsFile = CreateFile(md5, chartPath, oldSha256);
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [],
                DuplicateChartGroups = []
            };
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            BMSLibrary.PlaylistSummaryOwnedHashSnapshot initialSummary = library.GetPlaylistSummaryOwnedHashSnapshot();
            SetCurrentResourceHealthIndex(library, [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);
            bmsFile.SetSha256(newSha256);

            InvokeDispatchOwnedChartDigestChanges(
                library,
                [new LibraryChartDigestChange(LibraryChartKind.Bms, chartPath, md5, oldSha256, md5, newSha256)],
                "test_sha_only_digest");

            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            BMSLibrary.PlaylistSummaryOwnedHashSnapshot updatedSummary = library.GetPlaylistSummaryOwnedHashSnapshot();
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(md5));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(md5));
            Assert.IsFalse(updatedLookup.Sha256Directories.ContainsKey(oldSha256));
            Assert.IsTrue(updatedLookup.Sha256Directories.ContainsKey(newSha256));
            Assert.IsNotNull(library.DuplicateChartGroups);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.AreNotEqual(initialSummary.Version, updatedSummary.Version);
            Assert.IsFalse(updatedSummary.Sha256Hashes.Contains(oldSha256));
            Assert.IsTrue(updatedSummary.Sha256Hashes.Contains(newSha256));
        });
    }

    [TestMethod]
    public void LibraryChartDigestChange_PathlessOwnedEventThrows()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new LibraryChartDigestChange(
                LibraryChartKind.Bms,
                null,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                null,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                null));
    }

    [TestMethod]
    public void LibraryChartDigestChange_Md5lessExplicitOwnedEventThrows()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new LibraryChartDigestChange(
                LibraryChartKind.Bms,
                Path.Combine("C:\\Installed", "Bms", "chart.bms"),
                null,
                new string('a', 64),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                new string('b', 64)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            new LibraryChartDigestChange(
                LibraryChartKind.Bmson,
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('a', 64),
                null,
                new string('b', 64)));
    }

    [TestMethod]
    public void LibraryChartDigestChange_FactorySkipsInvalidProjectionEvent()
    {
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var bmsFile = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var pathlessBmsFile = CreateFile(md5, null, new string('d', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), null);
        bmsonSong.sha256 = new string('c', 64);
        var pathlessBmsonSong = CreateBmsonSong(null, md5);
        pathlessBmsonSong.sha256 = new string('e', 64);

        Assert.IsNull(LibraryChartDigestChange.FromBms(bmsFile, null, null));
        Assert.IsNull(LibraryChartDigestChange.FromBms(pathlessBmsFile, md5, null));
        Assert.IsNull(LibraryChartDigestChange.FromBmson(bmsonSong, null, null));
        Assert.IsNull(LibraryChartDigestChange.FromBmson(pathlessBmsonSong, md5, null));
    }

    [TestMethod]
    public void LibraryChartDigestChange_PrimaryHashChangedUsesMd5Only()
    {
        var digestChange = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            Path.Combine("C:\\Installed", "Bms", "chart.bms"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('a', 64),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('b', 64));

        Assert.IsFalse(digestChange.PrimaryHashChanged);
        Assert.IsTrue(digestChange.Sha256Changed);
    }

    [TestMethod]
    public void CreateInstalledDisplayPackageForResourceOnlyMerge_UsesDestinationDirectChildrenOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string destinationDirectory = Path.Combine("C:\\Installed", "Destination");
            string otherDirectory = Path.Combine("C:\\Installed", "Other");
            var destinationBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(destinationDirectory, "chart.bms"), new string('b', 64));
            var sameHashOtherBms = CreateFile(destinationBms.hash, Path.Combine(otherDirectory, "chart.bms"), destinationBms.sha256);
            var nestedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine(destinationDirectory, "Nested", "nested.bms"), new string('d', 64));
            var destinationBmson = CreateBmsonSong(Path.Combine(destinationDirectory, "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            destinationBmson.sha256 = new string('f', 64);
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [destinationBms, sameHashOtherBms, nestedBms],
                BmsonSongs = [destinationBmson]
            };
            var overlayDelta = new LibraryMutationDelta();
            overlayDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(destinationBms, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine("C:\\Overlay", "Bms")
            });
            InvokeApplyLibraryMutationDelta(library, overlayDelta);
            var originalPackage = ChartPackage.FromChartEntries([
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(destinationBms, includeWarningSnapshot: false, includeResourceReferences: false)),
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(nestedBms, includeWarningSnapshot: false, includeResourceReferences: false)),
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(destinationBmson, includeWarningSnapshot: false, includeResourceReferences: false))
            ]);

            ChartPackage displayPackage = InvokeCreateInstalledDisplayPackageForResourceOnlyMerge(
                library,
                originalPackage,
                destinationDirectory);

            Assert.IsNotNull(displayPackage);
            Assert.AreEqual(destinationDirectory, displayPackage.path);
            Assert.IsFalse(displayPackage.delete_parent);
            List<ChartFile> displayCharts = [.. displayPackage.ChartEntries.Select(entry => entry.Chart)];
            Assert.AreEqual(2, displayCharts.Count);
            ChartFile displayBms = displayCharts.Single(chart => chart.Kind == ChartFileKind.Bms);
            ChartFile displayBmson = displayCharts.Single(chart => chart.Kind == ChartFileKind.Bmson);
            Assert.AreSame(destinationBms, displayBms.GetBmsStorageOwner());
            Assert.AreEqual(Path.Combine("C:\\Overlay", "Bms"), displayBms.InstallDestination);
            Assert.AreSame(destinationBmson, displayBmson.GetBmsonStorageOwner());
            Assert.IsFalse(displayCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), sameHashOtherBms)));
            Assert.IsFalse(displayCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), nestedBms)));
        });
    }

    [TestMethod]
    public void CreateInstalledDisplayPackageForResourceOnlyMerge_UsesBmsonOnlyOwnedLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string destinationDirectory = Path.Combine("C:\\Installed", "Destination");
            var destinationBmson = CreateBmsonSong(Path.Combine(destinationDirectory, "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            destinationBmson.sha256 = new string('b', 64);
            var otherBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Other", "chart.bmson"), destinationBmson.md5);
            otherBmson.sha256 = destinationBmson.sha256;
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = null,
                BmsonSongs = [destinationBmson, otherBmson]
            };
            var originalPackage = ChartPackage.FromChartEntries([
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(destinationBmson, includeWarningSnapshot: false, includeResourceReferences: false))
            ]);

            ChartPackage displayPackage = InvokeCreateInstalledDisplayPackageForResourceOnlyMerge(
                library,
                originalPackage,
                destinationDirectory);

            Assert.IsNotNull(displayPackage);
            List<ChartFile> displayCharts = [.. displayPackage.ChartEntries.Select(entry => entry.Chart)];
            Assert.AreEqual(1, displayCharts.Count);
            Assert.AreSame(destinationBmson, displayCharts[0].GetBmsonStorageOwner());
            Assert.IsFalse(displayCharts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), otherBmson)));
        });
    }

    private static void AssertChartSnapshotParity(IReadOnlyList<ChartFile> expected, IReadOnlyList<ChartFile> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Kind, actual[i].Kind);
            Assert.AreEqual(expected[i].Path, actual[i].Path);
            Assert.AreEqual(expected[i].Md5, actual[i].Md5);
            Assert.AreEqual(expected[i].Sha256, actual[i].Sha256);
            Assert.AreSame(expected[i].GetBmsStorageOwner(), actual[i].GetBmsStorageOwner());
            Assert.AreSame(expected[i].GetBmsonStorageOwner(), actual[i].GetBmsonStorageOwner());
        }
    }

    private static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(BMSLibrary library)
    {
        return library.CreateOwnedChartInfoFullBackfillTargetSnapshotForDiagnostics();
    }

    private static InstallDestinationOverlayChartRefSnapshot InvokeCreateInstallDestinationOverlayChartRefSnapshot(BMSLibrary library)
    {
        return library.CreateInstallDestinationOverlayChartRefSnapshotForDiagnostics();
    }

    private static InstalledChartLookupIndexSnapshot InvokeCreateInstalledChartLookupSnapshot(BMSLibrary library)
    {
        return library.CreateInstalledChartLookupSnapshotForDiagnostics();
    }

    private static IPrimaryHashLookup InvokeCreateInstalledChartKeySnapshotExcludingCharts(BMSLibrary library, IEnumerable<ChartFile> excluded)
    {
        return library.CreateInstalledChartKeySnapshotExcludingChartsForDiagnostics(excluded);
    }

    private static void InvokeResolveInstallDestinationMetadataProfile(BMSLibrary library, string destinationDirectory)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ResolveInstallDestinationMetadataProfileUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [destinationDirectory]);
    }

    private static int GetInstallEstimationMetadataProfileCacheCount(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("installEstimationMetadataProfileCache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        var cache = (System.Collections.ICollection)fieldInfo.GetValue(library);
        return cache.Count;
    }

    private static int GetInstallDestinationRuntimeStateCount(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("installDestinationRuntimeStatesByKey", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        var states = (System.Collections.ICollection)fieldInfo.GetValue(library);
        return states.Count;
    }

    private static bool IsInstalledChartLookupIndexInitialized(BMSLibrary library)
    {
        return library.IsInstalledChartLookupIndexInitializedForDiagnostics();
    }

    private static bool IsInstalledPrimaryHashLookupInitialized(BMSLibrary library)
    {
        return library.IsInstalledPrimaryHashLookupInitializedForDiagnostics();
    }

    private static ChartPackage InvokeCreateInstalledDisplayPackageForResourceOnlyMerge(
        BMSLibrary library,
        ChartPackage originalPackage,
        string destinationDirectory)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateInstalledDisplayPackageForResourceOnlyMerge", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (ChartPackage)methodInfo.Invoke(library, [originalPackage, destinationDirectory]);
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        var coordinator = new LibraryMutationDeltaApplyCoordinator(new BMSLibrary.LibraryMutationDeltaApplyHost(library));
        coordinator.Apply(delta);
    }

    private static void ApplyInstallDestinationChange(BMSLibrary library, BMSFile bmsFile, string installDestination)
    {
        var delta = new LibraryMutationDelta();
        delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
        {
            Chart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
            NewInstallDestination = installDestination
        });
        InvokeApplyLibraryMutationDelta(library, delta);
    }

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet addedTargets)
    {
        ((IPackageInstallHost)library).ApplyInstalledChartStorageTargets(addedTargets);
    }

    private static void InvokeApplyLibraryFileScanStorageMutation(
        BMSLibrary library,
        SongTableFileCheckResult result,
        string reason)
    {
        library.ApplyFileScanStorageMutation(result, reason);
    }

    private static void InvokeApplyCatalogStorageRowsWithoutNotification(
        BMSLibrary library,
        SongTableFileCheckResult result)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod(
            "ApplyCatalogStorageRows",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(
            library,
            [
                result.NextFiles,
                result.NextBmsonSongs,
                true,
                true,
                false,
                false
            ]);
    }

    private static ChartInfoInlineBuildResult InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
        BMSLibrary library,
        string reason,
        IEnumerable<ChartFile> charts)
    {
        var coordinator = new ChartInfoInlineBuildCoordinator(new BMSLibrary.ChartInfoInlineBuildHost(library));
        return coordinator.BuildAndPersist(reason, charts);
    }

    private static void InvokeDispatchOwnedChartDigestChanges(
        BMSLibrary library,
        IEnumerable<LibraryChartDigestChange> digestChanges,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod(
            "DispatchOwnedChartDigestChanges",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [digestChanges, reason, true]);
    }

    private static void InvokeDispatchOwnedPotentialDigestChanges(
        BMSLibrary library,
        IEnumerable<ChartFile> charts,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod(
            "DispatchOwnedPotentialDigestChanges",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [charts, reason, true]);
    }

    private static IDisposable InvokeSuppressResourceHealthIndexInvalidation(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("SuppressResourceHealthIndexInvalidation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (IDisposable)methodInfo.Invoke(library, []);
    }

    private static void SetLibraryFilesWithoutNotification(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.AddRange(files ?? []);
        result.NextBmsonSongs.AddRange(library.BmsonSongs);
        InvokeApplyCatalogStorageRowsWithoutNotification(library, result);
    }

    private static void SetLibraryBmsonSongsWithoutNotification(BMSLibrary library, IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.AddRange(library.BMSFiles);
        result.NextBmsonSongs.AddRange(songs ?? []);
        InvokeApplyCatalogStorageRowsWithoutNotification(library, result);
    }

    private static void SetDuplicateChartGroupsWithoutNotification(BMSLibrary library, IEnumerable<DuplicateGroup> groups)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("_DuplicateChartGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(library, groups.ToList());
    }

    private static void SetCurrentResourceHealthIndex(BMSLibrary library, IEnumerable<ChartFile> charts)
    {
        var snapshot = ResourceHealthIndexSnapshot.Build(charts, new BmsLibraryMaintenanceService(), version: 1);
        var host = new BMSLibrary.ResourceHealthIndexFullRebuildHost(library);
        host.PublishSnapshot(snapshot);
    }

    private static bool IsResourceHealthIndexInvalidated(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("resourceHealthIndexInvalidated", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return (bool)fieldInfo.GetValue(library);
    }

    private static void SetPrivateField(BMSLibrary library, string fieldName, object value)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(library, value);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedChartCollection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static TestableBmsFile CreateFile(string? hash, string? path, string? sha256 = null)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        file.SetSha256(sha256);
        return file;
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSong(string? path, string? md5)
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = md5
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string? value)
        {
            hash = value;
        }

        public void SetSha256(string? value)
        {
            sha256 = value;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            if (File.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination file already exists.");
                }
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination directory already exists.");
                }
                Directory.Delete(destinationPath, recursive: true);
            }
            Directory.Move(sourcePath, destinationPath);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }
}
