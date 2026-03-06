using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchItem
{
    public BMSPackage OriginalPackage { get; set; }

    public BMSPackage InstallWorkPackage { get; set; }

    public string DestinationDirectory { get; set; }

    public bool IsResourceOnlyInstall { get; set; }

    public int InstallTargetFileCount { get; set; }

    public HashSet<string> ExcludedComponentPaths { get; set; }
}
