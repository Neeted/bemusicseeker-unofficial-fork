using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchResult
{
    public HashSet<BMSPackage> PendingPackagesToRemove { get; } = new HashSet<BMSPackage>();

    public List<BMSPackage> DeferredInstalledPackages { get; } = new List<BMSPackage>();

    public List<BMSFile> DeferredMaintenanceTargets { get; } = new List<BMSFile>();

    public List<BMSPackage> FailedPackages { get; } = new List<BMSPackage>();

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
