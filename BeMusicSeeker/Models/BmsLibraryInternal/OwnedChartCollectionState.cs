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

    internal int RemoveCharts(IEnumerable<ChartFile> removedCharts)
    {
        List<ChartFile> removedChartList = [.. (removedCharts ?? []).Where(chart => chart != null)];
        if (removedChartList.Count == 0)
        {
            return 0;
        }

        var bmsOwners = new HashSet<BMSFile>(removedChartList
            .Select(chart => chart.GetBmsStorageOwner())
            .Where(file => file != null));
        var bmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(removedChartList
            .Select(chart => chart.GetBmsonStorageOwner())
            .Where(song => song != null));
        var bmsPaths = new HashSet<string>(
            removedChartList
                .Where(chart => chart.Kind == ChartFileKind.Bms)
                .Select(chart => chart.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        var bmsonPaths = new HashSet<string>(
            removedChartList
                .Where(chart => chart.Kind == ChartFileKind.Bmson)
                .Select(chart => chart.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            System.StringComparer.OrdinalIgnoreCase);
        return charts.RemoveAll(chart => IsRemovedChart(chart, bmsOwners, bmsonOwners, bmsPaths, bmsonPaths));
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

    private static bool IsRemovedChart(
        ChartFile chart,
        ISet<BMSFile> bmsOwners,
        ISet<LR2SongDBExtended.bmson_song> bmsonOwners,
        ISet<string> bmsPaths,
        ISet<string> bmsonPaths)
    {
        if (chart == null)
        {
            return false;
        }
        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null && bmsOwners.Contains(bmsOwner))
        {
            return true;
        }
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        if (bmsonOwner != null && bmsonOwners.Contains(bmsonOwner))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(chart.Path))
        {
            return false;
        }
        return chart.Kind switch
        {
            ChartFileKind.Bms => bmsPaths.Contains(chart.Path),
            ChartFileKind.Bmson => bmsonPaths.Contains(chart.Path),
            _ => false
        };
    }
}
