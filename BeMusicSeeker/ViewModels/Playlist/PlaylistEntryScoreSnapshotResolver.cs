using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistEntryScoreSnapshotResolver
{
    internal static BMSScore Resolve(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        LR2SongDBExtended.chart_info entryChartInfo,
        BMSLibrary.ScoreSnapshot scoreSnapshot,
        IReadOnlyDictionary<string, BMSScore> scoresByHash,
        IReadOnlyDictionary<string, BMSScore> scoresBySha256)
    {
        if (entry == null || scoreSnapshot == null)
        {
            return null;
        }
        if (scoreSnapshot.ActiveScoreSource == ActiveScoreSource.Lr2)
        {
            string hash = FirstNonEmpty(resolvedChart?.Md5, entry.md5);
            if (scoresByHash != null && scoresByHash.TryGetValue(hash, out BMSScore lr2Score))
            {
                return lr2Score;
            }
            return null;
        }
        if (scoreSnapshot.ActiveScoreSource == ActiveScoreSource.Beatoraja && scoresBySha256 != null)
        {
            string sha256 = FirstNonEmpty(resolvedChart?.Sha256, entry.sha256, entryChartInfo?.sha256);
            if (scoresBySha256.TryGetValue(sha256, out BMSScore beatorajaScore))
            {
                string hash = FirstNonEmpty(resolvedChart?.Md5, entry.md5);
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
