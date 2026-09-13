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
public sealed class OwnedChartCollectionReferenceIndexTests
{
    [TestMethod]
    public void CanonicalExactPathQueryReturnsCurrentOwnerAndPreservesOrderKeyAcrossRelocation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "old.bms");
        string newPath = Path.Combine("C:\\Installed", "Bms", "new.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var sibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "sibling.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile, sibling], []);

        Assert.IsTrue(state.TryGetCanonicalChartRefForExactPath(
            LibraryChartKind.Bms,
            oldPath,
            out LibraryChartRef oldRef,
            out OwnedChartCanonicalOrderKey oldOrder));
        Assert.AreSame(bmsFile, oldRef.GetBmsStorageOwner());

        bmsFile.path = newPath;
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);

        Assert.IsFalse(state.TryGetCanonicalChartRefForExactPath(
            LibraryChartKind.Bms,
            oldPath,
            out _,
            out _));
        Assert.IsTrue(state.TryGetCanonicalChartRefForExactPath(
            LibraryChartKind.Bms,
            newPath,
            out LibraryChartRef newRef,
            out OwnedChartCanonicalOrderKey newOrder));
        Assert.AreSame(bmsFile, newRef.GetBmsStorageOwner());
        Assert.AreEqual(oldOrder, newOrder);
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

    /// <summary>指定したexact keyだけを投影し、同じFSへ解決する別表記を対象へ加えません。</summary>
    [TestMethod]
    public void CreateSnapshotForPaths_ProjectsOnlyRequestedPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var firstBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Target", "first.bms"), new string('b', 64));
        var secondBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "second.bms"), new string('d', 64));
        var targetBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Target", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([firstBms, secondBms], [targetBmson]);

        List<ChartFile> snapshot = state.CreateSnapshotForPaths(
            [firstBms.path, targetBmson.path, targetBmson.path],
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), firstBms)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), targetBmson)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), secondBms)));

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

    /// <summary>行のexact lookupと、directory探索の既存正規化を区別します。</summary>
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
            LibraryChartRef.FromPath(LibraryChartKind.Bms, directBms.path, directBms.hash, directBms.sha256),
            LibraryChartRef.FromPath(LibraryChartKind.Bmson, bmsonSong.path, bmsonSong.md5, bmsonSong.sha256)
        ]);

        Assert.AreEqual(2, resolveResult.CanonicalCharts.Count);
        Assert.AreSame(directBms, resolveResult.CanonicalCharts.Single(chart => chart.Kind == LibraryChartKind.Bms).GetBmsStorageOwner());
        Assert.AreSame(bmsonSong, resolveResult.CanonicalCharts.Single(chart => chart.Kind == LibraryChartKind.Bmson).GetBmsonStorageOwner());
        Assert.AreEqual(0, index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(LibraryChartKind.Bms, Path.Combine(targetDirectory, ".", "direct.bms"), directBms.hash, directBms.sha256)
        ]).CanonicalCharts.Count);
        Assert.AreEqual(3, index.CountChartRefsUnderRealPath(Path.Combine(targetDirectory.ToUpperInvariant(), "."), null));
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


}
