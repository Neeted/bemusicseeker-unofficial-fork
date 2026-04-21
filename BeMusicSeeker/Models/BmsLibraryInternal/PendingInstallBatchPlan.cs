using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchPlan
{
    public List<BMSPackage> SelectedPendingPackages { get; } = new List<BMSPackage>();

    public List<PendingInstallBatchGroup> Groups { get; } = new List<PendingInstallBatchGroup>();

    public List<BMSPackage> CleanupOnlyCandidates { get; } = new List<BMSPackage>();

    public HashSet<string> MoveGuardHashes { get; set; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    public int InstallTargetFileCount { get; set; }

    public long FilterMs { get; set; }

    public long GroupBuildMs { get; set; }

    public long PlanBuildMs { get; set; }

    public int SelectedPendingCount { get; set; }

    public int GroupedPackageCount { get; set; }

    public int CleanupOnlyCandidateCount { get; set; }

    public int DeferredManualHoldCount { get; set; }
}
