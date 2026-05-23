using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedChartCollectionState
{
    private readonly List<ChartFile> charts;

    internal OwnedChartCollectionState()
        : this(new List<ChartFile>())
    {
    }

    private OwnedChartCollectionState(List<ChartFile> charts)
    {
        this.charts = charts ?? [];
    }

    internal static OwnedChartCollectionState FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<ChartFile> charts = ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFiles);
        charts.AddRange(ChartFileProjection.FromBmsonStorageOwnerIdentities(bmsonSongs));
        return new OwnedChartCollectionState(charts);
    }

    internal List<ChartFile> CreateSnapshot(
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        return [.. charts
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: includeWarningSnapshot,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: includeScoreSnapshot))
            .Where(chart => chart != null)];
    }

    internal bool MatchesStorageRows(
        IReadOnlyList<BMSFile> bmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        int chartIndex = 0;
        if (bmsFiles != null)
        {
            for (int i = 0; i < bmsFiles.Count; i++)
            {
                BMSFile bmsFile = bmsFiles[i];
                if (bmsFile == null)
                {
                    continue;
                }
                if (chartIndex >= charts.Count
                    || charts[chartIndex]?.Kind != ChartFileKind.Bms
                    || !ReferenceEquals(charts[chartIndex].GetBmsStorageOwner(), bmsFile))
                {
                    return false;
                }
                chartIndex++;
            }
        }
        if (bmsonSongs != null)
        {
            for (int i = 0; i < bmsonSongs.Count; i++)
            {
                LR2SongDBExtended.bmson_song bmsonSong = bmsonSongs[i];
                if (bmsonSong == null)
                {
                    continue;
                }
                if (chartIndex >= charts.Count
                    || charts[chartIndex]?.Kind != ChartFileKind.Bmson
                    || !ReferenceEquals(charts[chartIndex].GetBmsonStorageOwner(), bmsonSong))
                {
                    return false;
                }
                chartIndex++;
            }
        }
        return chartIndex == charts.Count;
    }
}
