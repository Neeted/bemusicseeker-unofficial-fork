using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageInstallExecutionResult
{
    public List<PackageChartEntry> AddedEntries { get; } = [];

    public List<ChartFile> AddedCharts { get; } = [];

    public List<BMSFile> AddedBmsFiles => [.. AddedCharts
        .Select(chart => chart?.GetBmsStorageOwner())
        .Where(ChartFileKindResolver.IsBmsChartFile)];

    public List<LR2SongDBExtended.bmson_song> AddedBmsonSongs => [.. AddedCharts
        .Select(chart => chart?.GetBmsonStorageOwner())
        .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<ChartPackage> InstalledPackagesToRegister { get; } = [];

    public long MoveMs { get; set; }

    public long SongDbMs { get; set; }

    public long MaintenanceMs { get; set; }

    public long ZeroNoteMs { get; set; }

    public long ScoreMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
