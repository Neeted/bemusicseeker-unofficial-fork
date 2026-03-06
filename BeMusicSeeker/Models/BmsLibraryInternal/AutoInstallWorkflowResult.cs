using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallWorkflowResult
{
    public List<BMSPackage> DiscoveredPackages { get; } = new List<BMSPackage>();

    public List<BMSPackage> PendingPackagesToRemove { get; } = new List<BMSPackage>();

    public List<BMSPackage> PendingPackagesToAdd { get; } = new List<BMSPackage>();

    public List<BMSPackage> AutoInstallCandidates { get; } = new List<BMSPackage>();

    public List<string> ExtractedTempDirectories { get; } = new List<string>();

    public long DiscoveryMs { get; set; }

    public long ClassificationMs { get; set; }

    public long TotalMs { get; set; }
}
