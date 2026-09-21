using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

internal static class ChartFileIdentity
{
    internal static bool IsSameChartTarget(ChartFile chart, ChartFile targetChart)
    {
        if (chart == null || targetChart == null || chart.Kind != targetChart.Kind)
        {
            return false;
        }
        if (ReferenceEquals(chart, targetChart))
        {
            return true;
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        BMSFile targetBmsFile = targetChart.GetBmsStorageOwner();
        if (bmsFile != null
            && targetBmsFile != null
            && ReferenceEquals(bmsFile, targetBmsFile))
        {
            return true;
        }
        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        LR2SongDBExtended.bmson_song targetBmsonSong = targetChart.GetBmsonStorageOwner();
        if (bmsonSong != null
            && targetBmsonSong != null
            && ReferenceEquals(bmsonSong, targetBmsonSong))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(chart.Path)
            && !string.IsNullOrWhiteSpace(targetChart.Path)
            && chart.Path.Equals(targetChart.Path, StringComparison.OrdinalIgnoreCase);
    }
}
