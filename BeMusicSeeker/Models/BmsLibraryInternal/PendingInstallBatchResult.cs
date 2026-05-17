using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchResult
{
    public HashSet<ChartPackage> PendingPackagesToRemove { get; } = new HashSet<ChartPackage>();

    public List<ChartPackage> DeferredInstalledPackages { get; } = new List<ChartPackage>();

    public List<BMSFile> DeferredMaintenanceTargets { get; } = new List<BMSFile>();

    public List<ChartPackage> FailedPackages { get; } = new List<ChartPackage>();

    public List<string> InstallRowsToDelete { get; } = new List<string>();

    public int CleanupOnlySucceeded { get; set; }

    public int CleanupOnlyFailed { get; set; }

    public int CleanupOnlyMissingSource { get; set; }

    public long MoveMs { get; set; }

    public long SongDbMs { get; set; }

    public long MaintenanceMs { get; set; }

    public long ZeroNoteMs { get; set; }

    public long ScoreMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
