using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageInstallExecutionResult
{
    public List<PackageChartEntry> AddedEntries { get; } = [];

    public List<ChartFile> AddedCharts { get; } = [];

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<ChartPackage> InstalledPackagesToRegister { get; } = [];

    public long MoveMs { get; set; }

    public long SongDbMs { get; set; }

    public long MaintenanceMs { get; set; }

    public long ScoreMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
