using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncFileDiffFreshnessSnapshot(
    string reason,
    int scanSurfaceGeneration,
    int ownedCollectionVersion,
    int bmsRowsVersion,
    int bmsonRowsVersion,
    int bmsOwnerCount,
    int bmsonOwnerCount,
    int bmsTargetCount,
    int bmsDeletedCount,
    int bmsDateOnlyUpdateCount,
    int bmsTextOnlyUpdateCount,
    int bmsMovedHashRelinkCount,
    int bmsMovedHashRelinkAmbiguousCount,
    int inlineChartInfoTargetCount,
    int inlineChartInfoSuccessCount,
    int inlineChartInfoCurrentSkippedCount,
    int inlineChartInfoParseFailedCount,
    int inlineMaintenanceTargetCount,
    int inlineMaintenanceBmsCount,
    int inlineMaintenanceBmsonCount,
    int inlineMaintenanceFailedCount,
    IEnumerable<string> transientSongRowSkipPaths)
{
    public string Reason { get; } = reason ?? "unknown";

    public int ScanSurfaceGeneration { get; } = scanSurfaceGeneration;

    public int OwnedCollectionVersion { get; } = ownedCollectionVersion;

    public int BmsRowsVersion { get; } = bmsRowsVersion;

    public int BmsonRowsVersion { get; } = bmsonRowsVersion;

    public int BmsOwnerCount { get; } = bmsOwnerCount;

    public int BmsonOwnerCount { get; } = bmsonOwnerCount;

    public int BmsTargetCount { get; } = bmsTargetCount;

    public int BmsDeletedCount { get; } = bmsDeletedCount;

    public int BmsDateOnlyUpdateCount { get; } = bmsDateOnlyUpdateCount;

    public int BmsTextOnlyUpdateCount { get; } = bmsTextOnlyUpdateCount;

    public int BmsMovedHashRelinkCount { get; } = bmsMovedHashRelinkCount;

    public int BmsMovedHashRelinkAmbiguousCount { get; } = bmsMovedHashRelinkAmbiguousCount;

    public int InlineChartInfoTargetCount { get; } = inlineChartInfoTargetCount;

    public int InlineChartInfoSuccessCount { get; } = inlineChartInfoSuccessCount;

    public int InlineChartInfoCurrentSkippedCount { get; } = inlineChartInfoCurrentSkippedCount;

    public int InlineChartInfoParseFailedCount { get; } = inlineChartInfoParseFailedCount;

    public int InlineMaintenanceTargetCount { get; } = inlineMaintenanceTargetCount;

    public int InlineMaintenanceBmsCount { get; } = inlineMaintenanceBmsCount;

    public int InlineMaintenanceBmsonCount { get; } = inlineMaintenanceBmsonCount;

    public int InlineMaintenanceFailedCount { get; } = inlineMaintenanceFailedCount;

    public HashSet<string> TransientSongRowSkipPaths { get; } =
        new HashSet<string>(
            (transientSongRowSkipPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
}
