using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingPackageMutationDelta
{
    public bool HasChanges { get; set; }

    public List<ChartPackage> RemainingPackages { get; set; } = new List<ChartPackage>();

    public List<string> InstallPathsToDelete { get; set; } = new List<string>();
}
