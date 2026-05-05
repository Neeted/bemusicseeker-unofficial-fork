using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceTableHydrationResult
{
    public List<string> Pragmas { get; } = new List<string>();

    public Dictionary<string, BMSFileMaintenanceInfo> MaintenanceMap { get; } = new Dictionary<string, BMSFileMaintenanceInfo>(System.StringComparer.OrdinalIgnoreCase);

    public long MaintenanceTableCount { get; set; }

    public long MaintenanceTableLoadMs { get; set; }

    public long MaintenanceCountMs { get; set; }

    public long MaintenanceMaterializeMs { get; set; }

    public long MaintenanceMapBuildMs { get; set; }

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }

    public long MaintenanceApplyMs { get; set; }

    public long MaintenanceAttachMs { get; set; }

    public long ResourceHealthIndexMs { get; set; }

    public long CleanupMs { get; set; }

    public int AppliedBmsCount { get; set; }

    public int AppliedBmsonCount { get; set; }

    public int DefaultBmsCount { get; set; }

    public int DefaultBmsonCount { get; set; }

    public int ValidSnapshotCount { get; set; }

    public int PlaceholderCount { get; set; }

    public bool ViewRefreshQueued { get; set; }

    public int OwnerPathCount { get; set; }

    public int StalePathCount { get; set; }

    public int CleanupDeletedCount { get; set; }

    public List<string> StaleMaintenancePaths { get; } = new List<string>();

    public long TotalMs { get; set; }
}
