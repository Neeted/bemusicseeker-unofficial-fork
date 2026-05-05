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

    public long MaintenanceApplyMs { get; set; }

    public int AppliedBmsCount { get; set; }

    public int AppliedBmsonCount { get; set; }

    public int DefaultBmsCount { get; set; }

    public int DefaultBmsonCount { get; set; }

    public long TotalMs { get; set; }
}
