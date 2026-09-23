using System;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal sealed class FakePlaylistDetailDataSource : IPlaylistDetailDataSource
{
    internal int EnsureEntriesLoadedCallCount { get; private set; }

    internal int ResolveIndexCallCount { get; private set; }

    internal PlaylistLibraryResolveIndexSnapshot ResolveIndexSnapshot { get; set; } = PlaylistLibraryResolveIndexSnapshot.Empty;

    internal BMSLibrary.PlaylistLibraryResolveIndexRuntimeState RuntimeState { get; set; } = new();

    internal LR2SongDBExtended.chart_info ChartInfo { get; set; } = null!;

    internal BMSLibrary.ScoreSnapshot ScoreSnapshot { get; set; } = null!;

    internal Action? EnsureEntriesLoadedAction { get; set; }

    public int ChartInfoIndexVersion => 1;

    public int ScoreSnapshotVersion => 1;

    public long OwnedChartCollectionVersion => 1;

    public void EnsureEntriesLoaded(BMSTable table, string reason)
    {
        EnsureEntriesLoadedCallCount++;
        EnsureEntriesLoadedAction?.Invoke();
    }

    public BMSLibrary.ScoreSnapshot GetScoreSnapshot()
    {
        return ScoreSnapshot;
    }

    public PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        ResolveIndexCallCount++;
        cacheHit = true;
        staleRetryCount = 0;
        return ResolveIndexSnapshot;
    }

    public BMSLibrary.PlaylistLibraryResolveIndexRuntimeState GetResolveIndexRuntimeState()
    {
        return RuntimeState;
    }

    public LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        return ChartInfo;
    }

    public PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        LR2SongDBExtended.chart_info chartInfo,
        LibraryChartRef resolvedChartRef)
    {
        return new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            scoreSnapshot: score,
            entryChartInfo: chartInfo,
            resolvedChartRef: resolvedChartRef);
    }
}

internal sealed class TestablePlaylistEntry : BMSTableEntry
{
    internal TestablePlaylistEntry(string md5Value, string titleValue)
    {
        md5 = md5Value;
        title = titleValue;
    }

    internal void SetSha256(string value)
    {
        sha256 = value;
    }
}
