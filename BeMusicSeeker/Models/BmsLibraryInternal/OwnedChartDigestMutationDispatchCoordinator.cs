using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedChartDigestMutationDispatchCoordinator
{
    private readonly IOwnedChartDigestMutationDispatchHost host;

    internal OwnedChartDigestMutationDispatchCoordinator(IOwnedChartDigestMutationDispatchHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    internal void DispatchDigestChanges(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        string reason,
        bool resourceHealthIndexInvalidated = true)
    {
        host.DispatchOwnedChartDigestMutation(
            BuildDigestMutationPlan(digestChanges, resourceHealthIndexInvalidated),
            reason);
    }

    internal void DispatchPotentialDigestChanges(
        IEnumerable<ChartFile> charts,
        string reason,
        bool resourceHealthIndexInvalidated = true)
    {
        host.DispatchOwnedChartDigestMutation(
            BuildPotentialDigestMutationPlan(charts, resourceHealthIndexInvalidated),
            reason);
    }

    internal static OwnedChartDigestMutationPlan BuildDigestMutationPlan(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        bool resourceHealthIndexInvalidated = true)
    {
        List<LibraryChartDigestChange> changes = [.. (digestChanges ?? []).Where(change => change?.HasDigestChange == true)];
        bool anyChanges = changes.Count > 0;
        bool primaryHashChanged = changes.Any(change => change.PrimaryHashChanged);
        bool md5Changed = changes.Any(change => change.Md5Changed);
        return new OwnedChartDigestMutationPlan(
            changes,
            installEstimationMetadataProfileCacheInvalidated: anyChanges,
            duplicateCacheInvalidated: primaryHashChanged,
            playlistSummaryOwnedHashInvalidated: anyChanges,
            ownedCollectionChanged: anyChanges,
            resourceHealthIndexInvalidated: resourceHealthIndexInvalidated && md5Changed,
            warningPresentationChanged: primaryHashChanged || (resourceHealthIndexInvalidated && md5Changed),
            bmsFilesStorageRowsChanged: changes.Any(change => change.Kind == LibraryChartKind.Bms),
            bmsonSongsStorageRowsChanged: changes.Any(change => change.Kind == LibraryChartKind.Bmson));
    }

    internal static OwnedChartDigestMutationPlan BuildPotentialDigestMutationPlan(
        IEnumerable<ChartFile> charts,
        bool resourceHealthIndexInvalidated = true)
    {
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart != null)];
        if (targetCharts.Count == 0)
        {
            return OwnedChartDigestMutationPlan.Empty;
        }

        return new OwnedChartDigestMutationPlan(
            [],
            requiresInstalledLookupFullInvalidate: true,
            installEstimationMetadataProfileCacheInvalidated: true,
            duplicateCacheInvalidated: true,
            playlistSummaryOwnedHashInvalidated: true,
            ownedCollectionChanged: true,
            resourceHealthIndexInvalidated: resourceHealthIndexInvalidated,
            warningPresentationChanged: resourceHealthIndexInvalidated,
            bmsFilesStorageRowsChanged: targetCharts.Any(chart => chart.Kind == ChartFileKind.Bms),
            bmsonSongsStorageRowsChanged: targetCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
    }
}
