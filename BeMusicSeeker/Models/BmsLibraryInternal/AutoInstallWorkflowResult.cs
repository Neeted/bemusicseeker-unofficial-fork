using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallWorkflowResult
{
    public List<ChartPackage> DiscoveredPackages { get; } = [];

    public List<string> RegroupEligibleSourceDirectories { get; } = [];

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> PendingPackagesToAdd { get; } = [];

    public List<ChartPackage> AutoInstallCandidates { get; } = [];

    public List<string> ExtractedTempDirectories { get; } = [];

    public long DiscoveryMs { get; set; }

    public long ClassificationMs { get; set; }

    public long InstalledCheckMs { get; set; }

    public long WarningClassificationMs { get; set; }

    public long TotalMs { get; set; }
}
