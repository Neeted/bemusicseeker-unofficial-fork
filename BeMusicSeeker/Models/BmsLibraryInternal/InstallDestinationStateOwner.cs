using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the runtime install-destination overlay and its immutable views.
/// </summary>
internal sealed class InstallDestinationStateOwner
{
    private readonly object gate = new();

    private readonly CatalogStorageRowsOwner storageRowsOwner;

    private readonly Dictionary<string, InstallDestinationRuntimeStateEntry> runtimeStatesByKey = new(StringComparer.Ordinal);

    private readonly Func<HashSet<string>> currentOwnedRuntimeStateKeySnapshotProvider;

    private InstallDestinationOverlayChartRefSnapshot overlaySnapshot;

    internal InstallDestinationStateOwner(
        CatalogStorageRowsOwner storageRowsOwner,
        Func<HashSet<string>> currentOwnedRuntimeStateKeySnapshotProvider)
    {
        this.storageRowsOwner = storageRowsOwner ?? throw new ArgumentNullException(nameof(storageRowsOwner));
        this.currentOwnedRuntimeStateKeySnapshotProvider = currentOwnedRuntimeStateKeySnapshotProvider
            ?? throw new ArgumentNullException(nameof(currentOwnedRuntimeStateKeySnapshotProvider));
    }

    internal IReadOnlyList<ChartFile> ReattachFileScanResidualInstallDestinationCharts(
        IEnumerable<ChartFile> charts)
    {
        CatalogStorageRowsSnapshot storageRowsSnapshot = storageRowsOwner.CaptureSnapshot();
        return [.. (charts ?? [])
            .Select(chart => ReattachFileScanResidualInstallDestinationChart(chart, storageRowsSnapshot))
            .Where(chart => chart != null)];
    }

    internal List<ChartFile> OverlayRuntimeStates(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Select(OverlayRuntimeState).Where(chart => chart != null)];
    }

    internal List<ChartFile> CreateCurrentCleanupCharts()
    {
        lock (gate)
        {
            return CreateCurrentCleanupChartsUnsafe();
        }
    }

    internal InstallDestinationCleanupSnapshot CreateCleanupSnapshot()
    {
        return InstallDestinationCleanupSnapshot.FromCharts(CreateCurrentCleanupCharts());
    }

    internal InstallDestinationOverlayChartRefSnapshot CreateOverlaySnapshot(out bool wasCached)
    {
        lock (gate)
        {
            wasCached = overlaySnapshot != null;
            return overlaySnapshot ??= InstallDestinationOverlayChartRefSnapshot
                .FromCharts(CreateCurrentCleanupChartsUnsafe());
        }
    }

    internal void Apply(InstallDestinationRuntimeStateMutation mutation)
    {
        if (mutation == null || !mutation.HasStateChanges)
        {
            return;
        }

        lock (gate)
        {
            foreach (LibraryChartPathChange pathChange in mutation.PathChanges)
            {
                MoveRuntimeState(pathChange);
            }

            foreach (ChartFile chart in mutation.AppliedCharts)
            {
                ChartFileTransientState state = ChartFileTransientState.FromInstallDestinationState(
                    chart,
                    includeWarningSnapshot: true,
                    forceInstallDestinationProjection: true,
                    forceWarningProjection: true);
                foreach (string key in EnumerateRuntimeStateKeys(chart))
                {
                    if (state.HasState)
                    {
                        runtimeStatesByKey[key] = InstallDestinationRuntimeStateEntry.FromChart(chart, state);
                    }
                    else
                    {
                        runtimeStatesByKey.Remove(key);
                    }
                }
            }
            InvalidateOverlaySnapshotUnsafe();
        }
    }

    internal void PruneToCurrentOwnedCharts()
    {
        lock (gate)
        {
            // Startup assigns the full owned chart set before any runtime overlay exists.
            if (runtimeStatesByKey.Count == 0)
            {
                return;
            }
        }

        HashSet<string> currentKeys = currentOwnedRuntimeStateKeySnapshotProvider();

        lock (gate)
        {
            bool removedAny = false;
            foreach (string key in runtimeStatesByKey.Keys.ToList())
            {
                if (!currentKeys.Contains(key))
                {
                    runtimeStatesByKey.Remove(key);
                    removedAny = true;
                }
            }
            if (removedAny)
            {
                InvalidateOverlaySnapshotUnsafe();
            }
        }
    }

    internal List<ChartFile> CreateChangedChartSnapshots(
        LibraryMutationDelta delta,
        IReadOnlyCollection<LibraryChartPathChange> pathChanges)
    {
        var chartsByKey = new Dictionary<string, ChartFile>(StringComparer.Ordinal);
        foreach (ChartFile chart in delta?.CreateAppliedInstallDestinationChartSnapshots() ?? [])
        {
            AddChangedChart(chartsByKey, chart);
        }

        foreach (ChartFile chart in CreateMovedRuntimeStateSnapshots(pathChanges))
        {
            AddChangedChart(chartsByKey, chart);
        }

        return [.. chartsByKey.Values];
    }

    private static ChartFile ReattachFileScanResidualInstallDestinationChart(
        ChartFile chart,
        CatalogStorageRowsSnapshot storageRowsSnapshot)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return null;
        }

        if (chart.Kind == ChartFileKind.Bms)
        {
            BMSFile bmsOwner = FindCurrentBmsOwner(chart, storageRowsSnapshot.BmsRows);
            return bmsOwner == null
                ? null
                : CreatePackageStateChart(
                    chart,
                    ChartFileProjection.FromBmsFile(
                        bmsOwner,
                        includeWarningSnapshot: false,
                        includeResourceReferences: false));
        }

        LR2SongDBExtended.bmson_song bmsonOwner = FindCurrentBmsonOwner(chart, storageRowsSnapshot.BmsonRows);
        return bmsonOwner == null
            ? null
            : CreatePackageStateChart(
                chart,
                ChartFileProjection.FromBmsonSong(
                    bmsonOwner,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
    }

    private static BMSFile FindCurrentBmsOwner(
        ChartFile chart,
        IEnumerable<BMSFile> rows)
    {
        List<BMSFile> candidates = [.. (rows ?? [])
            .Where(file => file != null && AreResidualPathsEqual(file.path, chart.Path))];
        return candidates.FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.hash)
                && !string.IsNullOrWhiteSpace(chart.Md5)
                && string.Equals(file.hash, chart.Md5, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private static LR2SongDBExtended.bmson_song FindCurrentBmsonOwner(
        ChartFile chart,
        IEnumerable<LR2SongDBExtended.bmson_song> rows)
    {
        List<LR2SongDBExtended.bmson_song> candidates = [.. (rows ?? [])
            .Where(song => song != null && AreResidualPathsEqual(song.path, chart.Path))];
        return candidates.FirstOrDefault(song =>
                !string.IsNullOrWhiteSpace(song.md5)
                && !string.IsNullOrWhiteSpace(chart.Md5)
                && string.Equals(song.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private static bool AreResidualPathsEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static ChartFile CreatePackageStateChart(ChartFile source, ChartFile ownerProjection)
    {
        return ChartFileProjection.WithPackageState(
            ownerProjection,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            source.InstallDestinationSuggestions,
            source.Warnings);
    }

    private List<ChartFile> CreateCurrentCleanupChartsUnsafe()
    {
        return [.. runtimeStatesByKey.Values
            .Distinct()
            .Select(entry => entry.CreateChartSnapshot())
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
    }

    private ChartFile OverlayRuntimeState(ChartFile chart)
    {
        lock (gate)
        {
            foreach (InstallDestinationRuntimeStateKey key in EnumerateChartRuntimeStateLookupKeys(chart))
            {
                if (runtimeStatesByKey.TryGetValue(key.Key, out InstallDestinationRuntimeStateEntry entry)
                    && entry.CanApplyTo(chart, key.RequireOwnerMatch))
                {
                    return ChartFileProjection.WithTransientState(chart, entry.State, includeWarningSnapshot: false);
                }
            }
        }
        return chart;
    }

    private static IEnumerable<string> EnumerateRuntimeStateKeys(ChartFile chart)
    {
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (InstallDestinationRuntimeStateKey key in EnumerateChartRuntimeStateLookupKeys(chart))
        {
            if (seenKeys.Add(key.Key))
            {
                yield return key.Key;
            }
        }

        BMSFile bmsOwner = chart?.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            foreach (InstallDestinationRuntimeStateKey ownerKey in EnumerateChartRuntimeStateLookupKeys(ChartFileProjection.FromBmsStorageOwnerIdentity(bmsOwner)))
            {
                if (seenKeys.Add(ownerKey.Key))
                {
                    yield return ownerKey.Key;
                }
            }
            yield break;
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
        if (bmsonOwner != null)
        {
            foreach (InstallDestinationRuntimeStateKey ownerKey in EnumerateChartRuntimeStateLookupKeys(ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonOwner)))
            {
                if (seenKeys.Add(ownerKey.Key))
                {
                    yield return ownerKey.Key;
                }
            }
        }
    }

    private static IEnumerable<InstallDestinationRuntimeStateKey> EnumerateChartRuntimeStateLookupKeys(ChartFile chart)
    {
        string primaryKey = ChartFileRuntimeStateKey.Create(chart);
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            yield return new InstallDestinationRuntimeStateKey(primaryKey, requireOwnerMatch: false);
        }

        // Maintenance can recalculate a BMS hash after a runtime state is published.
        // Keep an owner-guarded path key so the overlay survives that owner refresh
        // without leaking to a different chart later installed at the same path.
        string pathKey = ChartFileRuntimeStateKey.CreatePathKey(chart);
        if (!string.IsNullOrWhiteSpace(pathKey) && !string.Equals(pathKey, primaryKey, StringComparison.Ordinal))
        {
            yield return new InstallDestinationRuntimeStateKey(pathKey, requireOwnerMatch: true);
        }
    }

    private IEnumerable<ChartFile> CreateMovedRuntimeStateSnapshots(IEnumerable<LibraryChartPathChange> pathChanges)
    {
        if (pathChanges == null)
        {
            yield break;
        }

        foreach (LibraryChartPathChange pathChange in pathChanges)
        {
            ChartFile movedChart = CreateMovedRuntimeStateSnapshot(pathChange);
            if (movedChart != null)
            {
                yield return movedChart;
            }
        }
    }

    private ChartFile CreateMovedRuntimeStateSnapshot(LibraryChartPathChange pathChange)
    {
        if (pathChange?.Chart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
        {
            return null;
        }

        string oldPath = string.IsNullOrWhiteSpace(pathChange.OldPath)
            ? pathChange.Chart.Path
            : pathChange.OldPath;
        ChartFile oldChart = ChartFileProjection.WithPath(pathChange.Chart, oldPath);
        List<InstallDestinationRuntimeStateKey> oldKeys = [.. EnumerateChartRuntimeStateLookupKeys(oldChart)];
        if (oldKeys.Count == 0)
        {
            return null;
        }

        InstallDestinationRuntimeStateEntry entry;
        lock (gate)
        {
            entry = oldKeys
                .Select(key => runtimeStatesByKey.TryGetValue(key.Key, out InstallDestinationRuntimeStateEntry value) && value.CanApplyTo(oldChart, key.RequireOwnerMatch) ? value : null)
                .FirstOrDefault(value => value?.State?.HasState == true);
        }
        return entry?.State?.HasState == true
            ? ChartFileProjection.WithTransientState(ChartFileProjection.WithPath(pathChange.Chart, pathChange.NewPath), entry.State, includeWarningSnapshot: false)
            : null;
    }

    private static void AddChangedChart(Dictionary<string, ChartFile> chartsByKey, ChartFile chart)
    {
        string key = ChartFileRuntimeStateKey.Create(chart);
        if (!string.IsNullOrWhiteSpace(key))
        {
            chartsByKey[key] = chart;
        }
    }

    private void MoveRuntimeState(LibraryChartPathChange pathChange)
    {
        if (pathChange?.Chart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
        {
            return;
        }

        string oldPath = string.IsNullOrWhiteSpace(pathChange.OldPath)
            ? pathChange.Chart.Path
            : pathChange.OldPath;
        ChartFile oldChart = ChartFileProjection.WithPath(pathChange.Chart, oldPath);
        ChartFile newChart = ChartFileProjection.WithPath(pathChange.Chart, pathChange.NewPath);
        List<InstallDestinationRuntimeStateKey> oldKeys = [.. EnumerateChartRuntimeStateLookupKeys(oldChart)];
        List<InstallDestinationRuntimeStateKey> newKeys = [.. EnumerateChartRuntimeStateLookupKeys(newChart)];
        InstallDestinationRuntimeStateEntry entry = oldKeys
            .Select(key => runtimeStatesByKey.TryGetValue(key.Key, out InstallDestinationRuntimeStateEntry value) && value.CanApplyTo(oldChart, key.RequireOwnerMatch) ? value : null)
            .FirstOrDefault(value => value?.State?.HasState == true);
        if (entry?.State?.HasState != true)
        {
            return;
        }

        InstallDestinationRuntimeStateEntry movedEntry = InstallDestinationRuntimeStateEntry.FromChart(newChart, entry.State);
        foreach (InstallDestinationRuntimeStateKey newKey in newKeys)
        {
            runtimeStatesByKey[newKey.Key] = movedEntry;
        }
        foreach (InstallDestinationRuntimeStateKey oldKey in oldKeys)
        {
            if (!newKeys.Any(key => string.Equals(key.Key, oldKey.Key, StringComparison.Ordinal)))
            {
                runtimeStatesByKey.Remove(oldKey.Key);
            }
        }
    }

    private void InvalidateOverlaySnapshotUnsafe()
    {
        overlaySnapshot = null;
    }

    private readonly struct InstallDestinationRuntimeStateKey(string key, bool requireOwnerMatch)
    {
        internal string Key { get; } = key;

        internal bool RequireOwnerMatch { get; } = requireOwnerMatch;
    }

    private sealed class InstallDestinationRuntimeStateEntry
    {
        private readonly BMSFile bmsOwner;
        private readonly LR2SongDBExtended.bmson_song bmsonOwner;

        private readonly ChartFile chartSnapshot;

        private InstallDestinationRuntimeStateEntry(
            ChartFileTransientState state,
            BMSFile bmsOwner,
            LR2SongDBExtended.bmson_song bmsonOwner,
            ChartFile chartSnapshot)
        {
            State = state ?? ChartFileTransientState.Empty;
            this.bmsOwner = bmsOwner;
            this.bmsonOwner = bmsonOwner;
            this.chartSnapshot = chartSnapshot;
        }

        internal ChartFileTransientState State { get; }

        internal static InstallDestinationRuntimeStateEntry FromChart(ChartFile chart, ChartFileTransientState state)
        {
            return new InstallDestinationRuntimeStateEntry(
                state,
                chart?.GetBmsStorageOwner(),
                chart?.GetBmsonStorageOwner(),
                ChartFileProjection.ToImmutableSnapshot(chart));
        }

        internal bool CanApplyTo(ChartFile chart, bool requireOwnerMatch)
        {
            return CanApplyTo(chart?.GetBmsStorageOwner(), chart?.GetBmsonStorageOwner(), requireOwnerMatch);
        }

        private bool CanApplyTo(
            BMSFile currentBmsOwner,
            LR2SongDBExtended.bmson_song currentBmsonOwner,
            bool requireOwnerMatch)
        {
            if (State?.HasState != true)
            {
                return false;
            }
            if (!requireOwnerMatch)
            {
                return true;
            }

            if (bmsOwner != null || currentBmsOwner != null)
            {
                return ReferenceEquals(bmsOwner, currentBmsOwner);
            }

            // Scan residual events carry immutable snapshots only. The facade
            // reattaches the current storage owner before applying them when a
            // row is available; an ownerless snapshot must never satisfy the
            // guarded path fallback because that would leak state to another
            // chart later installed at the same path.
            if (bmsonOwner == null && currentBmsonOwner == null)
            {
                return false;
            }

            return (bmsonOwner != null || currentBmsonOwner != null)
                && ReferenceEquals(bmsonOwner, currentBmsonOwner);
        }

        internal ChartFile CreateChartSnapshot()
        {
            if (State?.HasInstallDestinationState != true)
            {
                return null;
            }

            ChartFile source = bmsOwner != null
                ? ChartFileProjection.FromBmsStorageOwnerIdentity(bmsOwner)
                : bmsonOwner != null
                    ? ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonOwner)
                    : chartSnapshot;
            if (source == null)
            {
                return null;
            }
            return ChartFileProjection.WithTransientState(source, State, includeWarningSnapshot: false);
        }
    }
}
