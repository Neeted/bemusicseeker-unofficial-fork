using System.Collections.Generic;
namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchResult
{
    public HashSet<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> DeferredInstalledPackages { get; } = [];

    public List<ChartFile> DeferredMaintenanceCharts { get; } = [];

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

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
