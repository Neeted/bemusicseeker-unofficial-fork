using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryPlaylistReferenceService(int playlistReferenceApplyChunkSize)
{
    private readonly int playlistReferenceApplyChunkSize = playlistReferenceApplyChunkSize;

    public int ApplyReferenceMap(IEnumerable<PlaylistReferenceChartSnapshot> charts, PlaylistReferenceLookupKeys lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats applyStats)
    {
        matchedCharts = 0;
        applyStats = default;
        if (charts == null || lookupKeys == null || !lookupKeys.HasAny)
        {
            return 0;
        }
        int processed = 0;
        var chunkStopwatch = Stopwatch.StartNew();
        foreach (PlaylistReferenceChartSnapshot chart in charts)
        {
            if (HasReference(chart, lookupKeys))
            {
                matchedCharts++;
            }
            processed++;
            if (processed % playlistReferenceApplyChunkSize == 0)
            {
                chunkStopwatch.Stop();
                applyStats.Chunks++;
                if (chunkStopwatch.ElapsedMilliseconds > applyStats.MaxChunkMs)
                {
                    applyStats.MaxChunkMs = chunkStopwatch.ElapsedMilliseconds;
                }
                Thread.Sleep(0);
                applyStats.YieldCount++;
                chunkStopwatch.Restart();
            }
        }
        chunkStopwatch.Stop();
        if (processed % playlistReferenceApplyChunkSize != 0 || processed == 0)
        {
            applyStats.Chunks++;
            if (chunkStopwatch.ElapsedMilliseconds > applyStats.MaxChunkMs)
            {
                applyStats.MaxChunkMs = chunkStopwatch.ElapsedMilliseconds;
            }
        }
        return matchedCharts;
    }

    private static bool HasReference(PlaylistReferenceChartSnapshot chart, PlaylistReferenceLookupKeys lookupKeys)
    {
        if (chart == null || lookupKeys == null)
        {
            return false;
        }

        bool matched = lookupKeys.ContainsMd5(chart.Md5);
        if (!matched)
        {
            matched = lookupKeys.ContainsSha256(chart.Sha256);
        }
        return matched;
    }
}
