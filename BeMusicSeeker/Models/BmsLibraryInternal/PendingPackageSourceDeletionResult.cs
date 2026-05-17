using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingPackageSourceDeletionResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int Removed { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public bool Canceled { get; set; }

    public List<ChartPackage> PackagesToRemove { get; } = new List<ChartPackage>();

    public List<PendingPackageSourceDeletionFailure> Failures { get; } = new List<PendingPackageSourceDeletionFailure>();
}
