using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallTableLoadResult
{
    public List<ChartPackage> PendingPackages { get; } = new List<ChartPackage>();

    public List<ChartPackage> StalePackages { get; } = new List<ChartPackage>();

    public List<string> StaleInstallPaths { get; } = new List<string>();

    public List<BMSFile> PendingWarningInitTargets { get; } = new List<BMSFile>();

    public int InstalledWarningCount { get; set; }

    public int SingleFileWarningCount { get; set; }

    public int StrictWarningCount { get; set; }

    public long LoadMs { get; set; }

    public long WarningInitMs { get; set; }

    public long TotalMs { get; set; }
}
