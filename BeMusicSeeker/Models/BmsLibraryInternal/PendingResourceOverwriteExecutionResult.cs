using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingResourceOverwriteExecutionResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int SucceededInstall { get; set; }

    public int SucceededCleanupOnly { get; set; }

    public int SkippedNotPending { get; set; }

    public int SkippedMissingInstlDst { get; set; }

    public int SkippedMultiDestination { get; set; }

    public int SkippedNoComponentTarget { get; set; }

    public int Failed { get; set; }

    public bool Canceled { get; set; }

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

    public PendingInstalledOnlyResourceOverwriteResult ToPublicResult()
    {
        return new PendingInstalledOnlyResourceOverwriteResult
        {
            Requested = Requested,
            Processed = Processed,
            SucceededInstall = SucceededInstall,
            SucceededCleanupOnly = SucceededCleanupOnly,
            SkippedNotPending = SkippedNotPending,
            SkippedMissingInstlDst = SkippedMissingInstlDst,
            SkippedMultiDestination = SkippedMultiDestination,
            SkippedNoComponentTarget = SkippedNoComponentTarget,
            Failed = Failed,
            Canceled = Canceled
        };
    }
}
