using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthIndexMutation
{
    internal List<ChartFile> UpdatedTargets { get; } = [];

    internal List<ChartFile> RemovedTargets { get; } = [];

    internal ResourceMaintenanceTargetSet FullOwnedTargetSet { get; set; }

    internal int? DeltaBaseResourceHealthInputVersion { get; set; }

    internal int? DeltaTargetResourceHealthInputVersion { get; set; }

    internal bool Invalidate { get; set; }

    internal bool RebuildFull { get; set; }

    internal bool Defer { get; set; }

    internal bool InvalidateIfDeltaFails { get; set; }

    internal int UpdateTargetCount => UpdatedTargets.Count + RemovedTargets.Count;

    internal bool HasDeltaTargets => UpdatedTargets.Count > 0 || RemovedTargets.Count > 0;

    internal bool HasChanges => Invalidate || RebuildFull || Defer || HasDeltaTargets;
}
