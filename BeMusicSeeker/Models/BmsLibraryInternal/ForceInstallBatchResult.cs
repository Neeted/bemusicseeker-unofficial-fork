using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ForceInstallBatchResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public List<ChartPackage> PendingPackagesToRemove { get; } = new List<ChartPackage>();

    public List<ChartPackage> DeferredInstalledPackages { get; } = new List<ChartPackage>();
}
