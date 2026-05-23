using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchPlan
{
    public List<ChartPackage> SelectedPendingPackages { get; } = [];

    public List<PendingInstallBatchGroup> Groups { get; } = [];

    public List<ChartPackage> CleanupOnlyCandidates { get; } = [];

    public IMutablePrimaryHashLookup MoveGuardLookup { get; set; } = new PrimaryHashSetLookup();

    public int InstallTargetFileCount { get; set; }

    public long FilterMs { get; set; }

    public long GroupBuildMs { get; set; }

    public long PlanBuildMs { get; set; }

    public int SelectedPendingCount { get; set; }

    public int GroupedPackageCount { get; set; }

    public int CleanupOnlyCandidateCount { get; set; }

    public int DeferredManualHoldCount { get; set; }
}
