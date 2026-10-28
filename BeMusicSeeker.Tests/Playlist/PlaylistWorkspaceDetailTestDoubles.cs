using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal sealed class FakePlaylistDetailDataSource : IPlaylistDetailDataSource
{
    internal int EnsureEntriesLoadedCallCount { get; private set; }

    internal int ResolveIndexCallCount { get; private set; }

    internal PlaylistLibraryResolveIndexSnapshot ResolveIndexSnapshot { get; set; } = PlaylistLibraryResolveIndexSnapshot.Empty;

    internal BMSLibrary.PlaylistLibraryResolveIndexRuntimeState RuntimeState { get; set; } = new();

    internal BeMusicSeeker.Models.ChartDetails ChartInfo { get; set; } = null!;

    internal BMSLibrary.ScoreSnapshot ScoreSnapshot { get; set; } = null!;

    internal Action? EnsureEntriesLoadedAction { get; set; }

    internal Action? ResolveIndexAction { get; set; }

    internal CancellationToken LastResolveIndexCancellationToken { get; private set; }

    internal int CurrentChartInfoIndexVersion { get; set; } = 1;
    public int ChartInfoIndexVersion => CurrentChartInfoIndexVersion;

    public int ScoreSnapshotVersion => 1;

    /// <summary>他の入力版から導出せず、テストが独立して指定する現在確定参照版です。</summary>
    public long ReferenceRevision { get; set; }

    internal long? CurrentCatalogRevision { get; set; }

    public long CatalogRevision => CurrentCatalogRevision ?? ResolveIndexSnapshot.CatalogRevision;

    public void EnsureEntriesLoaded(BMSTable table, string reason)
    {
        EnsureEntriesLoadedCallCount++;
        EnsureEntriesLoadedAction?.Invoke();
    }

    internal CatalogReadSnapshot CatalogSnapshot { get; set; } = new(0, new Dictionary<OwnedChartToken, ChartFile>());

    public CatalogReadSnapshot CapturePlaylistReadSnapshot(IEnumerable<PlaylistEntryLookupKey> keys, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<PlaylistEntryLookupKey, LibraryChartRef>(CatalogReadSnapshot.LookupKeyComparer);
        foreach (PlaylistEntryLookupKey key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved[key] = ResolveIndexSnapshot.ResolveChartForPlaylistLookupKey(key);
        }
        return new CatalogReadSnapshot((int)CatalogRevision, new Dictionary<OwnedChartToken, ChartFile>(CatalogSnapshot.Charts), resolved);
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
        LastResolveIndexCancellationToken = cancellationToken;
        ResolveIndexAction?.Invoke();
        cacheHit = true;
        staleRetryCount = 0;
        return ResolveIndexSnapshot;
    }

    public BMSLibrary.PlaylistLibraryResolveIndexRuntimeState GetResolveIndexRuntimeState()
    {
        return RuntimeState;
    }

    public BeMusicSeeker.Models.ChartDetails ResolveChartInfo(string sha256, string md5)
    {
        return ChartInfo;
    }

    public PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        BeMusicSeeker.Models.ChartDetails chartInfo,
        LibraryChartRef resolvedChartRef,
        PlaylistEntryValue? entryValue = null,
        long entryOrder = 0,
        ChartListReferenceInput? referenceInput = null)
    {
        return new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            scoreSnapshot: score,
            entryChartInfo: chartInfo,
            resolvedChartRef: resolvedChartRef,
            entryValue: entryValue,
            entryOrder: entryOrder);
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
