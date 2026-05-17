using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallApplyResult
{
    public List<ChartPackage> PendingPackagesToAdd { get; } = new List<ChartPackage>();

    public List<ChartPackage> PendingPackagesToRemove { get; } = new List<ChartPackage>();

    public List<ChartPackage> AutoInstalledPackages { get; } = new List<ChartPackage>();

    public List<ChartPackage> AutoInstallFailures { get; } = new List<ChartPackage>();

    public List<ChartPackage> EstimateTargets { get; } = new List<ChartPackage>();

    public List<ChartPackage> InstallRowsToUpsert { get; } = new List<ChartPackage>();

    public List<string> InstallRowsToDelete { get; } = new List<string>();

    public long InstallMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
