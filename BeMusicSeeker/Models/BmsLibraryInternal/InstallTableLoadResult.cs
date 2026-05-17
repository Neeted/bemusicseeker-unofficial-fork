using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallTableLoadResult
{
    public List<ChartPackage> PendingPackages { get; } = [];

    public List<ChartPackage> StalePackages { get; } = [];

    public List<string> StaleInstallPaths { get; } = [];

    public List<BMSFile> PendingWarningInitTargets { get; } = [];

    public int InstalledWarningCount { get; set; }

    public int SingleFileWarningCount { get; set; }

    public int StrictWarningCount { get; set; }

    public long LoadMs { get; set; }

    public long WarningInitMs { get; set; }

    public long TotalMs { get; set; }
}
