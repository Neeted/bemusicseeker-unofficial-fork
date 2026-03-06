using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallTableLoadResult
{
    public List<BMSPackage> PendingPackages { get; } = new List<BMSPackage>();

    public List<BMSPackage> StalePackages { get; } = new List<BMSPackage>();

    public List<string> StaleInstallPaths { get; } = new List<string>();
}
