using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageInstallExecutionResult
{
    public List<BMSFile> AddedFiles { get; } = new List<BMSFile>();

    public List<ChartPackage> FailedPackages { get; } = new List<ChartPackage>();

    public List<ChartPackage> InstalledPackagesToRegister { get; } = new List<ChartPackage>();

    public long MoveMs { get; set; }

    public long SongDbMs { get; set; }

    public long MaintenanceMs { get; set; }

    public long ZeroNoteMs { get; set; }

    public long ScoreMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
