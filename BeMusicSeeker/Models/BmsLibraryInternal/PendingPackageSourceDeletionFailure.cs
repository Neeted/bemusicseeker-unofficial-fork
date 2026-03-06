using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingPackageSourceDeletionFailure
{
    public BMSPackage Package { get; set; }

    public Exception Exception { get; set; }

    public bool IsDirectory { get; set; }
}
