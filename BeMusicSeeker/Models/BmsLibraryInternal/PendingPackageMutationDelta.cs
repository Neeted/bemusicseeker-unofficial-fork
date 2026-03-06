using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingPackageMutationDelta
{
    public bool HasChanges { get; set; }

    public List<BMSPackage> RemainingPackages { get; set; } = new List<BMSPackage>();

    public List<string> InstallPathsToDelete { get; set; } = new List<string>();
}
