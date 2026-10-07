using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionLookupMembershipTests
{
    [TestMethod]
    public void CreateOwnedHashIndexSnapshot_ReprojectsCurrentStorageOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        bmsonSong = bmsonSong with { Sha256 = new string('d', 64) };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        bmsFile = bmsFile with { Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" };
        bmsFile = bmsFile with { Sha256 = new string('f', 64) };
        bmsonSong = bmsonSong with { Md5 = "11111111111111111111111111111111" };
        bmsonSong = bmsonSong with { Sha256 = new string('2', 64) };
        ApplyCapturedCurrentValues(state, bmsFile, bmsonSong);

        OwnedChartHashIndexSnapshot snapshot = state.CreateOwnedHashIndexSnapshot();

        CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        CollectionAssert.DoesNotContain(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
        CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), bmsFile.Md5);
        CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), bmsonSong.Md5);
        CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), bmsFile.Sha256);
        CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), bmsonSong.Sha256);
    }

    [TestMethod]
    public void CreateLibraryChartRefsForHashes_ReturnsOnlyMatchingCurrentOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile md5Bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Md5", "chart.bms"), new string('b', 64));
        ChartFile shaBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Sha", "chart.bms"), new string('d', 64));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        bmsonSong = bmsonSong with { Sha256 = new string('f', 64) };
        ChartFile unmatchedBms = CreateFile("11111111111111111111111111111111", Path.Combine("C:\\Installed", "Other", "chart.bms"), new string('2', 64));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([md5Bms, shaBms, unmatchedBms], [bmsonSong]));
        md5Bms = md5Bms with { Md5 = "33333333333333333333333333333333" };
        shaBms = shaBms with { Sha256 = new string('4', 64) };
        bmsonSong = bmsonSong with { Md5 = "55555555555555555555555555555555" };
        ApplyCapturedCurrentValues(state, md5Bms, shaBms, bmsonSong);

        List<LibraryChartRef> refs = state.CreateLibraryChartRefsForHashes(
            new HashSet<string>([md5Bms.Md5, bmsonSong.Md5], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([shaBms.Sha256], StringComparer.OrdinalIgnoreCase));

        Assert.AreEqual(3, refs.Count);
        Assert.IsTrue(refs.Any(chart => (chart.Path == md5Bms.Path && chart.Md5 == md5Bms.Md5 && chart.Kind == ChartFileKind.Bms)));
        Assert.IsTrue(refs.Any(chart => (chart.Path == shaBms.Path && chart.Md5 == shaBms.Md5 && chart.Kind == ChartFileKind.Bms)));
        Assert.IsTrue(refs.Any(chart => (chart.Path == bmsonSong.Path && chart.Md5 == bmsonSong.Md5 && chart.Kind == ChartFileKind.Bmson)));
        Assert.IsFalse(refs.Any(chart => (chart.Path == unmatchedBms.Path && chart.Md5 == unmatchedBms.Md5 && chart.Kind == ChartFileKind.Bms)));
    }

    [TestMethod]
    public void CreateChartInfoHydrationOwnerSummary_ReprojectsCurrentStorageOwnerHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile currentChartInfoFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Info", "chart.bms"), new string('b', 64));
        ChartFile parseFailureSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Failure", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartFile backfillFile = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "Backfill", "chart.bms"), new string('e', 64));
        parseFailureSong = parseFailureSong with { Sha256 = new string('f', 64) };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([currentChartInfoFile, backfillFile], [parseFailureSong]));
        currentChartInfoFile = currentChartInfoFile with { Sha256 = new string('1', 64) };
        parseFailureSong = parseFailureSong with { Md5 = "22222222222222222222222222222222" };
        ApplyCapturedCurrentValues(state, currentChartInfoFile, parseFailureSong);

        ChartInfoHydrationOwnerSummary summary = state.CreateChartInfoHydrationOwnerSummary(
            new HashSet<string>([currentChartInfoFile.Sha256], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([parseFailureSong.Md5], StringComparer.OrdinalIgnoreCase));

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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        ChartFile pathlessBms = CreateFile("dddddddddddddddddddddddddddddddd", string.Empty);
        ChartFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile pathlessBmson = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");
        ChartFile md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, pathlessBms, md5lessBms], [bmsonSong, pathlessBmson, md5lessBmson]));

        List<ChartFile> snapshot = state.CreateFullResourceMaintenanceTargetSnapshot();

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, bmsFile.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, bmsonSong.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, pathlessBms.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, pathlessBmson.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, md5lessBms.Token)));
        Assert.IsFalse(snapshot.Any(chart => ReferenceEquals(chart.Token, md5lessBmson.Token)));
    }

    [TestMethod]
    public void CreatePathSnapshot_ReprojectsCurrentStorageOwnerPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "OldBms", "chart.bms"));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "OldBmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        string newBmsPath = Path.Combine("C:\\Installed", "NewBms", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "NewBmson", "chart.bmson");
        bmsFile = bmsFile with { Path = newBmsPath };
        bmsonSong = bmsonSong with { Path = newBmsonPath };
        ApplyCapturedCurrentValues(state, bmsFile, bmsonSong);

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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath, new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(oldBmsonPath, "cccccccccccccccccccccccccccccccc");
        bmsonSong = bmsonSong with { Sha256 = new string('d', 64) };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        string newBmsPath = Path.Combine("C:\\Installed", "NewBms", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "NewBmson", "chart.bmson");
        bmsFile = bmsFile with { Path = newBmsPath };
        bmsFile = bmsFile with { Md5 = "11111111111111111111111111111111" };
        bmsFile = bmsFile with { Sha256 = new string('2', 64) };
        bmsonSong = bmsonSong with { Path = newBmsonPath };
        bmsonSong = bmsonSong with { Md5 = "33333333333333333333333333333333" };
        bmsonSong = bmsonSong with { Sha256 = new string('4', 64) };
        ApplyCapturedCurrentValues(state, bmsFile, bmsonSong);

        HashSet<string> keys = state.CreateInstallDestinationRuntimeStateKeySnapshot();

        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, newBmsPath, bmsFile.Md5, bmsFile.Sha256)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, newBmsPath)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, newBmsonPath, bmsonSong.Md5, bmsonSong.Sha256)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, newBmsonPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, newBmsPath.ToLowerInvariant(), bmsFile.Md5, bmsFile.Sha256)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, newBmsPath.ToLowerInvariant())));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, newBmsonPath.ToLowerInvariant(), bmsonSong.Md5, bmsonSong.Sha256)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, newBmsonPath.ToLowerInvariant())));
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
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath, new string('b', 64));
        ChartFile bmsonSong = CreateBmsonSong(oldBmsonPath, "cccccccccccccccccccccccccccccccc");
        bmsonSong = bmsonSong with { Sha256 = new string('d', 64) };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        string newBmsPath = Path.Combine("C:\\Installed", "NewBms", "chart.bms");
        string newBmsonPath = Path.Combine("C:\\Installed", "NewBmson", "chart.bmson");
        bmsFile = bmsFile with { Path = newBmsPath };
        bmsFile = bmsFile with { Md5 = "11111111111111111111111111111111" };
        bmsFile = bmsFile with { Sha256 = new string('2', 64) };
        bmsonSong = bmsonSong with { Path = newBmsonPath };
        bmsonSong = bmsonSong with { Md5 = "33333333333333333333333333333333" };
        bmsonSong = bmsonSong with { Sha256 = new string('4', 64) };
        ApplyCapturedCurrentValues(state, bmsFile, bmsonSong);

        HashSet<string> keys = state.CreateChartRuntimeStatePrimaryKeySnapshot();

        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, newBmsPath, bmsFile.Md5, bmsFile.Sha256)));
        Assert.IsTrue(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, newBmsonPath, bmsonSong.Md5, bmsonSong.Sha256)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, newBmsPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bmson, newBmsonPath)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, oldBmsPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64))));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, oldBmsonPath, "cccccccccccccccccccccccccccccccc", new string('d', 64))));
    }

    [TestMethod]
    public void RemoveChartRequests_UpdateOwnedCollectionMembership()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
        ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            Md5 = "cccccccccccccccccccccccccccccccc"
        };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([first, second], [bmsonSong]));

        Assert.AreEqual(2, state.RemoveChartRequests([
            OwnedChartRemoveRequest.FromChart(first),
            OwnedChartRemoveRequest.FromChart(bmsonSong)
        ]));
        List<ChartFile> afterRemove = state.CreateSnapshot();

        Assert.AreEqual(1, afterRemove.Count);
        Assert.AreSame(second.Token, afterRemove[0].Token);
    }

    [TestMethod]
    public void RemoveChartRequests_StaleOwnerReferenceDoesNotRemoveSamePathChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = bmsonPath,
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        ChartFile staleSamePathBmsOwner = CreateFile("cccccccccccccccccccccccccccccccc", bmsonPath);

        Assert.AreEqual(0, state.RemoveChartRequests([
            OwnedChartRemoveRequest.FromChart(staleSamePathBmsOwner)
        ]));
        List<ChartFile> snapshot = state.CreateSnapshot();

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, bmsFile.Token)));
        Assert.IsTrue(snapshot.Any(chart => ReferenceEquals(chart.Token, bmsonSong.Token)));
    }

    [TestMethod]
    public void RemoveChartRequests_PathCleanupRemovesUniqueOwnedPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        ChartFile other = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, other], []));

        Assert.AreEqual(1, state.RemoveChartRequests([
            OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, bmsPath)
        ]));
        List<ChartFile> snapshot = state.CreateSnapshot();

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(other.Token, snapshot[0].Token);
    }

    [TestMethod]
    public void RemoveChartRequests_OwnerReferenceRemovesOnlyRequestedOwnerWhenDuplicatePathWasSkipped()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        ChartFile samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        ChartFile other = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([first, samePath, other], []), out OwnedChartStorageRowFilterSummary filterSummary);

        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
        Assert.AreEqual(1, state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(first)]));
        List<ChartFile> snapshot = state.CreateSnapshot();

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(other.Token, snapshot[0].Token);
    }

    [TestMethod]
    public void ContainsKnownChart_UsesOwnedReferenceAndKindPathExactLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = bmsonPath,
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));
        var bmsonPathOnlyChart = new ChartFile(ChartFileKind.Bmson, bmsonPath, bmsonSong.Md5, null, "title", "raw", "artist", string.Empty, Path.GetDirectoryName(bmsonPath), string.Empty, string.Empty, null, 0, null);

        Assert.IsTrue(state.ContainsKnownChart((bmsFile)));
        Assert.IsTrue(state.ContainsKnownChart(bmsonPathOnlyChart));
        Assert.IsFalse(state.ContainsKnownChart(new ChartFile(ChartFileKind.Bmson, bmsPath, "cccccccccccccccccccccccccccccccc", null, "title", "raw", "artist", string.Empty, Path.GetDirectoryName(bmsPath), string.Empty, string.Empty, null, 0, null)));
    }

    [TestMethod]
    public void ContainsKnownChart_PathExactLookupUsesOwnedUniquePath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        ChartFile samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([first, samePath], []), out OwnedChartStorageRowFilterSummary filterSummary);
        var pathOnlyChart = new ChartFile(ChartFileKind.Bms, sharedPath, "cccccccccccccccccccccccccccccccc", null, "title", "raw", "artist", string.Empty, Path.GetDirectoryName(sharedPath), string.Empty, string.Empty, null, 0, null);

        Assert.AreEqual(1, filterSummary.DuplicatePathBmsCount);
        Assert.IsTrue(state.ContainsKnownChart(pathOnlyChart));
        Assert.AreEqual(1, state.CreateSnapshot().Count);
    }

    [TestMethod]
    public void ContainsKnownChart_DoesNotMatchPathlessOwnerBackedChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));

        Assert.IsFalse(state.ContainsKnownChart((bmsFile)));
    }

    [TestMethod]
    public void ContainsKnownChart_DoesNotMatchOwnerWhoseCurrentPathDisappeared()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string oldDirectory = Path.GetDirectoryName(oldPath)!;
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(bmsFile)]);

        Assert.IsFalse(state.ContainsKnownChart((bmsFile)));
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
        ChartFile keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        ChartFile replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedBmsPath);
        ChartFile newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedBmsPath);
        ChartFile addedBms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "Bms", "added.bms"));
        ChartFile keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "aaa.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ChartFile replacedBmson = CreateBmsonSong(replacedBmsonPath, "ffffffffffffffffffffffffffffffff");
        ChartFile newBmson = CreateBmsonSong(replacedBmsonPath, "11111111111111111111111111111111");
        ChartFile addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "zzz.bmson"), "22222222222222222222222222222222");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([keptBms, replacedBms], [replacedBmson, keptBmson]));

        state.UpsertCharts(ChartTestValues.Combine([newBms, addedBms], [newBmson, addedBmson]));
        List<ChartFile> snapshot = state.CreateSnapshot();

        Assert.AreEqual(6, snapshot.Count);
        Assert.AreSame(keptBms.Token, snapshot[0].Token);
        Assert.AreEqual(newBms.Md5, snapshot[1].Md5);
        Assert.AreNotSame(newBms.Token, snapshot[1].Token);
        Assert.IsNotNull(snapshot[1].Token);
        Assert.AreEqual(addedBms.Md5, snapshot[2].Md5);
        Assert.AreNotSame(addedBms.Token, snapshot[2].Token);
        Assert.IsNotNull(snapshot[2].Token);
        Assert.AreSame(keptBmson.Token, snapshot[3].Token);
        Assert.AreEqual(newBmson.Md5, snapshot[4].Md5);
        Assert.AreNotSame(newBmson.Token, snapshot[4].Token);
        Assert.IsNotNull(snapshot[4].Token);
        Assert.AreEqual(addedBmson.Md5, snapshot[5].Md5);
        Assert.AreNotSame(addedBmson.Token, snapshot[5].Token);
        Assert.IsNotNull(snapshot[5].Token);
    }

    [TestMethod]
    public void UpsertStorageRows_BmsSurvivorsKeepRelativeOrderAndReplacementAppends()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "B0.bms"));
        ChartFile replaced = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "B1.bms"));
        ChartFile third = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "B2.bms"));
        ChartFile replacement = CreateFile("dddddddddddddddddddddddddddddddd", replaced.Path);
        ChartFile added = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Path.Combine("C:\\Installed", "Bms", "B3.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([first, replaced, third], []));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertCharts(ChartTestValues.Combine([replacement, added], []));

        List<ChartFile> snapshot = state.CreateSnapshot();
        CollectionAssert.AreEqual(
            new[] { first.Md5, third.Md5, replacement.Md5, added.Md5 },
            snapshot.Select(chart => chart.Md5).ToArray());
        CollectionAssert.AreEqual(
            new[] { first.Md5, third.Md5, replacement.Md5, added.Md5 },
            index.GetChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Bms"))
                .Select(chart => chart.Md5)
                .ToArray());
    }

    [TestMethod]
    public void UpsertStorageRows_CanonicalBmsonUsesCapturedPathAndReplacementAppends()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directory = Path.Combine("C:\\Installed", "Bmson");
        ChartFile z = CreateBmsonSong(Path.Combine(directory, "z.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile upper = CreateBmsonSong(Path.Combine(directory, "A.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile lower = CreateBmsonSong(Path.Combine(directory, "a.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartFile bms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine(directory, "added.bms"));
        ChartFile replacement = CreateBmsonSong(upper.Path, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([], [z, upper, lower]));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertCharts(ChartTestValues.Combine([bms], []));
        CollectionAssert.AreEqual(
            new[] { bms.Md5, upper.Md5, lower.Md5, z.Md5 },
            state.CreateSnapshot()
                .Select(chart => chart.Md5)
                .ToArray());

        state.UpsertCharts(ChartTestValues.Combine([], [replacement]));
        CollectionAssert.AreEqual(
            new[] { bms.Md5, lower.Md5, replacement.Md5, z.Md5 },
            state.CreateSnapshot()
                .Select(chart => chart.Md5)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { bms.Md5, lower.Md5, replacement.Md5, z.Md5 },
            index.GetChartRefsUnderRealPath(directory)
                .Select(chart => chart.Md5)
                .ToArray());
    }

    [TestMethod]
    public void ApplyPathChanges_CanonicalBmsonKeepsCapturedSequencePosition()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directory = Path.Combine("C:\\Installed", "Bmson");
        ChartFile z = CreateBmsonSong(Path.Combine(directory, "z.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile upper = CreateBmsonSong(Path.Combine(directory, "A.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile lower = CreateBmsonSong(Path.Combine(directory, "a.bmson"), "cccccccccccccccccccccccccccccccc");
        ChartFile bms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine(directory, "added.bms"));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([], [z, upper, lower]));
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        state.UpsertCharts(ChartTestValues.Combine([bms], []));

        string oldPath = upper.Path!;
        string movedPath = Path.Combine(directory, "y.bmson");
        upper = upper with { Path = movedPath };
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = (upper),
                OldPath = oldPath,
                NewPath = movedPath
            }
        ]);

        CollectionAssert.AreEqual(
            new[] { bms.Md5, upper.Md5, lower.Md5, z.Md5 },
            state.CreateSnapshot()
                .Select(chart => chart.Md5)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { bms.Md5, upper.Md5, lower.Md5, z.Md5 },
            index.GetChartRefsUnderRealPath(directory)
                .Select(chart => chart.Md5)
                .ToArray());
    }

    [TestMethod]
    public void CanonicalSequence_Background16And128KeepsWarmMutationWorkBounded()
    {
        CanonicalWorkScenario small = RunCanonicalWorkScenario(16);
        CanonicalWorkScenario large = RunCanonicalWorkScenario(128);

        Assert.IsTrue(small.ColdEnumerationCount > 0);
        Assert.IsTrue(small.ColdVisitedEntryCount > 0);
        Assert.IsTrue(small.ColdMaterializationCount > 0);
        Assert.IsTrue(small.ColdAccessCount > 0);
        Assert.IsTrue(large.ColdEnumerationCount > 0);
        Assert.IsTrue(large.ColdVisitedEntryCount > 0);
        Assert.IsTrue(large.ColdMaterializationCount > 0);
        Assert.IsTrue(large.ColdAccessCount > 0);

        Assert.AreEqual(0, small.WarmEnumerationCount);
        Assert.AreEqual(0, small.WarmVisitedEntryCount);
        Assert.AreEqual(0, small.WarmMaterializationCount);
        Assert.AreEqual(0, large.WarmEnumerationCount);
        Assert.AreEqual(0, large.WarmVisitedEntryCount);
        Assert.AreEqual(0, large.WarmMaterializationCount);
        Assert.AreEqual(small.WarmEnumerationCount, large.WarmEnumerationCount);
        Assert.AreEqual(small.WarmVisitedEntryCount, large.WarmVisitedEntryCount);
        Assert.AreEqual(small.WarmMaterializationCount, large.WarmMaterializationCount);
        Assert.IsTrue(small.WarmAccessCount > 0);
        Assert.IsTrue(large.WarmAccessCount > 0);
        Assert.IsTrue(small.WarmAccessCount <= CalculateCanonicalWarmAccessUpperBound(16));
        Assert.IsTrue(large.WarmAccessCount <= CalculateCanonicalWarmAccessUpperBound(128));
    }

    private static CanonicalWorkScenario RunCanonicalWorkScenario(int backgroundCount)
    {
        const int bmsonCount = 3;
        var workObserver = new RecordingCanonicalSequenceWorkObserver();
        var bmsFiles = new List<ChartFile>(backgroundCount);
        for (int i = 0; i < backgroundCount; i++)
        {
            bmsFiles.Add(CreateFile(
                i.ToString("x32"),
                Path.Combine("C:\\CanonicalWork", $"background-{i}.bms")));
        }
        var bmsonSongs = new List<ChartFile>(bmsonCount);
        for (int i = 0; i < bmsonCount; i++)
        {
            bmsonSongs.Add(CreateBmsonSong(
                Path.Combine("C:\\CanonicalWork", $"background-{i}.bmson"),
                (i + 1).ToString("x32")));
        }
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine(bmsFiles, bmsonSongs), CancellationToken.None, workObserver);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        List<ChartFile> oldSnapshot = state.CreateSnapshot();
        workObserver.Reset();
        state.UpsertCharts(ChartTestValues.Combine([
            CreateFile(
                new string('e', 32),
                Path.Combine("C:\\CanonicalWork", $"cold-{backgroundCount}.bms"))
        ], []));
        CanonicalWorkCounts cold = workObserver.Capture();
        Assert.AreEqual(bmsonCount, cold.VisitedEntryCount);
        Assert.AreEqual(bmsonCount, cold.MaterializationCount);
        workObserver.Reset();

        ChartFile firstDelta = CreateFile(
            new string('d', 32),
            Path.Combine("C:\\CanonicalWork", $"delta-{backgroundCount}.bms"));
        ChartFile replacement = CreateFile(firstDelta.Md5, firstDelta.Path);
        replacement = replacement with { Md5 = new string('c', 32) };
        state.UpsertCharts(ChartTestValues.Combine([firstDelta], []));
        Assert.AreEqual(firstDelta.Path, index.GetChartRefsByPaths([firstDelta.Path]).Single().Path);
        Assert.AreEqual(firstDelta.Md5, index.GetChartRefsByPaths([firstDelta.Path]).Single().Md5);
        Assert.AreEqual(ChartFileKind.Bms, index.GetChartRefsByPaths([firstDelta.Path]).Single().Kind);
        state.UpsertCharts(ChartTestValues.Combine([replacement], []));
        Assert.AreEqual(replacement.Path, index.GetChartRefsByPaths([replacement.Path]).Single().Path);
        Assert.AreEqual(replacement.Md5, index.GetChartRefsByPaths([replacement.Path]).Single().Md5);
        Assert.AreEqual(ChartFileKind.Bms, index.GetChartRefsByPaths([replacement.Path]).Single().Kind);
        CanonicalWorkCounts warm = workObserver.Capture();

        Assert.AreEqual(backgroundCount + bmsonCount, oldSnapshot.Count);
        Assert.IsTrue(oldSnapshot.Any(chart => chart.Token != null));
        Assert.AreEqual(backgroundCount + bmsonCount + 2, state.CreateSnapshot().Count);
        return new CanonicalWorkScenario(
            cold.EnumerationCount,
            cold.VisitedEntryCount,
            cold.MaterializationCount,
            cold.AccessCount,
            warm.EnumerationCount,
            warm.VisitedEntryCount,
            warm.MaterializationCount,
            warm.AccessCount);
    }

    private static int CalculateCanonicalWarmAccessUpperBound(int backgroundCount)
    {
        int target = backgroundCount + 3;
        int powerOfTwo = 1;
        int ceilingLog2 = 0;
        while (powerOfTwo < target)
        {
            powerOfTwo <<= 1;
            ceilingLog2++;
        }
        return 8 + (4 * (ceilingLog2 + 1));
    }

    private sealed class RecordingCanonicalSequenceWorkObserver : ICatalogStorageSequenceWorkObserver
    {
        internal int AccessCount { get; private set; }

        internal int EnumerationCount { get; private set; }

        internal int VisitedEntryCount { get; private set; }

        internal int MaterializationCount { get; private set; }

        public void ObserveAccess() => AccessCount++;

        public void ObserveEnumeration() => EnumerationCount++;

        public void ObserveEntryVisit() => VisitedEntryCount++;

        public void ObserveMaterialization(int count) => MaterializationCount += count;

        internal CanonicalWorkCounts Capture()
        {
            return new CanonicalWorkCounts(
                EnumerationCount,
                VisitedEntryCount,
                MaterializationCount,
                AccessCount);
        }

        internal void Reset()
        {
            AccessCount = 0;
            EnumerationCount = 0;
            VisitedEntryCount = 0;
            MaterializationCount = 0;
        }
    }

    private readonly record struct CanonicalWorkCounts(
        int EnumerationCount,
        int VisitedEntryCount,
        int MaterializationCount,
        int AccessCount);

    private readonly record struct CanonicalWorkScenario(
        int ColdEnumerationCount,
        int ColdVisitedEntryCount,
        int ColdMaterializationCount,
        int ColdAccessCount,
        int WarmEnumerationCount,
        int WarmVisitedEntryCount,
        int WarmMaterializationCount,
        int WarmAccessCount);

    [TestMethod]
    public void DuplicateChartRowSnapshot_UsesCachedIndexWithoutCopyingRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [bmsonSong]));

        OwnedDuplicateChartRowSnapshot first = state.CreateDuplicateChartRowSnapshot();
        OwnedDuplicateChartRowSnapshot second = state.CreateDuplicateChartRowSnapshot();
        DuplicateChartRow bmsRow = first.Rows.Single(row => row.ChartKind == ChartFileKind.Bms);
        ChartFile materialized = bmsRow.CreateChart();

        Assert.IsTrue(state.IsDuplicateChartRowSnapshotInitialized);
        Assert.AreSame(first, second);
        Assert.AreEqual(2, first.Rows.Count);
        Assert.AreEqual(1, first.DuplicateHashCount);
        Assert.AreEqual(2, first.DuplicateHashRowCount);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", first.DuplicateHashBuckets.Single().LookupHash);
        Assert.AreEqual(2, first.DuplicateHashBuckets.Single().Rows.Count);
        CollectionAssert.AreEquivalent(new[] { bmsFile }, first.BmsCharts.ToArray());
        Assert.IsNotNull(materialized);
        Assert.AreSame(bmsFile.Token, bmsRow.Chart.Token);
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_TracksRemovePathChangeAndUpsert()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "move.bms");
        string newPath = Path.Combine("C:\\Installed", "Moved", "move.bms");
        ChartFile movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        ChartFile removedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "removed.bms"));
        ChartFile addedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "added.bms"));
        ChartFile addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms, removedBms], []));
        OwnedDuplicateChartRowSnapshot originalSnapshot = state.CreateDuplicateChartRowSnapshot();

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(removedBms)]);
        movedBms = movedBms with { Path = newPath };
        state.ApplyPathChanges(
        [
            new LibraryChartPathChange
            {
                Chart = (movedBms),
                OldPath = oldPath,
                NewPath = newPath
            }
        ]);
        state.UpsertCharts(ChartTestValues.Combine([addedBms], [addedBmson]));

        OwnedDuplicateChartRowSnapshot snapshot = state.CreateDuplicateChartRowSnapshot();
        Assert.AreEqual(2, originalSnapshot.Rows.Count);
        Assert.AreNotSame(originalSnapshot, snapshot);
        CollectionAssert.AreEqual(new[] { movedBms.Md5, addedBms.Md5 }, snapshot.BmsCharts.Select(chart => chart.Md5).ToArray());
        Assert.AreEqual(oldPath, originalSnapshot.Rows.Single(row => row.Chart.Token == movedBms.Token).Path);
        Assert.IsFalse(snapshot.Rows.Any(row => ReferenceEquals(row.Chart.Token, removedBms.Token)));
        Assert.AreEqual(newPath, snapshot.Rows.Single(row => ReferenceEquals(row.Chart.Token, movedBms.Token)).Path);
        Assert.IsTrue(snapshot.Rows.Any(row => row.Chart.Md5 == addedBms.Md5 && row.Chart.Token != addedBms.Token));
        Assert.IsTrue(snapshot.Rows.Any(row => row.Chart.Md5 == addedBmson.Md5 && row.Chart.Token != addedBmson.Token));
        Assert.AreEqual(0, snapshot.DuplicateHashCount);
        Assert.AreEqual(0, snapshot.DuplicateHashRowCount);
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_BmsonOnlyRemoveReusesBmsStorageRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        ChartFile removedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "removed.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "kept.bmson"), "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], [removedBmson, keptBmson]));
        OwnedDuplicateChartRowSnapshot originalSnapshot = state.CreateDuplicateChartRowSnapshot();

        state.RemoveChartRequests([OwnedChartRemoveRequest.FromChart(removedBmson)]);

        OwnedDuplicateChartRowSnapshot snapshot = state.CreateDuplicateChartRowSnapshot();
        Assert.AreNotSame(originalSnapshot, snapshot);
        Assert.AreSame(originalSnapshot.BmsCharts, snapshot.BmsCharts);
        Assert.IsFalse(snapshot.Rows.Any(row => ReferenceEquals(row.Chart.Token, removedBmson.Token)));
        Assert.IsTrue(snapshot.Rows.Any(row => ReferenceEquals(row.Chart.Token, keptBmson.Token)));
        Assert.AreEqual(0, snapshot.DuplicateHashCount);
        CollectionAssert.AreEqual(new[] { bmsFile }, snapshot.BmsCharts.ToArray());
    }

    [TestMethod]
    public void DuplicateChartRowSnapshot_TracksMd5DigestChangesOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string path = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", path, new string('1', 64));
        var state = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile], []));
        OwnedDuplicateChartRowSnapshot snapshot = state.CreateDuplicateChartRowSnapshot();
        DuplicateChartRow originalRow = snapshot.Rows.Single();

        state.ApplyDigestChanges(
        [
            new LibraryChartDigestChange(
                ChartFileKind.Bms,
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
                ChartFileKind.Bms,
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


}
