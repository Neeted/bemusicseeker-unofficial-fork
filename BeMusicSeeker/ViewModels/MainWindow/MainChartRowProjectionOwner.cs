using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns live projection state and provider wiring shared by every workflow presented in the main chart table.
/// </summary>
internal sealed class MainChartRowProjectionOwner
{
    private readonly Dictionary<string, ChartFileTransientState> transientStatesByKey = new(StringComparer.OrdinalIgnoreCase);
    private int chartInfoVersion;
    private int scoreSnapshotVersion;

    internal int ChartInfoVersion => Volatile.Read(ref chartInfoVersion);

    internal int ScoreSnapshotVersion => Volatile.Read(ref scoreSnapshotVersion);

    internal void CaptureVersions(BMSLibrary library)
    {
        Volatile.Write(ref chartInfoVersion, library?.ChartInfoIndexVersion ?? 0);
        Volatile.Write(ref scoreSnapshotVersion, library?.ScoreSnapshotVersion ?? 0);
    }

    internal void ConfigureLibraryRow(BMSLibrary library, LibraryChartRow row)
    {
        if (row == null)
        {
            return;
        }
        row.SetChartTransientStateProvider(GetTransientState);
        row.SetChartInfoProjectionProvider(chart => ResolveChartInfo(library, chart));
        row.SetResourceHealthProjectionProvider(candidate => ResolveResourceHealth(library, candidate));
        row.SetPlaylistReferenceDisplayProvider(candidate => ResolvePlaylistReferenceDisplay(library, candidate?.Chart));
    }

    internal void ConfigureLibraryRows(BMSLibrary library, IEnumerable<LibraryChartRow> rows)
    {
        foreach (LibraryChartRow row in rows ?? [])
        {
            ConfigureLibraryRow(library, row);
        }
    }

    internal LibraryChartRow CreateLibraryRow(BMSLibrary library, ChartFile chart)
    {
        LibraryChartRow row = LibraryChartRow.FromChartFile(chart);
        ConfigureLibraryRow(library, row);
        return row;
    }

    internal LibraryChartRow CreatePackageRow(BMSLibrary library, PackageChartEntry entry)
    {
        LibraryChartRow row = LibraryChartRow.FromPackageChartEntry(entry);
        ConfigureLibraryRow(library, row);
        return row;
    }

    internal LibraryChartRow CreateSubsetRow(
        BMSLibrary library,
        ChartListSourceRow sourceRow,
        bool includeResourceHealth)
    {
        if (sourceRow == null)
        {
            return null;
        }
        LibraryChartRow row = sourceRow.PackageEntry != null
            ? LibraryChartRow.FromPackageChartEntry(sourceRow.PackageEntry)
            : LibraryChartRow.FromChartFile(
                sourceRow.Chart,
                sourceRow.HideResourceHealthDigestWhenInstallDestinationSet);
        if (includeResourceHealth)
        {
            row?.SetResourceHealthProjectionProvider(candidate => ResolveResourceHealth(library, candidate));
        }
        row?.SetChartInfoProjectionProvider(chart => ResolveChartInfo(library, chart));
        row?.SetPlaylistReferenceDisplayProvider(candidate => ResolvePlaylistReferenceDisplay(library, candidate?.Chart));
        return row;
    }

    internal List<ChartListSourceRow> BuildPackageSourceRows(
        BMSLibrary library,
        IEnumerable<PackageChartEntry> entries,
        bool includeResourceHealth)
    {
        return ChartListSourceRow.BuildPackageRows(
            entries,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => ResolvePlaylistReferenceDisplay(library, row),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => ScoreSnapshotVersion);
    }

    internal List<ChartListSourceRow> BuildStandardSourceRows(
        BMSLibrary library,
        IEnumerable<ChartFile> charts,
        ChartListSourceProjectionMode projectionMode,
        bool includeResourceHealth)
    {
        return ChartListSourceRow.BuildStandardLibraryRows(
            charts,
            projectionMode,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => ResolvePlaylistReferenceDisplay(library, row),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => ScoreSnapshotVersion,
            row => ResolveScoreSnapshot(library, row));
    }

    internal List<ChartListSourceRow> BuildNormalSourceRows(
        BMSLibrary library,
        OwnedChartStorageOwnerView source,
        bool includeBmsonRows)
    {
        int expectedCount = source == null
            ? 0
            : source.BmsFiles.Count + (includeBmsonRows ? source.BmsonSongs.Count : 0);
        var rows = expectedCount > 0 ? new List<ChartListSourceRow>(expectedCount) : [];
        foreach (BMSFile file in source?.BmsFiles ?? [])
        {
            if (file == null)
            {
                continue;
            }
            ChartListSourceRow row = ChartListSourceRow.FromBmsStorageOwner(
                file,
                candidate => ResolveResourceHealth(library, candidate),
                candidate => ResolvePlaylistReferenceDisplay(library, candidate),
                GetTransientState,
                chart => ResolveChartInfo(library, chart),
                candidate => ResolveChartInfo(library, candidate),
                () => ChartInfoVersion,
                () => ScoreSnapshotVersion,
                candidate => ResolveScoreSnapshot(library, candidate));
            if (row != null)
            {
                rows.Add(row);
            }
        }
        if (!includeBmsonRows)
        {
            return rows;
        }
        foreach (LR2SongDBExtended.bmson_song song in source?.BmsonSongs ?? [])
        {
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            ChartListSourceRow row = ChartListSourceRow.FromBmsonStorageOwner(
                song,
                candidate => ResolveResourceHealth(library, candidate),
                candidate => ResolvePlaylistReferenceDisplay(library, candidate),
                GetTransientState,
                chart => ResolveChartInfo(library, chart),
                candidate => ResolveChartInfo(library, candidate),
                () => ChartInfoVersion,
                () => ScoreSnapshotVersion,
                candidate => ResolveScoreSnapshot(library, candidate));
            if (row != null)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    internal PlaylistDetailSourceRow CreatePlaylistDetailSourceRow(
        BMSLibrary library,
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore scoreSnapshot,
        LR2SongDBExtended.chart_info entryChartInfo,
        LibraryChartRef resolvedChartRef)
    {
        return new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            scoreSnapshot,
            entryChartInfo,
            chart => ResolvePlaylistReferenceDisplay(library, chart),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            resolvedChartRef);
    }

    internal ChartFileTransientState GetTransientState(ChartFile chart, bool includeWarningSnapshot)
    {
        string key = ChartFileRuntimeStateKey.Create(chart);
        if (string.IsNullOrWhiteSpace(key)
            || !transientStatesByKey.TryGetValue(key, out ChartFileTransientState state)
            || state == null)
        {
            return ChartFileTransientState.Empty;
        }
        return includeWarningSnapshot ? state : state.WithoutWarnings();
    }

    internal void UpdateTransientStates(IEnumerable<ChartFile> charts, bool forceInstallDestinationProjection = false)
    {
        foreach (ChartFile chart in charts ?? [])
        {
            string key = ChartFileRuntimeStateKey.Create(chart);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            ChartFileTransientState state = ChartFileTransientState.FromInstallDestinationState(
                chart,
                includeWarningSnapshot: true,
                forceInstallDestinationProjection: forceInstallDestinationProjection,
                forceWarningProjection: forceInstallDestinationProjection);
            if (state.HasState)
            {
                transientStatesByKey[key] = state;
            }
            else
            {
                transientStatesByKey.Remove(key);
            }
        }
    }

    internal void PruneTransientStates(IEnumerable<ChartFile> currentCharts)
    {
        var currentKeys = new HashSet<string>(
            (currentCharts ?? [])
                .Select(ChartFileRuntimeStateKey.Create)
                .Where(key => !string.IsNullOrWhiteSpace(key)),
            StringComparer.OrdinalIgnoreCase);
        PruneTransientStates(currentKeys);
    }

    internal void PruneTransientStatesToOwnedCharts(BMSLibrary library)
    {
        if (transientStatesByKey.Count == 0)
        {
            return;
        }
        PruneTransientStates(
            library?.CreateOwnedChartRuntimeStatePrimaryKeySnapshot()
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    internal void ClearTransientStates()
    {
        transientStatesByKey.Clear();
    }

    internal ResourceHealthWarningProjection ResolveResourceHealth(BMSLibrary library, ChartListSourceRow row)
    {
        return library == null || row == null
            ? ResourceHealthWarningProjection.Empty
            : library.TryGetCurrentResourceHealthWarningProjection(row.Kind, row.Path, row.Hash);
    }

    internal ChartScoreSnapshot ResolveScoreSnapshot(BMSLibrary library, ChartListSourceRow row)
    {
        return library == null || row == null
            ? ChartScoreSnapshot.MissingChart
            : library.ResolveChartScoreSnapshot(row.Kind, row.Path, row.Hash, row.Sha256);
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(BMSLibrary library, ChartFile chart)
    {
        return chart == null ? null : library?.ResolveChartInfo(chart.Sha256, chart.Md5);
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(BMSLibrary library, ChartListSourceRow row)
    {
        return row == null ? null : library?.ResolveChartInfo(row.Sha256, row.Hash);
    }

    internal PlaylistReferenceDisplay ResolvePlaylistReferenceDisplay(BMSLibrary library, ChartFile chart)
    {
        return library?.GetPlaylistReferenceDisplay(chart) ?? PlaylistReferenceDisplay.Empty;
    }

    internal PlaylistReferenceDisplay ResolvePlaylistReferenceDisplay(BMSLibrary library, ChartListSourceRow row)
    {
        return row == null
            ? PlaylistReferenceDisplay.Empty
            : library?.GetPlaylistReferenceDisplay(row.Hash, row.Sha256) ?? PlaylistReferenceDisplay.Empty;
    }

    private ResourceHealthWarningProjection ResolveResourceHealth(BMSLibrary library, LibraryChartRow row)
    {
        ChartFile chart = row?.Chart;
        return library == null || chart == null
            ? ResourceHealthWarningProjection.Empty
            : library.TryGetCurrentResourceHealthWarningProjection(chart);
    }

    private void PruneTransientStates(ISet<string> currentKeys)
    {
        foreach (string key in transientStatesByKey.Keys.ToList())
        {
            if (currentKeys?.Contains(key) != true)
            {
                transientStatesByKey.Remove(key);
            }
        }
    }
}
