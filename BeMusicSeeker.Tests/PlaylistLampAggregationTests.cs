using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// PLV-02〜08 の pure lamp aggregation contract tests.
/// </summary>
[TestClass]
public sealed class PlaylistLampAggregationTests
{
    private static readonly DateTime PlaylistUpdatedAt = new(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc);

    private static readonly DateTime AggregatedAt = new(2026, 8, 28, 2, 3, 4, DateTimeKind.Utc);

    [TestMethod]
    public void Aggregate_usesCanonicalFolderOrderKeepsEmptyAndExcludesNonRealEntries()
    {
        PlaylistLampScoreSnapshot scoreSnapshot = CreateScoreSnapshot(
            ActiveScoreSource.Beatoraja,
            new PlaylistLampScore(
                "",
                "a",
                ClearType.PA,
                RankType.MAX,
                100,
                0,
                100,
                1));
        var entries = new[]
        {
            Entry("z", "chart-1", owned: true, sha256: "a"),
            Entry("z", "chart-1", owned: false, sha256: "a"),
            Entry("a", "chart-1", owned: true, sha256: "a"),
            Entry("a", "chart-2", owned: false, sha256: "b"),
            Entry("empty", "dummy", owned: false, md5: BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, isDummy: true),
            Entry("z", "removed", owned: true, sha256: "a", isRemoved: true),
            Entry("[NO SONG]", "special", owned: true, sha256: "a")
        };
        var request = new PlaylistLampAggregationRequest(
            "42",
            ["z", "a", "empty", "[NO SONG]"],
            entries,
            scoreSnapshot,
            PlaylistUpdatedAt,
            AggregatedAt);

        PlaylistLampAggregationResult result = new PlaylistLampAggregationService().Aggregate(request);

        Assert.AreEqual(PlaylistLampViewerState.Ready, result.State);
        CollectionAssert.AreEqual(new[] { "z", "a", "empty" }, result.FolderRows.Select(row => row.FolderName).ToArray());
        Assert.AreEqual(3, result.Statistics.TotalCount);
        Assert.AreEqual(2, result.Statistics.OwnedCount);
        Assert.AreEqual(1, result.Statistics.MissingCount);
        Assert.AreEqual(1, result.FolderRows[0].Count);
        Assert.AreEqual(2, result.FolderRows[1].Count);
        Assert.AreEqual(0, result.FolderRows[2].Count);
        Assert.AreEqual(1, Count(result.FolderRows[0].ClearSegments, PlaylistLampClearCategory.PERFECT));
        Assert.AreEqual(1, Count(result.FolderRows[1].ClearSegments, PlaylistLampClearCategory.PERFECT));
        Assert.AreEqual(1, Count(result.FolderRows[1].ClearSegments, PlaylistLampClearCategory.NS));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.NS));
        Assert.AreEqual(3, result.ClearSegments.Sum(segment => segment.Count));
        Assert.AreEqual(3, result.RankSegments.Sum(segment => segment.Count));
        Assert.AreEqual(PlaylistLampClearCategory.MAX, result.ClearSegments[0].ClearCategory);
        Assert.AreEqual(PlaylistLampClearCategory.NS, result.ClearSegments[^1].ClearCategory);
        Assert.AreEqual(PlaylistLampRankCategory.AAA, result.RankSegments[0].RankCategory);
    }

    [TestMethod]
    public void Aggregate_mapsBeatorajaClearValuesAndDjRanksInContractOrder()
    {
        var scores = new[]
        {
            Score("pa", "pa", ClearType.PA, RankType.MAX),
            Score("clear", "clear", ClearType.CLEAR, RankType.AA),
            Score("invalid", "invalid", ClearType.INVALID, RankType.A),
            Score("assist", "assist", ClearType.L_ASSIST, RankType.B),
            Score("exhard", "exhard", ClearType.EX_HARD, RankType.C),
            Score("max", "max", ClearType.MAX, RankType.D)
        };
        PlaylistLampScoreSnapshot snapshot = CreateScoreSnapshot(ActiveScoreSource.Beatoraja, scores);
        var entries = scores.Select((score, index) => Entry("folder", "chart-" + index, true, sha256: score.Sha256)).ToArray();
        PlaylistLampAggregationResult result = Aggregate("beatoraja", ["folder"], entries, snapshot);

        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.PERFECT));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.NORMAL));
        Assert.AreEqual(2, Count(result.ClearSegments, PlaylistLampClearCategory.ASSIST));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.EXHARD));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.MAX));
        CollectionAssert.AreEqual(
            new[] { "AAA", "AA", "A", "B", "C", "D", "E", "F", "NP", "NS" },
            result.RankSegments.Select(segment => segment.RankCategory!.Value.ToString()).ToArray());
        Assert.AreEqual(1, Count(result.RankSegments, PlaylistLampRankCategory.AAA));
        Assert.AreEqual(1, Count(result.RankSegments, PlaylistLampRankCategory.AA));
        Assert.AreEqual(1, Count(result.RankSegments, PlaylistLampRankCategory.A));
    }

    [TestMethod]
    public void Aggregate_lr2DoesNotExposeMaxOrExhardAndUsesStorageEquivalentBuckets()
    {
        var scores = new[]
        {
            Score("max", "", ClearType.MAX, RankType.AAA),
            Score("exhard", "", ClearType.EX_HARD, RankType.AAA),
            Score("pa", "", ClearType.PA, RankType.AAA)
        };
        PlaylistLampScoreSnapshot snapshot = CreateScoreSnapshot(ActiveScoreSource.Lr2, scores);
        PlaylistLampAggregationResult result = Aggregate(
            "lr2",
            ["folder"],
            scores.Select(score => Entry("folder", score.Hash, true, md5: score.Hash)).ToArray(),
            snapshot);

        Assert.AreEqual(0, Count(result.ClearSegments, PlaylistLampClearCategory.MAX));
        Assert.AreEqual(0, Count(result.ClearSegments, PlaylistLampClearCategory.EXHARD));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.FC));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.HARD));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.PERFECT));
        Assert.AreEqual(3, result.ClearSegments.Sum(segment => segment.Count));
    }

    [TestMethod]
    public void Aggregate_usesFolderDenominatorAndCalculatesUnequalExRates()
    {
        var scores = new[]
        {
            Score("one", "one", ClearType.CLEAR, RankType.A, perfect: 100, totalNotes: 100),
            Score("two", "two", ClearType.FAILED, RankType.B, perfect: 50, totalNotes: 100)
        };
        PlaylistLampScoreSnapshot snapshot = CreateScoreSnapshot(ActiveScoreSource.Beatoraja, scores);
        var entries = new[]
        {
            Entry("folder", "one", true, sha256: "one"),
            Entry("folder", "two", true, sha256: "two"),
            Entry("folder", "three", true, sha256: "three"),
            Entry("folder", "missing", false, sha256: "missing")
        };
        PlaylistLampAggregationResult result = Aggregate("stats", ["folder"], entries, snapshot);

        Assert.AreEqual(4, result.FolderRows[0].Count);
        Assert.AreEqual(25.0, result.FolderRows[0].ClearSegments.Single(segment => segment.ClearCategory == PlaylistLampClearCategory.NORMAL).Percentage!.Value, 0.0001);
        Assert.AreEqual(75.0, result.Statistics.OwnershipRate!.Value * 100.0, 0.0001);
        Assert.AreEqual(2, result.Statistics.PlayedCount);
        Assert.AreEqual(1, result.Statistics.NoScoreOwnedCount);
        Assert.AreEqual(2.0 / 3.0, result.Statistics.PlayRate!.Value, 0.0001);
        Assert.AreEqual(0.75, result.Statistics.AverageExRate!.Value, 0.0001);
        Assert.AreEqual(0.25, result.Statistics.ClearRate!.Value, 0.0001);
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.NP));
        Assert.AreEqual(1, Count(result.ClearSegments, PlaylistLampClearCategory.NS));
    }

    [TestMethod]
    public void Aggregate_zeroAndOneOf205CategoriesHaveSafeWidthAndInvocationSemantics()
    {
        PlaylistLampScore score = Score("one", "one", ClearType.CLEAR, RankType.A);
        PlaylistLampScoreSnapshot snapshot = CreateScoreSnapshot(ActiveScoreSource.Beatoraja, score);
        var entries = Enumerable.Range(0, 205)
            .Select(index => Entry("folder", "chart-" + index, true, sha256: index == 0 ? "one" : "other-" + index))
            .ToArray();
        PlaylistLampAggregationResult result = Aggregate("205", ["folder"], entries, snapshot);
        PlaylistLampSegment globalNormal = result.ClearSegments.Single(segment => segment.ClearCategory == PlaylistLampClearCategory.NORMAL);
        PlaylistLampSegment normal = result.FolderRows[0].ClearSegments.Single(segment => segment.ClearCategory == PlaylistLampClearCategory.NORMAL);
        PlaylistLampSegment failed = result.FolderRows[0].ClearSegments.Single(segment => segment.ClearCategory == PlaylistLampClearCategory.FAILED);

        Assert.AreEqual(205, normal.Denominator);
        Assert.AreEqual(1, normal.Count);
        Assert.AreEqual(100.0 / 205.0, normal.Percentage!.Value, 0.0001);
        Assert.AreEqual(normal.Percentage!.Value, normal.PositiveWidthPercentage!.Value, 0.0001);
        Assert.IsTrue(normal.IsInvokable);
        Assert.AreEqual(normal.Count, globalNormal.Count);
        Assert.IsNull(failed.PositiveWidthPercentage);
        Assert.IsFalse(failed.IsInvokable);
        PlaylistLampSegmentInvocationRequest invocation = normal.CreateInvocationRequest("205");
        Assert.IsNotNull(invocation);
        Assert.AreEqual("205", invocation.PlaylistId);
        Assert.AreEqual("folder", invocation.FolderName);
        Assert.AreEqual(PlaylistLampClearCategory.NORMAL, invocation.ClearCategory);
        Assert.IsNull(failed.CreateInvocationRequest("205"));
    }

    [TestMethod]
    public void Aggregate_scoreSourceNoneOrFailedKeepsOwnershipButDoesNotInventNpOrGraphData()
    {
        var entries = new[]
        {
            Entry("folder", "owned", true, md5: "owned"),
            Entry("folder", "missing", false, md5: "missing")
        };
        foreach (ScoreTableLoadStatus status in new[] { ScoreTableLoadStatus.NotConfigured, ScoreTableLoadStatus.Failed })
        {
            var snapshot = new PlaylistLampScoreSnapshot(
                status == ScoreTableLoadStatus.Failed ? ActiveScoreSource.Lr2 : ActiveScoreSource.None,
                status,
                7,
                8,
                null,
                failureMessage: status == ScoreTableLoadStatus.Failed ? "load failed" : null);
            PlaylistLampAggregationResult result = Aggregate("degraded", ["folder"], entries, snapshot);

            Assert.AreEqual(2, result.Statistics.TotalCount);
            Assert.AreEqual(1, result.Statistics.OwnedCount);
            Assert.AreEqual(1, result.Statistics.MissingCount);
            Assert.IsFalse(result.ScoreDataAvailable);
            Assert.IsNull(result.Statistics.PlayedCount);
            Assert.IsNull(result.Statistics.NoScoreOwnedCount);
            Assert.IsNull(result.Statistics.PlayRate);
            Assert.IsNull(result.Statistics.ClearRate);
            Assert.IsFalse(result.IsSegmentInvocationEnabled);
            Assert.AreEqual(0, result.ClearSegments.Sum(segment => segment.Count));
            Assert.IsFalse(result.ClearSegments.Any(segment => segment.IsInvokable));
        }
    }

    [TestMethod]
    public void Aggregate_emptyPlaylistPreservesOrdinaryFoldersAndLeavesRatesUnavailable()
    {
        PlaylistLampScoreSnapshot snapshot = CreateScoreSnapshot(ActiveScoreSource.Lr2);
        PlaylistLampAggregationResult result = Aggregate("empty", ["first", "second"], [], snapshot);

        Assert.AreEqual(PlaylistLampViewerState.Empty, result.State);
        CollectionAssert.AreEqual(new[] { "first", "second" }, result.FolderRows.Select(row => row.FolderName).ToArray());
        Assert.IsTrue(result.FolderRows.All(row => row.Count == 0));
        Assert.IsNull(result.Statistics.OwnershipRate);
        Assert.IsNull(result.Statistics.PlayRate);
        Assert.IsNull(result.Statistics.AverageExRate);
        Assert.IsNull(result.Statistics.ClearRate);
        Assert.IsTrue(result.ClearSegments.All(segment => segment.Count == 0 && segment.Percentage == null));
    }

    private static PlaylistLampAggregationResult Aggregate(
        string playlistId,
        IEnumerable<string> folders,
        IEnumerable<PlaylistLampEntrySnapshot> entries,
        PlaylistLampScoreSnapshot scoreSnapshot)
    {
        return new PlaylistLampAggregationService().Aggregate(new PlaylistLampAggregationRequest(
            playlistId,
            folders,
            entries,
            scoreSnapshot,
            PlaylistUpdatedAt,
            AggregatedAt));
    }

    private static PlaylistLampEntrySnapshot Entry(
        string folder,
        string identity,
        bool owned,
        string? md5 = null,
        string? sha256 = null,
        bool isRemoved = false,
        bool isDummy = false)
    {
        return new PlaylistLampEntrySnapshot(
            folder,
            identity,
            owned,
            md5,
            sha256,
            owned ? "C:/charts/" + identity + ".bms" : null,
            md5,
            sha256,
            isRemoved,
            isDummy);
    }

    private static PlaylistLampScore Score(
        string hash,
        string sha256,
        ClearType clear,
        RankType rank,
        int perfect = 0,
        int totalNotes = 100)
    {
        return new PlaylistLampScore(hash, sha256, clear, rank, perfect, 0, totalNotes, 1);
    }

    private static PlaylistLampScoreSnapshot CreateScoreSnapshot(
        ActiveScoreSource source,
        params PlaylistLampScore[] scores)
    {
        var byHash = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase);
        var bySha256 = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistLampScore score in scores ?? [])
        {
            if (!string.IsNullOrWhiteSpace(score.Hash))
            {
                byHash[score.Hash] = score;
            }
            if (!string.IsNullOrWhiteSpace(score.Sha256))
            {
                bySha256[score.Sha256] = score;
            }
        }
        return new PlaylistLampScoreSnapshot(
            source,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            AggregatedAt,
            byHash,
            bySha256);
    }

    private static int Count(IEnumerable<PlaylistLampSegment> segments, PlaylistLampClearCategory category)
    {
        return segments.Single(segment => segment.ClearCategory == category).Count;
    }

    private static int Count(IEnumerable<PlaylistLampSegment> segments, PlaylistLampRankCategory category)
    {
        return segments.Single(segment => segment.RankCategory == category).Count;
    }
}
