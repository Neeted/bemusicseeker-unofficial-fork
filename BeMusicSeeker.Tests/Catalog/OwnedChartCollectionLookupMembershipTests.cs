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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        bmsonSong.sha256 = new string('d', 64);
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
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
        TestableBmsFile md5Bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Md5", "chart.bms"), new string('b', 64));
        TestableBmsFile shaBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Sha", "chart.bms"), new string('d', 64));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        bmsonSong.sha256 = new string('f', 64);
        TestableBmsFile unmatchedBms = CreateFile("11111111111111111111111111111111", Path.Combine("C:\\Installed", "Other", "chart.bms"), new string('2', 64));
        var state = OwnedChartCollectionState.FromStorageRows([md5Bms, shaBms, unmatchedBms], [bmsonSong]);
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
        TestableBmsFile currentChartInfoFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Info", "chart.bms"), new string('b', 64));
        LR2SongDBExtended.bmson_song parseFailureSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Failure", "chart.bmson"), "cccccccccccccccccccccccccccccccc");
        TestableBmsFile backfillFile = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "Backfill", "chart.bms"), new string('e', 64));
        parseFailureSong.sha256 = new string('f', 64);
        var state = OwnedChartCollectionState.FromStorageRows([currentChartInfoFile, backfillFile], [parseFailureSong]);
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        TestableBmsFile pathlessBms = CreateFile("dddddddddddddddddddddddddddddddd", string.Empty);
        TestableBmsFile md5lessBms = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "md5less.bms"));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        LR2SongDBExtended.bmson_song pathlessBmson = CreateBmsonSong(null, "cccccccccccccccccccccccccccccccc");
        LR2SongDBExtended.bmson_song md5lessBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "md5less.bmson"), null);
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile, pathlessBms, md5lessBms], [bmsonSong, pathlessBmson, md5lessBmson]);

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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "OldBms", "chart.bms"));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "OldBmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath, new string('b', 64));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(oldBmsonPath, "cccccccccccccccccccccccccccccccc");
        bmsonSong.sha256 = new string('d', 64);
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
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
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bms, newBmsPath.ToLowerInvariant(), bmsFile.hash, bmsFile.sha256)));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.CreatePathKey(ChartFileKind.Bms, newBmsPath.ToLowerInvariant())));
        Assert.IsFalse(keys.Contains(ChartFileRuntimeStateKey.Create(ChartFileKind.Bmson, newBmsonPath.ToLowerInvariant(), bmsonSong.md5, bmsonSong.sha256)));
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath, new string('b', 64));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(oldBmsonPath, "cccccccccccccccccccccccccccccccc");
        bmsonSong.sha256 = new string('d', 64);
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
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
        TestableBmsFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
        TestableBmsFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc"
        };
        var state = OwnedChartCollectionState.FromStorageRows([first, second], [bmsonSong]);

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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = bmsonPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        TestableBmsFile staleSamePathBmsOwner = CreateFile("cccccccccccccccccccccccccccccccc", bmsonPath);

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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        TestableBmsFile other = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile, other], []);

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
        TestableBmsFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        TestableBmsFile samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        TestableBmsFile other = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        var state = OwnedChartCollectionState.FromStorageRows([first, samePath, other], [], out OwnedChartStorageRowFilterSummary filterSummary);

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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = bmsonPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
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
        TestableBmsFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        TestableBmsFile samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var state = OwnedChartCollectionState.FromStorageRows([first, samePath], [], out OwnedChartStorageRowFilterSummary filterSummary);
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null);
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);

        Assert.IsFalse(state.ContainsKnownChart(ChartFileProjection.FromBmsStorageOwnerIdentity(bmsFile)));
    }

    [TestMethod]
    public void ContainsKnownChart_DoesNotMatchOwnerWhoseCurrentPathDisappeared()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string oldPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
        string oldDirectory = Path.GetDirectoryName(oldPath)!;
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
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
        TestableBmsFile keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
        TestableBmsFile replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedBmsPath);
        TestableBmsFile newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedBmsPath);
        TestableBmsFile addedBms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine("C:\\Installed", "Bms", "added.bms"));
        LR2SongDBExtended.bmson_song keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "aaa.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        LR2SongDBExtended.bmson_song replacedBmson = CreateBmsonSong(replacedBmsonPath, "ffffffffffffffffffffffffffffffff");
        LR2SongDBExtended.bmson_song newBmson = CreateBmsonSong(replacedBmsonPath, "11111111111111111111111111111111");
        LR2SongDBExtended.bmson_song addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "zzz.bmson"), "22222222222222222222222222222222");
        var state = OwnedChartCollectionState.FromStorageRows([keptBms, replacedBms], [replacedBmson, keptBmson]);

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
    public void UpsertStorageRows_BmsSurvivorsKeepRelativeOrderAndReplacementAppends()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "B0.bms"));
        TestableBmsFile replaced = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "B1.bms"));
        TestableBmsFile third = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "B2.bms"));
        TestableBmsFile replacement = CreateFile("dddddddddddddddddddddddddddddddd", replaced.path);
        TestableBmsFile added = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Path.Combine("C:\\Installed", "Bms", "B3.bms"));
        var state = OwnedChartCollectionState.FromStorageRows([first, replaced, third], []);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertStorageRows([replacement, added], []);

        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);
        CollectionAssert.AreEqual(
            new[] { first, third, replacement, added },
            snapshot.Select(chart => chart.GetBmsStorageOwner()).ToArray());
        CollectionAssert.AreEqual(
            new[] { first, third, replacement, added },
            index.GetChartRefsUnderRealPath(Path.Combine("C:\\Installed", "Bms"))
                .Select(chart => chart.GetBmsStorageOwner())
                .ToArray());
    }

    [TestMethod]
    public void UpsertStorageRows_CanonicalBmsonUsesCapturedPathAndReplacementAppends()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directory = Path.Combine("C:\\Installed", "Bmson");
        LR2SongDBExtended.bmson_song z = CreateBmsonSong(Path.Combine(directory, "z.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        LR2SongDBExtended.bmson_song upper = CreateBmsonSong(Path.Combine(directory, "A.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        LR2SongDBExtended.bmson_song lower = CreateBmsonSong(Path.Combine(directory, "a.bmson"), "cccccccccccccccccccccccccccccccc");
        TestableBmsFile bms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine(directory, "added.bms"));
        LR2SongDBExtended.bmson_song replacement = CreateBmsonSong(upper.path, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var state = OwnedChartCollectionState.FromStorageRows([], [z, upper, lower]);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();

        state.UpsertStorageRows([bms], []);
        CollectionAssert.AreEqual(
            new object[] { bms, upper, lower, z },
            state.CreateSnapshot(includeResourceReferences: false)
                .Select(chart => chart.Kind == ChartFileKind.Bms
                    ? (object)chart.GetBmsStorageOwner()
                    : chart.GetBmsonStorageOwner())
                .ToArray());

        state.UpsertStorageRows([], [replacement]);
        CollectionAssert.AreEqual(
            new object[] { bms, lower, replacement, z },
            state.CreateSnapshot(includeResourceReferences: false)
                .Select(chart => chart.Kind == ChartFileKind.Bms
                    ? (object)chart.GetBmsStorageOwner()
                    : chart.GetBmsonStorageOwner())
                .ToArray());
        CollectionAssert.AreEqual(
            new object[] { bms, lower, replacement, z },
            index.GetChartRefsUnderRealPath(directory)
                .Select(chart => chart.Kind == LibraryChartKind.Bms
                    ? (object)chart.GetBmsStorageOwner()
                    : chart.GetBmsonStorageOwner())
                .ToArray());
    }

    [TestMethod]
    public void ApplyPathChanges_CanonicalBmsonKeepsCapturedSequencePosition()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directory = Path.Combine("C:\\Installed", "Bmson");
        LR2SongDBExtended.bmson_song z = CreateBmsonSong(Path.Combine(directory, "z.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        LR2SongDBExtended.bmson_song upper = CreateBmsonSong(Path.Combine(directory, "A.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        LR2SongDBExtended.bmson_song lower = CreateBmsonSong(Path.Combine(directory, "a.bmson"), "cccccccccccccccccccccccccccccccc");
        TestableBmsFile bms = CreateFile("dddddddddddddddddddddddddddddddd", Path.Combine(directory, "added.bms"));
        var state = OwnedChartCollectionState.FromStorageRows([], [z, upper, lower]);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        state.UpsertStorageRows([bms], []);

        string oldPath = upper.path!;
        string movedPath = Path.Combine(directory, "y.bmson");
        upper.path = movedPath;
        state.ApplyPathChanges([
            new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsonSong(upper),
                OldPath = oldPath,
                NewPath = movedPath
            }
        ]);

        CollectionAssert.AreEqual(
            new object[] { bms, upper, lower, z },
            state.CreateSnapshot(includeResourceReferences: false)
                .Select(chart => chart.Kind == ChartFileKind.Bms
                    ? (object)chart.GetBmsStorageOwner()
                    : chart.GetBmsonStorageOwner())
                .ToArray());
        CollectionAssert.AreEqual(
            new object[] { bms, upper, lower, z },
            index.GetChartRefsUnderRealPath(directory)
                .Select(chart => chart.Kind == LibraryChartKind.Bms
                    ? (object)chart.GetBmsStorageOwner()
                    : chart.GetBmsonStorageOwner())
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
        var bmsFiles = new List<BMSFile>(backgroundCount);
        for (int i = 0; i < backgroundCount; i++)
        {
            bmsFiles.Add(CreateFile(
                i.ToString("x32"),
                Path.Combine("C:\\CanonicalWork", $"background-{i}.bms")));
        }
        var bmsonSongs = new List<LR2SongDBExtended.bmson_song>(bmsonCount);
        for (int i = 0; i < bmsonCount; i++)
        {
            bmsonSongs.Add(CreateBmsonSong(
                Path.Combine("C:\\CanonicalWork", $"background-{i}.bmson"),
                (i + 1).ToString("x32")));
        }
        var state = OwnedChartCollectionState.FromStorageRows(
            bmsFiles,
            bmsonSongs,
            CancellationToken.None,
            workObserver,
            out _);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        List<ChartFile> oldSnapshot = state.CreateSnapshot(includeResourceReferences: false);
        workObserver.Reset();
        state.UpsertStorageRows([
            CreateFile(
                new string('e', 32),
                Path.Combine("C:\\CanonicalWork", $"cold-{backgroundCount}.bms"))
        ], []);
        CanonicalWorkCounts cold = workObserver.Capture();
        Assert.AreEqual(bmsonCount, cold.VisitedEntryCount);
        Assert.AreEqual(bmsonCount, cold.MaterializationCount);
        workObserver.Reset();

        BMSFile firstDelta = CreateFile(
            new string('d', 32),
            Path.Combine("C:\\CanonicalWork", $"delta-{backgroundCount}.bms"));
        TestableBmsFile replacement = CreateFile(firstDelta.hash, firstDelta.path);
        replacement.SetHash(new string('c', 32));
        state.UpsertStorageRows([firstDelta], []);
        Assert.AreSame(firstDelta, index.GetChartRefsByPaths([firstDelta.path]).Single().GetBmsStorageOwner());
        state.UpsertStorageRows([replacement], []);
        Assert.AreSame(replacement, index.GetChartRefsByPaths([replacement.path]).Single().GetBmsStorageOwner());
        CanonicalWorkCounts warm = workObserver.Capture();

        Assert.AreEqual(backgroundCount + bmsonCount, oldSnapshot.Count);
        Assert.IsTrue(oldSnapshot.Any(chart => chart.GetBmsonStorageOwner() != null));
        Assert.AreEqual(backgroundCount + bmsonCount + 2, state.CreateSnapshot(includeResourceReferences: false).Count);
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);

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
        TestableBmsFile movedBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldPath);
        TestableBmsFile removedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "removed.bms"));
        TestableBmsFile addedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", "added.bms"));
        LR2SongDBExtended.bmson_song addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "dddddddddddddddddddddddddddddddd");
        var state = OwnedChartCollectionState.FromStorageRows([movedBms, removedBms], []);
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
        LR2SongDBExtended.bmson_song removedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "removed.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        LR2SongDBExtended.bmson_song keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "kept.bmson"), "cccccccccccccccccccccccccccccccc");
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], [removedBmson, keptBmson]);
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
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", path, new string('1', 64));
        var state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
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
