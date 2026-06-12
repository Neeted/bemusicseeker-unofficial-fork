using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryMutationDelta
{
    internal List<OwnedChartRemoveRequest> ChartRemoveRequests { get; } = [];

    public List<LibraryChartPathChange> ChartPathChanges { get; } = [];

    public List<LibraryFolderPathChange> FolderPathChanges { get; } = [];

    public List<LibraryInstallDestinationChange> UpdatedInstallDestinations { get; } = [];

    public List<LibraryInstalledPackagePathChange> UpdatedInstalledPackagePaths { get; } = [];

    public List<LibraryDeleteFailure> Failures { get; } = [];

    public bool NotifyStorageRowPathChanges { get; set; }

    public bool RaiseInstalledPackagesChanged { get; set; }

    public bool InvalidateInstalledDirectoryIndex { get; set; }

    public bool InvalidateParentFolderCache { get; set; }

    public bool ClearDuplicatedCache { get; set; }

    public int RenamedCount { get; set; }

    public int DuplicateDeletedCount { get; set; }

    public int SkippedCount { get; set; }

    public long TotalMs { get; set; }

    public List<ChartFile> CreateAppliedInstallDestinationChartSnapshots()
    {
        return [.. UpdatedInstallDestinations
            .Select(change => change?.CreateAppliedChartSnapshot(ChartPathChanges))
            .Where(chart => chart != null)];
    }

    public void Clear()
    {
        ChartRemoveRequests.Clear();
        ChartPathChanges.Clear();
        FolderPathChanges.Clear();
        UpdatedInstallDestinations.Clear();
        UpdatedInstalledPackagePaths.Clear();
        Failures.Clear();
        NotifyStorageRowPathChanges = false;
        RaiseInstalledPackagesChanged = false;
        InvalidateInstalledDirectoryIndex = false;
        InvalidateParentFolderCache = false;
        ClearDuplicatedCache = false;
        RenamedCount = 0;
        DuplicateDeletedCount = 0;
        SkippedCount = 0;
        TotalMs = 0L;
    }
}

internal sealed class LibraryChartPathChange
{
    public ChartFile Chart { get; set; }

    public string NewPath { get; set; }

    public string OldPath { get; set; }

    internal BMSFile GetBmsStorageOwner()
    {
        return Chart?.GetBmsStorageOwner();
    }

    internal LR2SongDBExtended.bmson_song GetBmsonStorageOwner()
    {
        return Chart?.GetBmsonStorageOwner();
    }
}

internal sealed class LibraryFolderPathChange
{
    public string NewFolderPath { get; set; }

    public string OldFolderPath { get; set; }
}

internal sealed class BmsSongPathReplacement
{
    public BMSFile Song { get; set; }

    public string OldPath { get; set; }

    public BMSFileMaintenanceInfo MaintenanceInfo { get; set; }
}

internal sealed class BmsonSongPathReplacement
{
    public LR2SongDBExtended.bmson_song Song { get; set; }

    public string OldPath { get; set; }
}

internal sealed class BmsLibraryStateApplyResult
{
    public long FolderDbMs { get; set; }

    public long PathMemoryApplyMs { get; set; }

    public long BmsPathDbMs { get; set; }

    public long BmsonPathDbMs { get; set; }

    public long PackageApplyMs { get; set; }
}

internal sealed class LibraryInstallDestinationChange
{
    public ChartFile Chart { get; set; }

    public PackageChartEntry Entry { get; set; }

    public string NewInstallDestination { get; set; }

    public bool ClearInstallDestinationState { get; set; }

    internal string GetCurrentInstallDestination()
    {
        return Entry?.Chart?.InstallDestination
            ?? Chart?.InstallDestination;
    }

    internal BMSFile GetBmsStorageOwner()
    {
        return Chart?.GetBmsStorageOwner();
    }

    internal ChartFile CreateAppliedChartSnapshot(IEnumerable<LibraryChartPathChange> pathChanges = null)
    {
        ChartFile source = Entry?.Chart ?? Chart;
        if (source == null)
        {
            return null;
        }

        ChartFile appliedChart;
        if (ClearInstallDestinationState)
        {
            appliedChart = ChartFileProjection.WithPackageState(
                source,
                null,
                string.Empty,
                string.Empty,
                [],
                [.. (source.Warnings ?? []).Where(warning => warning?.Category != ChartWarningCategory.InstallEstimation)]);
        }
        else
        {
            appliedChart = ChartFileProjection.WithPackageState(
                source,
                NewInstallDestination,
                source.InstallDestinationTitle,
                source.InstallDestinationArtist,
                source.InstallDestinationSuggestions,
                source.Warnings);
        }

        string newPath = Entry == null ? ResolveNewPath(source, pathChanges) : null;
        return string.IsNullOrWhiteSpace(newPath)
            ? appliedChart
            : ChartFileProjection.WithPath(appliedChart, newPath);
    }

    private static string ResolveNewPath(ChartFile source, IEnumerable<LibraryChartPathChange> pathChanges)
    {
        if (source == null)
        {
            return null;
        }

        BMSFile bmsFile = source.GetBmsStorageOwner();
        LR2SongDBExtended.bmson_song bmsonSong = source.GetBmsonStorageOwner();
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            ChartFile changedChart = pathChange?.Chart;
            if (changedChart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
            {
                continue;
            }
            if (ReferenceEquals(changedChart, source)
                || (bmsFile != null && ReferenceEquals(changedChart.GetBmsStorageOwner(), bmsFile))
                || (bmsonSong != null && ReferenceEquals(changedChart.GetBmsonStorageOwner(), bmsonSong)))
            {
                return pathChange.NewPath;
            }
        }
        return null;
    }
}

internal sealed class LibraryInstalledPackagePathChange
{
    public ChartPackage Package { get; set; }

    public string NewPath { get; set; }
}
