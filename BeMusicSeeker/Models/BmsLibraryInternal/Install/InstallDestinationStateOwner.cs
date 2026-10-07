using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the runtime install-destination overlay and its immutable views.
/// </summary>
internal sealed class InstallDestinationStateOwner
{
    private readonly object gate = new();

    private readonly CatalogOwnedCollectionOwner collectionOwner;

    private readonly Dictionary<string, InstallDestinationRuntimeStateEntry> runtimeStatesByKey = new(StringComparer.Ordinal);

    private readonly Func<HashSet<string>> currentOwnedRuntimeStateKeySnapshotProvider;

    private InstallDestinationOverlayChartRefSnapshot overlaySnapshot;

    internal InstallDestinationStateOwner(
        CatalogOwnedCollectionOwner collectionOwner,
        Func<HashSet<string>> currentOwnedRuntimeStateKeySnapshotProvider)
    {
        this.collectionOwner = collectionOwner ?? throw new ArgumentNullException(nameof(collectionOwner));
        this.currentOwnedRuntimeStateKeySnapshotProvider = currentOwnedRuntimeStateKeySnapshotProvider
            ?? throw new ArgumentNullException(nameof(currentOwnedRuntimeStateKeySnapshotProvider));
    }

    internal IReadOnlyList<ChartFile> ReattachFileScanResidualInstallDestinationCharts(
        IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? [])
            .Select(ReattachFileScanResidualInstallDestinationChart)
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
                var state = ChartFileTransientState.FromInstallDestinationState(
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

    /// <summary>
    /// package参照factsとcatalog path移動factsから、overlayへ反映するchart snapshotを生成します。
    /// </summary>
    /// <param name="installDestinationChanges">install destinationの確定変更。</param>
    /// <param name="pathChanges">同じ操作で適用するpath移動。</param>
    /// <returns>重複を除いた変更後chart snapshot。</returns>
    internal List<ChartFile> CreateChangedChartSnapshots(
        IReadOnlyCollection<LibraryInstallDestinationChange> installDestinationChanges,
        IReadOnlyCollection<LibraryChartPathChange> pathChanges)
    {
        var chartsByKey = new Dictionary<string, ChartFile>(StringComparer.Ordinal);
        foreach (ChartFile chart in (installDestinationChanges ?? [])
            .Select(change => change?.CreateAppliedChartSnapshot(pathChanges))
            .Where(chart => chart != null))
        {
            AddChangedChart(chartsByKey, chart);
        }

        foreach (ChartFile chart in CreateMovedRuntimeStateSnapshots(pathChanges))
        {
            AddChangedChart(chartsByKey, chart);
        }

        return [.. chartsByKey.Values];
    }

    private ChartFile ReattachFileScanResidualInstallDestinationChart(ChartFile chart)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return null;
        }

        ChartFile current;
        lock (collectionOwner.Gate)
        {
            current = collectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart));
        }
        return current == null ? null : CreatePackageStateChart(chart, current);
    }

    private static ChartFile CreatePackageStateChart(ChartFile source, ChartFile ownerProjection)
    {
        return ChartFileProjection.WithPackageState(
            ChartFileProjection.WithResources(ownerProjection, source.Resources),
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

    }

    private static IEnumerable<InstallDestinationRuntimeStateKey> EnumerateChartRuntimeStateLookupKeys(ChartFile chart)
    {
        string primaryKey = ChartFileRuntimeStateKey.Create(chart);
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            yield return new InstallDestinationRuntimeStateKey(primaryKey, requireOwnerMatch: false);
        }

        // ハッシュが更新されても同じ所持識別へ投影するため、tokenを照合するパスキーを併用します。
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

        var movedEntry = InstallDestinationRuntimeStateEntry.FromChart(newChart, entry.State);
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
        private readonly ChartFile chartSnapshot;

        private InstallDestinationRuntimeStateEntry(ChartFileTransientState state, ChartFile chartSnapshot)
        {
            State = state ?? ChartFileTransientState.Empty;
            this.chartSnapshot = chartSnapshot;
        }

        internal ChartFileTransientState State { get; }

        internal static InstallDestinationRuntimeStateEntry FromChart(ChartFile chart, ChartFileTransientState state)
        {
            return new InstallDestinationRuntimeStateEntry(state, chart);
        }

        internal bool CanApplyTo(ChartFile chart, bool requireOwnerMatch)
        {
            if (State?.HasState != true || chart == null)
            {
                return false;
            }
            if (chartSnapshot?.Token != null || chart.Token != null)
            {
                return ReferenceEquals(chartSnapshot?.Token, chart.Token);
            }
            return !requireOwnerMatch;
        }

        internal ChartFile CreateChartSnapshot()
        {
            return State?.HasInstallDestinationState == true && chartSnapshot != null
                ? ChartFileProjection.WithTransientState(chartSnapshot, State, includeWarningSnapshot: false)
                : null;
        }
    }
}
