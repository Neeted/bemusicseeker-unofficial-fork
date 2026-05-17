using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallWorkflowResult
{
    public List<ChartPackage> DiscoveredPackages { get; } = new List<ChartPackage>();

    public List<string> RegroupEligibleSourceDirectories { get; } = new List<string>();

    public List<ChartPackage> PendingPackagesToRemove { get; } = new List<ChartPackage>();

    public List<ChartPackage> PendingPackagesToAdd { get; } = new List<ChartPackage>();

    public List<ChartPackage> AutoInstallCandidates { get; } = new List<ChartPackage>();

    public List<string> ExtractedTempDirectories { get; } = new List<string>();

    public long DiscoveryMs { get; set; }

    public long ClassificationMs { get; set; }

    public long InstalledCheckMs { get; set; }

    public long WarningClassificationMs { get; set; }

    public long TotalMs { get; set; }
}
