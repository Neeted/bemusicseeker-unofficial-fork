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
public sealed class OwnedChartCollectionReferenceIndexTests
{
    [TestMethod]
    public void CanonicalExactPathQueryReturnsCurrentOwnerAndPreservesOrderKeyAcrossRelocation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "old.bms");
        string newPath = Path.Combine("C:\\Installed", "Bms", "new.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        ChartFile sibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "sibling.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, sibling], []));

        Assert.IsTrue(state.TryGetCanonicalChartRefForExactPath(
            ChartFileKind.Bms,
            oldPath,
            out LibraryChartRef oldRef,
            out OwnedChartCanonicalOrderKey oldOrder));
        Assert.AreEqual(bmsFile.Path, oldRef.Path);
        Assert.AreEqual(bmsFile.Md5, oldRef.Md5);
        Assert.AreEqual(ChartFileKind.Bms, oldRef.Kind);

        bmsFile = bmsFile with { Path = newPath };
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        Assert.IsFalse(state.TryGetCanonicalChartRefForExactPath(
            ChartFileKind.Bms,
            oldPath,
            out _,
            out _));
        Assert.IsTrue(state.TryGetCanonicalChartRefForExactPath(
            ChartFileKind.Bms,
            newPath,
            out LibraryChartRef newRef,
            out OwnedChartCanonicalOrderKey newOrder));
        Assert.AreEqual(bmsFile.Path, newRef.Path);
        Assert.AreEqual(bmsFile.Md5, newRef.Md5);
        Assert.AreEqual(ChartFileKind.Bms, newRef.Kind);
        Assert.AreEqual(oldOrder, newOrder);
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_ReprojectsCurrentStorageOwnerValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "BmsOld", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "BmsonOld", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        string newBmsPath = Path.Combine("C:\\Installed", "BmsNew", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "BmsonNew", "chart.bmson");
        bmsFile = bmsFile with { Path = newBmsPath };
        bmsFile = bmsFile with { Md5 = "dddddddddddddddddddddddddddddddd" };
        bmsFile = bmsFile with { Sha256 = new string('e', 64) };
        bmsonSong = bmsonSong with { Path = newBmsonPath };
        bmsonSong = bmsonSong with { Md5 = "ffffffffffffffffffffffffffffffff" };
        bmsonSong = bmsonSong with { Sha256 = new string('1', 64) };
        ApplyCapturedCurrentValues(state, bmsFile, bmsonSong);

        CanonicalChartResolveResult resolveResult = state.CreateLibraryChartRefIndexSnapshot().ResolveCanonicalCharts([
            LibraryChartRef.FromPath(ChartFileKind.Bms, newBmsPath, bmsFile.Md5, bmsFile.Sha256),
            LibraryChartRef.FromPath(ChartFileKind.Bmson, newBmsonPath, bmsonSong.Md5, bmsonSong.Sha256)
        ]);

        List<LibraryChartRef> refs = resolveResult.CanonicalCharts;
        List<LibraryChartRef> pathRefs = state.CreateLibraryChartRefIndexSnapshot().GetChartRefsByPaths([newBmsPath, newBmsonPath]);
        Assert.AreEqual(2, refs.Count);
        Assert.AreEqual(2, pathRefs.Count);
        LibraryChartRef bmsRef = refs.Single(chart => chart.Kind == ChartFileKind.Bms);
        LibraryChartRef bmsonRef = refs.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.IsTrue(pathRefs.Any(chart => chart.Kind == ChartFileKind.Bms && string.Equals(chart.Path, newBmsPath, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(pathRefs.Any(chart => chart.Kind == ChartFileKind.Bmson && string.Equals(chart.Path, newBmsonPath, StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(newBmsPath, bmsRef.Path);
        Assert.AreEqual(bmsFile.Md5, bmsRef.Md5);
        Assert.AreEqual(bmsFile.Sha256, bmsRef.Sha256);
        Assert.AreEqual(bmsFile.Path, bmsRef.Path);
        Assert.AreEqual(bmsFile.Md5, bmsRef.Md5);
        Assert.AreEqual(ChartFileKind.Bms, bmsRef.Kind);
        Assert.AreEqual(newBmsonPath, bmsonRef.Path);
        Assert.AreEqual(bmsonSong.Md5, bmsonRef.Md5);
        Assert.AreEqual(bmsonSong.Sha256, bmsonRef.Sha256);
        Assert.AreEqual(bmsonSong.Path, bmsonRef.Path);
        Assert.AreEqual(bmsonSong.Md5, bmsonRef.Md5);
        Assert.AreEqual(ChartFileKind.Bmson, bmsonRef.Kind);
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_CachedRefsUseCurrentOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        bmsFile = bmsFile with { Md5 = "cccccccccccccccccccccccccccccccc" };
        bmsFile = bmsFile with { Sha256 = new string('d', 64) };
        bmsonSong = bmsonSong with { Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" };
        bmsonSong = bmsonSong with { Sha256 = new string('f', 64) };
        ApplyCapturedCurrentValues(state, bmsFile, bmsonSong);

        List<LibraryChartRef> refs = index.GetChartRefsByPaths([bmsFile.Path, bmsonSong.Path]);
        LibraryChartRef bmsRef = refs.Single(chart => chart.Kind == ChartFileKind.Bms);
        LibraryChartRef bmsonRef = refs.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(bmsFile.Md5, bmsRef.Md5);
        Assert.AreEqual(bmsFile.Sha256, bmsRef.Sha256);
        Assert.AreEqual(bmsonSong.Md5, bmsonRef.Md5);
        Assert.AreEqual(bmsonSong.Sha256, bmsonRef.Sha256);
    }

    [TestMethod]
    public void CreateChartDirectoriesUnderRealPath_ReturnsDistinctSubtreeDirectoriesWithoutChartSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string rootPath = Path.Combine("C:\\Installed", "Root");
        string childPath = Path.Combine(rootPath, "Child");
        string nestedPath = Path.Combine(childPath, "Nested");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(childPath, "chart.bms"));
        ChartFile sameDirectoryBmson = CreateBmsonSong(Path.Combine(childPath, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile nestedBmson = CreateBmsonSong(Path.Combine(nestedPath, "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [sameDirectoryBmson, nestedBmson]));

        List<string> directories = state.CreateChartDirectoriesUnderRealPath(rootPath);

        CollectionAssert.AreEqual(new[] { childPath, nestedPath }, directories.ToArray());
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_PathlessRowsAreNotPathLookupTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");

        var index = LibraryChartRefIndexSnapshot.FromLibraryChartRefs([
            LibraryChartRef.FromChartFile((bmsFile)),
            LibraryChartRef.FromChartFile((bmsonSong))
        ]);

        Assert.AreEqual(0, index.GetChartRefsByPaths([bmsFile.Path, bmsonSong.Path]).Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath("C:\\Installed", null));
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromChartFile((bmsFile)),
            LibraryChartRef.FromChartFile((bmsonSong))
        ]);
        Assert.AreEqual(0, resolveResult.CanonicalCharts.Count);
    }

    [TestMethod]
    public void CreateSnapshotForMd5Hashes_ProjectsOnlyMatchingCurrentOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        bmsFile = bmsFile with { Md5 = "dddddddddddddddddddddddddddddddd" };
        ApplyCapturedCurrentValues(state, bmsFile);

        List<ChartFile> snapshot = state.CreateSnapshotForMd5Hashes(
            new HashSet<string>(["dddddddddddddddddddddddddddddddd"], StringComparer.OrdinalIgnoreCase),
            includeWarningSnapshot: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(bmsFile.Token, snapshot[0].Token);
        Assert.AreEqual("dddddddddddddddddddddddddddddddd", snapshot[0].Md5);
    }

    [TestMethod]
    public void CreateSnapshotForMd5Hashes_ExcludesMatchingPathlessOwnedRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile pathlessBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", string.Empty, new string('d', 64));
        ChartFile pathlessBmson = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, pathlessBms], [pathlessBmson]));

        List<ChartFile> snapshot = state.CreateSnapshotForMd5Hashes(
            new HashSet<string>([pathlessBms.Md5, pathlessBmson.Md5], StringComparer.OrdinalIgnoreCase),
            includeWarningSnapshot: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(0, snapshot.Count);
    }

    /// <summary>指定したexact keyだけを投影し、同じFSへ解決する別表記を対象へ加えません。</summary>
    [TestMethod]
    public void CreateSnapshotForPaths_ProjectsOnlyRequestedPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile firstBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Target", "first.bms"), new string('b', 64));
        ChartFile secondBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "second.bms"), new string('d', 64));
        ChartFile targetBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Target", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([firstBms, secondBms], [targetBmson]));

        List<ChartFile> snapshot = state.CreateSnapshotForPaths(
            [firstBms.Path, targetBmson.Path, targetBmson.Path],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, firstBms.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, targetBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, secondBms.Token)));

        Assert.AreEqual(0, state.CreateSnapshotForPaths(
            [Path.Combine("C:\\Installed", "Target", ".", "first.bms")],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false).Count);
    }

    [TestMethod]
    public void CreateSnapshotForPaths_ReturnsOnlyFirstOwnedChartForDuplicateStoragePaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([first, second], []), out OwnedChartStorageRowFilterSummary filterSummary);

        List<ChartFile> snapshot = state.CreateSnapshotForPaths(
            [sharedPath],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(first.Token, snapshot[0].Token);
        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
    }

    [TestMethod]
    public void CreateBmsSnapshot_ProjectsOnlyCurrentBmsOwners()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        string newBmsPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        bmsFile = bmsFile with { Path = newBmsPath };
        bmsFile = bmsFile with { Md5 = "dddddddddddddddddddddddddddddddd" };
        ApplyCapturedCurrentValues(state, bmsFile);

        List<ChartFile> snapshot = state.CreateBmsSnapshot(
            includeWarningSnapshot: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual(ChartFileKind.Bms, snapshot[0].Kind);
        Assert.AreSame(bmsFile.Token, snapshot[0].Token);
        Assert.AreEqual(newBmsPath, snapshot[0].Path);
        Assert.AreEqual("dddddddddddddddddddddddddddddddd", snapshot[0].Md5);
    }

    [TestMethod]
    public void CreateSnapshotForDirectChildDirectories_FiltersBeforeProjectionUsingCurrentOwnerPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        ChartFile movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"));
        ChartFile directBmson = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile nestedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms, nestedBms], [directBmson]));
        movedBms = movedBms with { Path = Path.Combine(targetDirectory, "chart.bms") };
        ApplyCapturedCurrentValues(state, movedBms);

        List<ChartFile> snapshot = state.CreateSnapshotForDirectChildDirectories(
            [targetDirectory],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, movedBms.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, directBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, nestedBms.Token)));
    }

    [TestMethod]
    public void CreateSnapshotForDirectChildDirectories_UsesUpdatedDirectoryIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldDirectory = Path.Combine("C:\\Installed", "Old");
        string newDirectory = Path.Combine("C:\\Installed", "New");
        string oldPath = Path.Combine(oldDirectory, "chart.bms");
        string newPath = Path.Combine(newDirectory, "chart.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = newPath };
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
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
        Assert.AreSame(bmsFile.Token, newSnapshot[0].Token);
        Assert.AreEqual(newPath, newSnapshot[0].Path);
    }

    [TestMethod]
    public void CreateSnapshotForSubtreeDirectory_UsesOwnedRefIndexAndCurrentOwnerPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        ChartFile movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"));
        ChartFile nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        ChartFile siblingPrefixBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "TargetPrefix", "prefix.bms"));
        ChartFile directBmson = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms, nestedBms, siblingPrefixBms], [directBmson]));
        movedBms = movedBms with { Path = Path.Combine(targetDirectory, "chart.bms") };
        ApplyCapturedCurrentValues(state, movedBms);

        List<ChartFile> snapshot = state.CreateSnapshotForSubtreeDirectory(
            targetDirectory,
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(3, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, movedBms.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, nestedBms.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, directBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, siblingPrefixBms.Token)));
    }

    [TestMethod]
    public void CreateStorageTargetsForSubtreeDirectory_UsesCurrentValuesAndOwnedRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        ChartFile directBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(targetDirectory, "direct.bms"));
        ChartFile nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        ChartFile siblingPrefixBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "TargetPrefix", "prefix.bms"));
        ChartFile directBmson = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "dddddddddddddddddddddddddddddddd");
        ChartFile pathlessBmson = CreateBmsonSong(null, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([directBms, nestedBms, siblingPrefixBms], [directBmson, pathlessBmson]));

        ChartStorageTargetSet targets = state.CreateStorageTargetsForSubtreeDirectory(targetDirectory);

        CollectionAssert.AreEquivalent(new[] { directBms.Path, nestedBms.Path }, targets.BmsCharts.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(new[] { directBmson.Path }, targets.BmsonCharts.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(3, targets.Charts.Count);
        Assert.IsTrue(targets.Charts.Any(chart => chart.Path == directBms.Path && chart.Md5 == directBms.Md5 && chart.Token != null));
        Assert.IsTrue(targets.Charts.Any(chart => chart.Path == nestedBms.Path && chart.Md5 == nestedBms.Md5 && chart.Token != null));
        Assert.IsTrue(targets.Charts.Any(chart => chart.Path == directBmson.Path && chart.Md5 == directBmson.Md5 && chart.Token != null));
        Assert.IsFalse(targets.Charts.Any(chart => chart.Path == siblingPrefixBms.Path));
        Assert.IsFalse(targets.Charts.Any(chart => ReferenceEquals(chart.Token, pathlessBmson.Token)));
    }

    [TestMethod]
    public void ChartStorageTargetSetFromCharts_CapturesImmutableCommonValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath, new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(bmsonPath, "cccccccccccccccccccccccccccccccc");
        ChartFile staleBmsChart = (bmsFile);
        ChartFile staleBmsonChart = (bmsonSong);
        bmsFile = bmsFile with { Md5 = "dddddddddddddddddddddddddddddddd" };
        bmsFile = bmsFile with { Sha256 = new string('e', 64) };
        bmsonSong = bmsonSong with { Md5 = "ffffffffffffffffffffffffffffffff" };
        bmsonSong = bmsonSong with { Sha256 = new string('1', 64) };

        var targets = ChartStorageTargetSet.FromCharts([staleBmsChart, staleBmsonChart]);

        Assert.AreEqual(2, targets.Charts.Count);
        ChartFile currentBmsChart = targets.Charts.Single(chart => chart.Kind == ChartFileKind.Bms);
        ChartFile currentBmsonChart = targets.Charts.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(staleBmsChart.Md5, currentBmsChart.Md5);
        Assert.AreEqual(staleBmsChart.Sha256, currentBmsChart.Sha256);
        Assert.AreEqual(staleBmsonChart.Md5, currentBmsonChart.Md5);
        Assert.AreEqual(staleBmsonChart.Sha256, currentBmsonChart.Sha256);
        Assert.AreSame(bmsFile.Token, currentBmsChart.Token);
        Assert.AreSame(bmsonSong.Token, currentBmsonChart.Token);
    }

    [TestMethod]
    public void ChartStorageTargetSetFromCharts_RejectsInvalidOwnerCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile pathlessBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string.Empty);
        ChartFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        ChartFile pathlessBmson = CreateBmsonSong(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);

        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([(pathlessBms)]));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([(md5lessBms)]));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([(pathlessBmson)]));
        Assert.ThrowsException<InvalidOperationException>(() =>
            ChartStorageTargetSet.FromCharts([(md5lessBmson)]));
    }

    [TestMethod]
    public void ChartStorageTargetSet_ReturnsDistinctChartDirectoriesFromChartView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedDirectory = Path.Combine("C:\\Installed", "Shared");
        string nestedDirectory = Path.Combine(sharedDirectory, "Nested");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sharedDirectory, "chart.bms"));
        ChartFile sameDirectoryBmson = CreateBmsonSong(Path.Combine(sharedDirectory.ToUpperInvariant(), "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile nestedBmson = CreateBmsonSong(Path.Combine(nestedDirectory, "nested.bmson"), "cccccccccccccccccccccccccccccccc");
        var targets = ChartStorageTargetSet.FromCharts([
            (bmsFile),
            (sameDirectoryBmson),
            (nestedBmson)
        ]);

        List<string> directories = targets.GetDistinctChartDirectories();

        CollectionAssert.AreEqual(new[] { sharedDirectory, nestedDirectory }, directories);
    }

    /// <summary>行のexact lookupと、directory探索の既存正規化を区別します。</summary>
    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_ResolvesPathOnlyAndCountsRealPathSubtree()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        string nestedDirectory = Path.Combine(targetDirectory, "Nested");
        ChartFile directBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(targetDirectory, "direct.bms"));
        ChartFile nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(nestedDirectory, "nested.bms"));
        ChartFile otherBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "other.bms"));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine(targetDirectory, "chart.bmson"), "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([directBms, nestedBms, otherBms], [bmsonSong]));

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(ChartFileKind.Bms, directBms.Path, directBms.Md5, directBms.Sha256),
            LibraryChartRef.FromPath(ChartFileKind.Bmson, bmsonSong.Path, bmsonSong.Md5, bmsonSong.Sha256)
        ]);

        Assert.AreEqual(2, resolveResult.CanonicalCharts.Count);
        Assert.AreEqual(directBms.Path, resolveResult.CanonicalCharts.Single(chart => chart.Kind == ChartFileKind.Bms).Path);
        Assert.AreEqual(directBms.Md5, resolveResult.CanonicalCharts.Single(chart => chart.Kind == ChartFileKind.Bms).Md5);
        Assert.AreEqual(ChartFileKind.Bms, resolveResult.CanonicalCharts.Single(chart => chart.Kind == ChartFileKind.Bms).Kind);
        Assert.AreEqual(bmsonSong.Path, resolveResult.CanonicalCharts.Single(chart => chart.Kind == ChartFileKind.Bmson).Path);
        Assert.AreEqual(bmsonSong.Md5, resolveResult.CanonicalCharts.Single(chart => chart.Kind == ChartFileKind.Bmson).Md5);
        Assert.AreEqual(ChartFileKind.Bmson, resolveResult.CanonicalCharts.Single(chart => chart.Kind == ChartFileKind.Bmson).Kind);
        Assert.AreEqual(0, index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(ChartFileKind.Bms, Path.Combine(targetDirectory, ".", "direct.bms"), directBms.Md5, directBms.Sha256)
        ]).CanonicalCharts.Count);
        Assert.AreEqual(3, index.CountChartRefsUnderRealPath(Path.Combine(targetDirectory.ToUpperInvariant(), "."), null));
        Assert.AreEqual(3, index.CountChartRefsUnderRealPath(targetDirectory, null));
        Assert.AreEqual(2, index.CountChartRefsUnderRealPath(targetDirectory, new HashSet<string>([directBms.Path], StringComparer.OrdinalIgnoreCase)));
        Assert.AreEqual(3, index.GetChartRefsUnderRealPath(targetDirectory).Count);
        Assert.AreEqual(2, index.CountBmsChartRefsUnderRealPath(targetDirectory));
        CollectionAssert.AreEquivalent(new[] { directBms.Path, nestedBms.Path }, index.GetBmsChartPathsUnderRealPath(targetDirectory));
        Assert.AreEqual(0, index.GetChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Missing")).Count);
        Assert.AreEqual(0, index.CountBmsChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_RealPathSubtreeUsesDirectoryBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string targetDirectory = Path.Combine("C:\\Installed", "Target");
        ChartFile directBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(targetDirectory, "direct.bms"));
        ChartFile nestedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(targetDirectory, "Nested", "nested.bms"));
        ChartFile siblingPrefixBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "TargetPrefix", "prefix.bms"));
        ChartFile siblingSuffixBms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "TargetSuffix", "suffix.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([directBms, nestedBms, siblingPrefixBms, siblingSuffixBms], []));

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        List<LibraryChartRef> refs = index.GetChartRefsUnderRealPath(targetDirectory);

        Assert.AreEqual(2, refs.Count);
        Assert.IsTrue(refs.Any(chart => (chart.Path == directBms.Path && chart.Md5 == directBms.Md5 && chart.Kind == ChartFileKind.Bms)));
        Assert.IsTrue(refs.Any(chart => (chart.Path == nestedBms.Path && chart.Md5 == nestedBms.Md5 && chart.Kind == ChartFileKind.Bms)));
        Assert.IsFalse(refs.Any(chart => (chart.Path == siblingPrefixBms.Path && chart.Md5 == siblingPrefixBms.Md5 && chart.Kind == ChartFileKind.Bms)));
        Assert.IsFalse(refs.Any(chart => (chart.Path == siblingSuffixBms.Path && chart.Md5 == siblingSuffixBms.Md5 && chart.Kind == ChartFileKind.Bms)));
        Assert.AreEqual(4, index.GetChartRefsUnderRealPath("C:\\").Count);
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_ResolvesFirstOwnedChartWhenStorageRowsHaveDuplicatePath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([first, second], []), out OwnedChartStorageRowFilterSummary filterSummary);

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(ChartFileKind.Bms, sharedPath, first.Md5, first.Sha256)
        ]);

        Assert.AreEqual(1, resolveResult.CanonicalCharts.Count);
        Assert.AreEqual(first.Path, resolveResult.CanonicalCharts[0].Path);
        Assert.AreEqual(first.Md5, resolveResult.CanonicalCharts[0].Md5);
        Assert.AreEqual(ChartFileKind.Bms, resolveResult.CanonicalCharts[0].Kind);
        Assert.AreEqual(1, index.GetChartRefsByPaths([sharedPath]).Count);
        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
    }

    [TestMethod]
    public void ApplyPathChanges_UpdatesCachedLibraryChartRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        bmsFile = bmsFile with { Path = newPath };
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(0, index.GetChartRefsByPaths([oldPath]).Count);
        List<LibraryChartRef> newPathRefs = index.GetChartRefsByPaths([newPath]);
        Assert.AreEqual(1, newPathRefs.Count);
        Assert.AreEqual(bmsFile.Path, newPathRefs[0].Path);
        Assert.AreEqual(bmsFile.Md5, newPathRefs[0].Md5);
        Assert.AreEqual(ChartFileKind.Bms, newPathRefs[0].Kind);
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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(bmsFile)]);
        bmsFile = bmsFile with { Path = newPath };
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = newPath };
        ChartFile movedChartSnapshot = (bmsFile);
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = movedChartSnapshot,
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(bmsFile)]);

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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = newPath };

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            },
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = newPath };

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = newPath };

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        LibraryChartRef movedRef = index.GetChartRefsByPaths([newPath]).Single();
        ChartFile currentChart = movedRef.ToChartFileIdentity();
        Assert.AreEqual(newPath, movedRef.Path);
        Assert.AreEqual(newPath, currentChart.Path);
        Assert.AreSame(bmsFile.Token, currentChart.Token);
    }

    [TestMethod]
    public void ApplyPathChanges_DoesNotAddPathlessOwnerWhenPathAppears()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = newPath };

        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
                OldPath = null,
                NewPath = newPath
            },
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
                OldPath = null,
                NewPath = newPath
            }
        ]);

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        List<LibraryChartRef> refs = index.GetChartRefsByPaths([newPath]);
        Assert.AreEqual(0, refs.Count);
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(newPath), null));
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([LibraryChartRef.FromChartFile((bmsFile))]);
        Assert.AreEqual(0, resolveResult.CanonicalCharts.Count);
    }

    [TestMethod]
    public void ApplyPathChanges_RejectsPathDisappearingOwnedMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Old", "chart.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        bmsFile = bmsFile with { Path = null };

        Assert.ThrowsException<InvalidOperationException>(() => state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (bmsFile),
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
        ChartFile movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        ChartFile existingBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", existingPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms, existingBms], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        movedBms = movedBms with { Path = existingPath };

        Assert.ThrowsException<InvalidOperationException>(() => state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (movedBms),
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
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var index = LibraryChartRefIndexSnapshot.FromLibraryChartRefs([
            LibraryChartRef.FromChartFile((first)),
            LibraryChartRef.FromChartFile((second))
        ]);

        index.RemoveCharts([(first)]);
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(ChartFileKind.Bms, sharedPath, second.Md5, second.Sha256)
        ]);

        Assert.AreEqual(1, resolveResult.CanonicalCharts.Count);
        Assert.AreEqual(second.Path, resolveResult.CanonicalCharts[0].Path);
        Assert.AreEqual(second.Md5, resolveResult.CanonicalCharts[0].Md5);
        Assert.AreEqual(ChartFileKind.Bms, resolveResult.CanonicalCharts[0].Kind);
        Assert.AreEqual(1, index.GetChartRefsByPaths([sharedPath]).Count);
    }

    [TestMethod]
    public void UpsertStorageRows_UpdatesCachedLibraryChartRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string replacedPath = Path.Combine("C:\\Installed", "Bms", "replace.bms");
        ChartFile keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        ChartFile replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedPath);
        ChartFile newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([keptBms, replacedBms], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertCharts(ChartTestValues.Combine([newBms], []));

        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        List<LibraryChartRef> refs = index.GetChartRefsByPaths([replacedPath]);
        Assert.AreEqual(1, refs.Count);
        Assert.AreEqual(newBms.Path, refs[0].Path);
        Assert.AreEqual(newBms.Md5, refs[0].Md5);
        Assert.AreEqual(ChartFileKind.Bms, refs[0].Kind);
        Assert.AreEqual(2, index.CountChartRefsUnderRealPath(Path.GetDirectoryName(replacedPath), null));
        Assert.AreEqual(2, index.CountBmsChartRefsUnderRealPath(Path.GetDirectoryName(replacedPath)));
    }

    [TestMethod]
    public void UpsertStorageRows_RejectsInvalidBmsAndBmsonRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string existingBmsPath = Path.Combine("C:\\Installed", "Bms", "existing.bms");
        string existingBmsonPath = Path.Combine("C:\\Installed", "Bmson", "existing.bmson");
        ChartFile existingBms = CreateFile("11111111111111111111111111111111", existingBmsPath);
        ChartFile existingBmson = CreateBmsonSong(existingBmsonPath, "22222222222222222222222222222222");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([existingBms], [existingBmson]));
        ChartFile pathlessBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string.Empty);
        ChartFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        ChartFile pathlessBmson = CreateBmsonSong(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        ChartFile duplicateBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "duplicate.bms"));
        ChartFile samePathBms = CreateFile("dddddddddddddddddddddddddddddddd", duplicateBms.Path);
        ChartFile caseOnlyPathBms = CreateFile("99999999999999999999999999999999", duplicateBms.Path.ToUpperInvariant());
        ChartFile crossKindBmson = CreateBmsonSong(existingBmsPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ChartFile crossKindBms = CreateFile("ffffffffffffffffffffffffffffffff", existingBmsonPath);

        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([pathlessBms], [])));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([md5lessBms], [])));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([], [pathlessBmson])));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([], [md5lessBmson])));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([duplicateBms, samePathBms], [])));
        state.UpsertCharts(ChartTestValues.Combine([caseOnlyPathBms], []));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([], [crossKindBmson])));
        Assert.ThrowsException<InvalidOperationException>(() => state.UpsertCharts(ChartTestValues.Combine([crossKindBms], [])));
    }

    [TestMethod]
    public void UpsertStorageRows_PreservesFullBuildSubtreeOrder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directoryPath = Path.Combine("C:\\Installed", "Mixed");
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine(directoryPath, "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile bmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(directoryPath, "chart.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([], [bmsonSong]));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertCharts(ChartTestValues.Combine([bmsFile], []));
        List<ChartFile> cachedSnapshot = state.CreateSnapshotForSubtreeDirectory(
            directoryPath,
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);
        List<ChartFile> rebuiltSnapshot = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]))
            .CreateSnapshotForSubtreeDirectory(
                directoryPath,
                includeWarningSnapshot: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false);

        var rebuiltPaths = rebuiltSnapshot.Select(chart => chart.Path).ToList();
        var cachedPaths = cachedSnapshot.Select(chart => chart.Path).ToList();
        Assert.AreSame(index, state.CreateLibraryChartRefIndexSnapshot());
        Assert.AreEqual(1, index.CountBmsChartRefsUnderRealPath(directoryPath));
        CollectionAssert.AreEqual(new[] { bmsFile.Path }, index.GetBmsChartPathsUnderRealPath(directoryPath));
        Assert.AreEqual(
            string.Join("|", rebuiltPaths),
            string.Join("|", cachedPaths));
    }


}
