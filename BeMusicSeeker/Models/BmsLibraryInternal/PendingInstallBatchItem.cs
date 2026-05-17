using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchItem
{
    public ChartPackage OriginalPackage { get; set; }

    public ChartPackage InstallWorkPackage { get; set; }

    public string DestinationDirectory { get; set; }

    public bool IsResourceOnlyInstall { get; set; }

    public int InstallTargetFileCount { get; set; }

    public HashSet<string> ExcludedComponentPaths { get; set; }
}
