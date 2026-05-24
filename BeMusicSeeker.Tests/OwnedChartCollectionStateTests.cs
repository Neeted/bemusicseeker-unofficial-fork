using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
    public void CreateSnapshot_MatchesStorageRowsProjection()
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
    public void MatchesStorageRows_DetectsSameCountReplacementAndReorder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
        var replacement = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], []);

        Assert.IsTrue(state.MatchesStorageRows([first, second], []));
        Assert.IsFalse(state.MatchesStorageRows([first, replacement], []));
        Assert.IsFalse(state.MatchesStorageRows([second, first], []));
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
        List<LibraryChartRef> allRefs = state.CreateLibraryChartRefIndexSnapshot().CreateAllChartRefsSnapshot();
        Assert.AreEqual(2, refs.Count);
        Assert.AreEqual(2, allRefs.Count);
        LibraryChartRef bmsRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bms);
        LibraryChartRef bmsonRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bmson);
        Assert.IsTrue(allRefs.Any(chart => chart.Kind == LibraryChartKind.Bms && string.Equals(chart.Path, newBmsPath, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(allRefs.Any(chart => chart.Kind == LibraryChartKind.Bmson && string.Equals(chart.Path, newBmsonPath, StringComparison.OrdinalIgnoreCase)));
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

        List<LibraryChartRef> refs = index.CreateAllChartRefsSnapshot();
        LibraryChartRef bmsRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bms);
        LibraryChartRef bmsonRef = refs.Single(chart => chart.Kind == LibraryChartKind.Bmson);
        Assert.AreEqual(bmsFile.hash, bmsRef.Md5);
        Assert.AreEqual(bmsFile.sha256, bmsRef.Sha256);
        Assert.AreEqual(bmsonSong.md5, bmsonRef.Md5);
        Assert.AreEqual(bmsonSong.sha256, bmsonRef.Sha256);
    }

    [TestMethod]
    public void CreateLibraryChartRefIndexSnapshot_AllRefsIncludesPathlessRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, new string('b', 64));
        var bmsonSong = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");

        LibraryChartRefIndexSnapshot index = LibraryChartRefIndexSnapshot.FromLibraryChartRefs([
            LibraryChartRef.FromBmsFile(bmsFile),
            LibraryChartRef.FromBmsonSong(bmsonSong)
        ]);

        List<LibraryChartRef> refs = index.CreateAllChartRefsSnapshot();

        Assert.AreEqual(2, refs.Count);
        Assert.IsTrue(refs.Any(chart => chart.Kind == LibraryChartKind.Bms && chart.GetBmsStorageOwner() == bmsFile));
        Assert.IsTrue(refs.Any(chart => chart.Kind == LibraryChartKind.Bmson && chart.GetBmsonStorageOwner() == bmsonSong));
        Assert.AreEqual(0, index.CountChartRefsUnderRealPath("C:\\Installed", null));
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
    public void CreateSnapshotForMd5Hashes_IncludesMatchingPathlessBmsonOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);

        List<ChartFile> snapshot = state.CreateSnapshotForMd5Hashes(
            new HashSet<string>(["cccccccccccccccccccccccccccccccc"], StringComparer.OrdinalIgnoreCase),
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(bmsonSong, snapshot[0].GetBmsonStorageOwner());
        Assert.IsNull(snapshot[0].Path);
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
        Assert.AreEqual(0, index.GetChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Missing")).Count);
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
    public void CreateLibraryChartRefIndexSnapshot_DoesNotResolveAmbiguousSameKindPathOnlyInput()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], []);

        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        CanonicalChartResolveResult resolveResult = index.ResolveCanonicalCharts([
            LibraryChartRef.FromPath(LibraryChartKind.Bms, sharedPath, first.hash, first.sha256)
        ]);

        Assert.AreEqual(0, resolveResult.CanonicalCharts.Count);
        Assert.AreEqual(1, resolveResult.UnresolvedCharts.Count);
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
        var pathlessBmson = CreateBmsonSong(null, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        bmsonSong.sha256 = new string('d', 64);
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong, pathlessBmson]);
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
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, null, pathlessBmson.md5, pathlessBmson.sha256)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, oldBmsPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64))));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, oldBmsPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, oldBmsonPath, "cccccccccccccccccccccccccccccccc", new string('d', 64))));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, oldBmsonPath)));
    }

    [TestMethod]
    public void RemoveCharts_UpdateOwnedCollectionMembership()
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

        Assert.AreEqual(2, state.RemoveCharts([
            ChartFileProjection.FromBmsStorageOwnerIdentity(first),
            ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonSong)
        ]));
        List<ChartFile> afterRemove = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, afterRemove.Count);
        Assert.AreSame(second, afterRemove[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void RemoveCharts_PathFallbackDoesNotCrossChartKind()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = sharedPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        var bmsPathOnlyChart = new ChartFile(
            ChartFileKind.Bms,
            sharedPath,
            bmsFile.hash,
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

        Assert.AreEqual(1, state.RemoveCharts([bmsPathOnlyChart]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(bmsonSong, snapshot[0].GetBmsonStorageOwner());
    }

    [TestMethod]
    public void RemoveCharts_PathFallbackMatchesSameKindDuplicateRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var other = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, samePath, other], []);

        Assert.AreEqual(2, state.RemoveCharts([ChartFileProjection.FromBmsStorageOwnerIdentity(first)]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(other, snapshot[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void UpsertStorageRows_ReplacesSamePathRowsAndPreservesStorageOrder()
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
    public void ApplyLibraryMutationDelta_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [first, second],
                BmsonSongs = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateCurrentInstalledChartSnapshot(library, includeResourceReferences: false);
            Assert.AreEqual(2, initialSnapshot.Count);
            Assert.IsTrue(IsOwnedChartCollectionInitialized(library));
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true
            };
            delta.ChartsToUnregister.Add(initialSnapshot[0]);

            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.IsTrue(IsOwnedChartCollectionInitialized(library));
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(second, library.BMSFiles[0]);
            List<ChartFile> afterSnapshot = InvokeCreateCurrentInstalledChartSnapshot(library, includeResourceReferences: false);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsStorageOwner());
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
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [keptBms, replacedBms],
                BmsonSongs = [replacedBmson, keptBmson]
            };
            InvokeCreateCurrentInstalledChartSnapshot(library, includeResourceReferences: false);
            object ownedStateBefore = GetOwnedChartCollectionState(library);

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newBms], [newBmson]));
            List<ChartFile> snapshot = InvokeCreateCurrentInstalledChartSnapshot(library, includeResourceReferences: false);

            Assert.IsTrue(IsOwnedChartCollectionInitialized(library));
            Assert.AreSame(ownedStateBefore, GetOwnedChartCollectionState(library));
            Assert.AreEqual(4, snapshot.Count);
            Assert.AreSame(keptBms, snapshot[0].GetBmsStorageOwner());
            Assert.AreSame(newBms, snapshot[1].GetBmsStorageOwner());
            Assert.AreSame(keptBmson, snapshot[2].GetBmsonStorageOwner());
            Assert.AreSame(newBmson, snapshot[3].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_InvalidatesOwnedCollectionOnFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            string duplicateBmsonPath = Path.Combine("C:\\Installed", "Bmson", "duplicate.bmson");
            var duplicateBmsonA = CreateBmsonSong(duplicateBmsonPath, "cccccccccccccccccccccccccccccccc");
            var duplicateBmsonB = CreateBmsonSong(duplicateBmsonPath, "dddddddddddddddddddddddddddddddd");
            var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [keptBms],
                BmsonSongs = [duplicateBmsonA, duplicateBmsonB]
            };
            InvokeCreateCurrentInstalledChartSnapshot(library, includeResourceReferences: false);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(() =>
                InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms], [addedBmson])));

            Assert.IsInstanceOfType(exception.InnerException, typeof(ArgumentException));
            Assert.IsFalse(IsOwnedChartCollectionInitialized(library));
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

            List<LibraryChartRef> refs = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreEqual(1, refs.Count);
            Assert.AreSame(bmsFile, refs[0].GetBmsStorageOwner());
            Assert.AreEqual(Path.Combine("C:\\Install", "Bms"), refs[0].ToChartFile().InstallDestination);
        });
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

    private static List<ChartFile> InvokeCreateCurrentInstalledChartSnapshot(BMSLibrary library, bool includeResourceReferences)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateCurrentInstalledChartSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (List<ChartFile>)methodInfo.Invoke(library, [includeResourceReferences]);
    }

    private static List<LibraryChartRef> InvokeCreateInstallDestinationOverlayChartRefSnapshot(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateInstallDestinationOverlayChartRefSnapshotUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (List<LibraryChartRef>)methodInfo.Invoke(library, []);
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
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyLibraryMutationDelta", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [delta]);
    }

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet addedTargets)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyInstalledChartStorageTargets", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [addedTargets, "test"]);
    }

    private static bool IsOwnedChartCollectionInitialized(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("ownedChartCollectionInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return (bool)fieldInfo.GetValue(library);
    }

    private static object GetOwnedChartCollectionState(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("ownedChartCollection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return fieldInfo.GetValue(library);
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

    private static TestableBmsFile CreateFile(string hash, string? path, string? sha256 = null)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        file.SetSha256(sha256);
        return file;
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSong(string? path, string md5)
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = md5
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string? value)
        {
            sha256 = value;
        }
    }
}
