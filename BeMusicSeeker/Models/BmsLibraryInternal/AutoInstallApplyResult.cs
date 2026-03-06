using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallApplyResult
{
    public List<BMSPackage> PendingPackagesToAdd { get; } = new List<BMSPackage>();

    public List<BMSPackage> PendingPackagesToRemove { get; } = new List<BMSPackage>();

    public List<BMSPackage> AutoInstalledPackages { get; } = new List<BMSPackage>();

    public List<BMSPackage> AutoInstallFailures { get; } = new List<BMSPackage>();

    public List<BMSPackage> EstimateTargets { get; } = new List<BMSPackage>();

    public List<BMSPackage> InstallRowsToUpsert { get; } = new List<BMSPackage>();

    public List<string> InstallRowsToDelete { get; } = new List<string>();

    public long InstallMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
