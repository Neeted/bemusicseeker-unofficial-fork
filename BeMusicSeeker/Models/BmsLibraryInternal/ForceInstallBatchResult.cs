using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ForceInstallBatchResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public List<BMSPackage> PendingPackagesToRemove { get; } = new List<BMSPackage>();

    public List<BMSPackage> DeferredInstalledPackages { get; } = new List<BMSPackage>();
}
