using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// playlist lamp の score/category mapping と count/rate 計算だけを担う pure service です。
/// </summary>
internal sealed class PlaylistLampAggregationService
{
    private static readonly PlaylistLampClearCategory[] clearCategoryOrder =
    [
        PlaylistLampClearCategory.MAX,
        PlaylistLampClearCategory.PERFECT,
        PlaylistLampClearCategory.FC,
        PlaylistLampClearCategory.EXHARD,
        PlaylistLampClearCategory.HARD,
        PlaylistLampClearCategory.NORMAL,
        PlaylistLampClearCategory.EASY,
        PlaylistLampClearCategory.ASSIST,
        PlaylistLampClearCategory.FAILED,
        PlaylistLampClearCategory.NP
    ];

    private static readonly PlaylistLampRankCategory[] rankCategoryOrder =
    [
        PlaylistLampRankCategory.AAA,
        PlaylistLampRankCategory.AA,
        PlaylistLampRankCategory.A,
        PlaylistLampRankCategory.B,
        PlaylistLampRankCategory.C,
        PlaylistLampRankCategory.D,
        PlaylistLampRankCategory.E,
        PlaylistLampRankCategory.F,
        PlaylistLampRankCategory.NP
    ];

    /// <summary>
    /// clear category の公開順です。
    /// </summary>
    internal IReadOnlyList<PlaylistLampClearCategory> ClearCategoryOrder => clearCategoryOrder;

    /// <summary>
    /// DJレベル category の公開順です。
    /// </summary>
    internal IReadOnlyList<PlaylistLampRankCategory> RankCategoryOrder => rankCategoryOrder;

    /// <summary>
    /// immutable request を集計します。
    /// </summary>
    /// <param name="request">集計対象 snapshot。</param>
    /// <param name="cancellationToken">長い playlist の集計を中断する token。</param>
    /// <returns>immutable なランプ集計結果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> が null の場合。</exception>
    internal PlaylistLampAggregationResult Aggregate(
        PlaylistLampAggregationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.InputState)
        {
            case PlaylistLampInputState.Loading:
                return CreateTerminalResult(request, PlaylistLampViewerState.Loading, string.Empty);
            case PlaylistLampInputState.Deleted:
                return CreateTerminalResult(request, PlaylistLampViewerState.Deleted, string.Empty);
            case PlaylistLampInputState.Failed:
                return CreateTerminalResult(request, PlaylistLampViewerState.Failed, request.FailureMessage);
        }

        PlaylistLampScoreSnapshot scoreSnapshot = request.ScoreSnapshot;
        bool scoreDataAvailable = scoreSnapshot.IsScoreDataAvailable;
        List<string> folderOrder = BuildFolderOrder(request);
        var entriesByFolder = folderOrder.ToDictionary(
            folder => folder,
            _ => new List<PlaylistLampEntrySnapshot>(),
            StringComparer.Ordinal);
        foreach (PlaylistLampEntrySnapshot entry in request.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry == null || !entry.IsActiveRealEntry || IsSyntheticFolder(entry.FolderName))
            {
                continue;
            }
            string folder = entry.FolderName ?? string.Empty;
            if (!entriesByFolder.TryGetValue(folder, out List<PlaylistLampEntrySnapshot> folderEntries))
            {
                // Request builders normally source this list directly from BMSTable.folder_list.
                // Keeping an unexpected active folder at the end prevents data loss in a manually
                // constructed snapshot while preserving the repository's canonical order first.
                folderEntries = [];
                entriesByFolder.Add(folder, folderEntries);
                folderOrder.Add(folder);
            }
            folderEntries.Add(entry);
        }

        Dictionary<PlaylistLampClearCategory, int> clearCounts = CreateClearCounts();
        Dictionary<PlaylistLampRankCategory, int> rankCounts = CreateRankCounts();
        var folderRows = new List<PlaylistLampFolderRow>(folderOrder.Count);
        int totalCount = 0;
        int ownedCount = 0;
        int playedCount = 0;
        int clearCount = 0;
        var exRates = new List<double>();
        foreach (string folder in folderOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PlaylistLampEntrySnapshot> uniqueEntries = DistinctEntries(entriesByFolder[folder]);
            int folderCount = uniqueEntries.Count;
            int folderOwnedCount = uniqueEntries.Count(entry => entry.IsOwned);
            int folderMissingCount = folderCount - folderOwnedCount;
            Dictionary<PlaylistLampClearCategory, int> folderClearCounts = CreateClearCounts();
            Dictionary<PlaylistLampRankCategory, int> folderRankCounts = CreateRankCounts();
            foreach (PlaylistLampEntrySnapshot entry in uniqueEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalCount++;
                if (entry.IsOwned)
                {
                    ownedCount++;
                }
                if (!scoreDataAvailable)
                {
                    continue;
                }
                PlaylistLampScore score = scoreSnapshot.Resolve(entry);
                if (score == null)
                {
                    Increment(clearCounts, folderClearCounts, PlaylistLampClearCategory.NP);
                    Increment(rankCounts, folderRankCounts, PlaylistLampRankCategory.NP);
                    continue;
                }

                if (IsNoPlay(score.Clear))
                {
                    Increment(clearCounts, folderClearCounts, PlaylistLampClearCategory.NP);
                    Increment(rankCounts, folderRankCounts, PlaylistLampRankCategory.NP);
                    continue;
                }
                playedCount++;
                PlaylistLampClearCategory clearCategory = ToClearCategory(scoreSnapshot.Source, score.Clear);
                PlaylistLampRankCategory rankCategory = ToRankCategory(score.Rank);
                Increment(clearCounts, folderClearCounts, clearCategory);
                Increment(rankCounts, folderRankCounts, rankCategory);
                if (IsCleared(clearCategory))
                {
                    clearCount++;
                }
                if (score.TotalNotes > 0)
                {
                    double? rate = ScoreValueCalculator.CalculateRateDouble(score.ExScore, score.TotalNotes);
                    if (rate.HasValue)
                    {
                        exRates.Add(rate.Value);
                    }
                }
            }

            IReadOnlyList<PlaylistLampSegment> clearSegments = BuildClearSegments(
                folderClearCounts,
                folderCount,
                scoreDataAvailable,
                request.PlaylistId,
                folder);
            IReadOnlyList<PlaylistLampSegment> rankSegments = BuildRankSegments(
                folderRankCounts,
                folderCount,
                scoreDataAvailable,
                request.PlaylistId,
                folder);
            folderRows.Add(new PlaylistLampFolderRow(
                folder,
                folderCount,
                folderOwnedCount,
                folderMissingCount,
                clearSegments,
                rankSegments));
        }

        // The dictionaries above are used as global counters while each folder is built.
        // Recompute them from rows' immutable source entries so that the global graph remains
        // independent from any later mutation of the input collection.
        clearCounts = CreateClearCounts();
        rankCounts = CreateRankCounts();
        playedCount = 0;
        clearCount = 0;
        exRates.Clear();
        foreach (PlaylistLampFolderRow row in folderRows)
        {
            foreach (PlaylistLampSegment segment in row.ClearSegments)
            {
                if (segment.ClearCategory.HasValue)
                {
                    clearCounts[segment.ClearCategory.Value] += segment.Count;
                }
            }
            foreach (PlaylistLampSegment segment in row.RankSegments)
            {
                if (segment.RankCategory.HasValue)
                {
                    rankCounts[segment.RankCategory.Value] += segment.Count;
                }
            }
        }
        // Counts needed for statistics are derived directly from the folder rows and entries to
        // avoid depending on graph presentation mechanics when score source is degraded.
        if (scoreDataAvailable)
        {
            foreach (string folder in folderOrder)
            {
                foreach (PlaylistLampEntrySnapshot entry in DistinctEntries(entriesByFolder[folder]))
                {
                    PlaylistLampScore score = scoreSnapshot.Resolve(entry);
                    if (score == null)
                    {
                        continue;
                    }
                    if (IsNoPlay(score.Clear))
                    {
                        continue;
                    }
                    playedCount++;
                    PlaylistLampClearCategory category = ToClearCategory(scoreSnapshot.Source, score.Clear);
                    if (IsCleared(category))
                    {
                        clearCount++;
                    }
                    if (score.TotalNotes > 0)
                    {
                        double? rate = ScoreValueCalculator.CalculateRateDouble(score.ExScore, score.TotalNotes);
                        if (rate.HasValue)
                        {
                            exRates.Add(rate.Value);
                        }
                    }
                }
            }
        }

        int missingCount = totalCount - ownedCount;
        double? ownershipRate = Ratio(ownedCount, totalCount);
        double? playRate = scoreDataAvailable ? Ratio(playedCount, totalCount) : null;
        double? averageExRate = scoreDataAvailable && exRates.Count > 0 ? exRates.Average() : null;
        double? clearRate = scoreDataAvailable ? Ratio(clearCount, totalCount) : null;
        var statistics = new PlaylistLampStatistics(
            totalCount,
            ownedCount,
            missingCount,
            scoreDataAvailable ? playedCount : null,
            scoreDataAvailable ? totalCount - playedCount : null,
            ownershipRate,
            playRate,
            averageExRate,
            clearRate,
            scoreDataAvailable,
            scoreDataAvailable ? scoreSnapshot.LastUpdatedUtc : null,
            request.PlaylistLastUpdated);
        PlaylistLampViewerState state = totalCount == 0
            ? PlaylistLampViewerState.Empty
            : PlaylistLampViewerState.Ready;
        IReadOnlyList<PlaylistLampSegment> globalClearSegments = BuildClearSegments(
            clearCounts,
            totalCount,
            scoreDataAvailable,
            request.PlaylistId,
            null);
        IReadOnlyList<PlaylistLampSegment> globalRankSegments = BuildRankSegments(
            rankCounts,
            totalCount,
            scoreDataAvailable,
            request.PlaylistId,
            null);
        return new PlaylistLampAggregationResult(
            request.PlaylistId,
            state,
            new List<PlaylistLampFolderRow>(folderRows).AsReadOnly(),
            globalClearSegments,
            globalRankSegments,
            statistics,
            scoreSnapshot,
            string.Empty,
            request.Query,
            request.HistoricalDateRange,
            request.HistoricalStatus,
            request.HistoricalFailureMessage);
    }

    private static PlaylistLampAggregationResult CreateTerminalResult(
        PlaylistLampAggregationRequest request,
        PlaylistLampViewerState state,
        string failureMessage)
    {
        PlaylistLampStatistics statistics = new(
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            request.PlaylistLastUpdated);
        return new PlaylistLampAggregationResult(
            request.PlaylistId,
            state,
            [],
            [],
            [],
            statistics,
            request.ScoreSnapshot,
            failureMessage,
            request.Query,
            request.HistoricalDateRange,
            request.HistoricalStatus,
            request.HistoricalFailureMessage);
    }

    private static List<string> BuildFolderOrder(PlaylistLampAggregationRequest request)
    {
        var folders = new List<string>(request.FolderOrder);
        var known = new HashSet<string>(folders, StringComparer.Ordinal);
        foreach (PlaylistLampEntrySnapshot entry in request.Entries)
        {
            if (entry == null || !entry.IsActiveRealEntry || IsSyntheticFolder(entry.FolderName))
            {
                continue;
            }
            string folder = entry.FolderName ?? string.Empty;
            if (known.Add(folder))
            {
                folders.Add(folder);
            }
        }
        return folders;
    }

    private static List<PlaylistLampEntrySnapshot> DistinctEntries(IReadOnlyList<PlaylistLampEntrySnapshot> entries)
    {
        var result = new List<PlaylistLampEntrySnapshot>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int unknownOrdinal = 0;
        foreach (PlaylistLampEntrySnapshot entry in entries ?? [])
        {
            string identity = entry?.IdentityKey;
            if (string.IsNullOrWhiteSpace(identity))
            {
                identity = "unknown:" + unknownOrdinal++;
            }
            if (indexes.TryGetValue(identity, out int index))
            {
                result[index] = result[index].Merge(entry);
            }
            else
            {
                indexes.Add(identity, result.Count);
                result.Add(entry);
            }
        }
        return result;
    }

    private static bool IsSyntheticFolder(string folderName)
    {
        return string.Equals(folderName, "[NO SONG]", StringComparison.Ordinal);
    }

    private static Dictionary<PlaylistLampClearCategory, int> CreateClearCounts()
    {
        return clearCategoryOrder.ToDictionary(category => category, _ => 0);
    }

    private static Dictionary<PlaylistLampRankCategory, int> CreateRankCounts()
    {
        return rankCategoryOrder.ToDictionary(category => category, _ => 0);
    }

    private static void Increment(
        IDictionary<PlaylistLampClearCategory, int> global,
        IDictionary<PlaylistLampClearCategory, int> folder,
        PlaylistLampClearCategory category)
    {
        global[category]++;
        folder[category]++;
    }

    private static void Increment(
        IDictionary<PlaylistLampRankCategory, int> global,
        IDictionary<PlaylistLampRankCategory, int> folder,
        PlaylistLampRankCategory category)
    {
        global[category]++;
        folder[category]++;
    }

    private static IReadOnlyList<PlaylistLampSegment> BuildClearSegments(
        IReadOnlyDictionary<PlaylistLampClearCategory, int> counts,
        int denominator,
        bool scoreDataAvailable,
        string playlistId,
        string folderName)
    {
        var segments = new List<PlaylistLampSegment>(clearCategoryOrder.Length);
        foreach (PlaylistLampClearCategory category in clearCategoryOrder)
        {
            int count = counts.TryGetValue(category, out int value) ? value : 0;
            segments.Add(PlaylistLampSegment.CreateClear(
                category,
                count,
                denominator,
                scoreDataAvailable,
                !string.IsNullOrWhiteSpace(playlistId) && folderName != null,
                folderName));
        }
        return segments.AsReadOnly();
    }

    private static IReadOnlyList<PlaylistLampSegment> BuildRankSegments(
        IReadOnlyDictionary<PlaylistLampRankCategory, int> counts,
        int denominator,
        bool scoreDataAvailable,
        string playlistId,
        string folderName)
    {
        var segments = new List<PlaylistLampSegment>(rankCategoryOrder.Length);
        foreach (PlaylistLampRankCategory category in rankCategoryOrder)
        {
            int count = counts.TryGetValue(category, out int value) ? value : 0;
            segments.Add(PlaylistLampSegment.CreateRank(
                category,
                count,
                denominator,
                scoreDataAvailable,
                !string.IsNullOrWhiteSpace(playlistId) && folderName != null,
                folderName));
        }
        return segments.AsReadOnly();
    }

    private static double? Ratio(int numerator, int denominator)
    {
        return denominator > 0 ? (double)numerator / denominator : null;
    }

    private static bool IsNoPlay(ClearType clear)
    {
        return clear == ClearType.NO_PLAY || clear == ClearType.NO_SONG;
    }

    private static bool IsCleared(PlaylistLampClearCategory category)
    {
        return category is PlaylistLampClearCategory.MAX
            or PlaylistLampClearCategory.PERFECT
            or PlaylistLampClearCategory.FC
            or PlaylistLampClearCategory.EXHARD
            or PlaylistLampClearCategory.HARD
            or PlaylistLampClearCategory.NORMAL
            or PlaylistLampClearCategory.EASY
            or PlaylistLampClearCategory.ASSIST;
    }

    private static PlaylistLampClearCategory ToClearCategory(ActiveScoreSource source, ClearType clear)
    {
        if (source == ActiveScoreSource.Lr2)
        {
            // LR2's persisted values collapse EX_HARD into HARD and MAX into FC. Keep
            // those storage-equivalent buckets rather than exposing categories LR2 cannot
            // distinguish. PA remains distinct in LR2's explicit value 21/op-history.
            return clear switch
            {
                ClearType.MAX => PlaylistLampClearCategory.FC,
                ClearType.EX_HARD => PlaylistLampClearCategory.HARD,
                ClearType.PA => PlaylistLampClearCategory.PERFECT,
                _ => ToDirectClearCategory(clear)
            };
        }
        return clear switch
        {
            ClearType.PA => PlaylistLampClearCategory.PERFECT,
            ClearType.CLEAR => PlaylistLampClearCategory.NORMAL,
            ClearType.INVALID or ClearType.L_ASSIST => PlaylistLampClearCategory.ASSIST,
            _ => ToDirectClearCategory(clear)
        };
    }

    private static PlaylistLampClearCategory ToDirectClearCategory(ClearType clear)
    {
        return clear switch
        {
            ClearType.MAX => PlaylistLampClearCategory.MAX,
            ClearType.EX_HARD => PlaylistLampClearCategory.EXHARD,
            ClearType.FC => PlaylistLampClearCategory.FC,
            ClearType.HARD => PlaylistLampClearCategory.HARD,
            ClearType.CLEAR => PlaylistLampClearCategory.NORMAL,
            ClearType.EASY => PlaylistLampClearCategory.EASY,
            ClearType.INVALID or ClearType.L_ASSIST => PlaylistLampClearCategory.ASSIST,
            ClearType.FAILED => PlaylistLampClearCategory.FAILED,
            ClearType.NO_PLAY or ClearType.NO_SONG => PlaylistLampClearCategory.NP,
            _ => throw new InvalidOperationException("Unsupported clear value: " + clear)
        };
    }

    private static PlaylistLampRankCategory ToRankCategory(RankType rank)
    {
        return rank switch
        {
            RankType.MAX or RankType.AAA => PlaylistLampRankCategory.AAA,
            RankType.AA => PlaylistLampRankCategory.AA,
            RankType.A => PlaylistLampRankCategory.A,
            RankType.B => PlaylistLampRankCategory.B,
            RankType.C => PlaylistLampRankCategory.C,
            RankType.D => PlaylistLampRankCategory.D,
            RankType.E => PlaylistLampRankCategory.E,
            RankType.F or RankType.INVALID => PlaylistLampRankCategory.F,
            _ => throw new InvalidOperationException("Unsupported rank value: " + rank)
        };
    }
}
