using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallApplyResult
{
    public List<ChartPackage> PendingPackagesToAdd { get; } = [];

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> AutoInstalledPackages { get; } = [];

    public List<ChartPackage> AutoInstallFailures { get; } = [];

    public List<ChartPackage> EstimateTargets { get; } = [];

    public List<ChartPackage> InstallRowsToUpsert { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

    public long InstallMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
