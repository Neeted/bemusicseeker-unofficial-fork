using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistEntryScoreSnapshotResolver
{
    internal static BMSScore Resolve(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BeMusicSeeker.Models.ChartDetails entryChartInfo,
        BMSLibrary.ScoreSnapshot scoreSnapshot,
        IReadOnlyDictionary<string, BMSScore> scoresByHash,
        IReadOnlyDictionary<string, BMSScore> scoresBySha256,
        Action<string> observeWork = null)
        => Resolve(entry?.CaptureValue(), resolvedChart, entryChartInfo, scoreSnapshot, scoresByHash, scoresBySha256, observeWork);

    /// <summary>正本から捕捉した不変 hash だけで独立した score owner の値を解決します。</summary>
    internal static BMSScore Resolve(
        PlaylistEntryValue entry,
        ChartFile resolvedChart,
        BeMusicSeeker.Models.ChartDetails entryChartInfo,
        BMSLibrary.ScoreSnapshot scoreSnapshot,
        IReadOnlyDictionary<string, BMSScore> scoresByHash,
        IReadOnlyDictionary<string, BMSScore> scoresBySha256,
        Action<string> observeWork = null)
    {
        if (entry == null || scoreSnapshot == null)
        {
            return null;
        }
        if (scoreSnapshot.ActiveScoreSource == ActiveScoreSource.Lr2)
        {
            string hash = FirstNonEmpty(resolvedChart?.Md5, entry.Md5);
            observeWork?.Invoke("score_projection_lookup");
            if (scoresByHash != null && scoresByHash.TryGetValue(hash, out BMSScore lr2Score))
            {
                return lr2Score;
            }
            return null;
        }
        if (scoreSnapshot.ActiveScoreSource == ActiveScoreSource.Beatoraja && scoresBySha256 != null)
        {
            string sha256 = FirstNonEmpty(resolvedChart?.Sha256, entry.Sha256, entryChartInfo?.sha256);
            observeWork?.Invoke("score_projection_lookup");
            if (scoresBySha256.TryGetValue(sha256, out BMSScore beatorajaScore))
            {
                string hash = FirstNonEmpty(resolvedChart?.Md5, entry.Md5);
                return string.IsNullOrWhiteSpace(hash)
                    ? beatorajaScore
                    : BmsLibraryIrService.CloneScoreForFileHash(beatorajaScore, hash);
            }
        }
        return null;
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        if (candidates == null)
        {
            return string.Empty;
        }
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }
}
