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
public sealed class OwnedChartCollectionLookupMembershipTests
{
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


}
