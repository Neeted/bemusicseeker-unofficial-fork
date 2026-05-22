using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryMutationDelta
{
    public List<ChartFile> ChartsToUnregister { get; } = [];

    public List<LibraryChartPathChange> ChartPathChanges { get; } = [];

    public List<LibraryFolderPathChange> FolderPathChanges { get; } = [];

    public List<LibraryInstallDestinationChange> UpdatedInstallDestinations { get; } = [];

    public List<LibraryInstalledPackagePathChange> UpdatedInstalledPackagePaths { get; } = [];

    public List<LibraryDeleteFailure> Failures { get; } = [];

    public bool RaiseLibraryChartsChanged { get; set; }

    public bool RaiseInstalledPackagesChanged { get; set; }

    public bool InvalidateInstalledDirectoryIndex { get; set; }

    public bool InvalidateParentFolderCache { get; set; }

    public bool ClearDuplicatedCache { get; set; }

    public int RenamedCount { get; set; }

    public int DuplicateDeletedCount { get; set; }

    public int SkippedCount { get; set; }

    public long TotalMs { get; set; }

    public void Clear()
    {
        ChartsToUnregister.Clear();
        ChartPathChanges.Clear();
        FolderPathChanges.Clear();
        UpdatedInstallDestinations.Clear();
        UpdatedInstalledPackagePaths.Clear();
        Failures.Clear();
        RaiseLibraryChartsChanged = false;
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

    public bool CalcFolderParent { get; set; } = true;

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

    internal ChartFile CreateAppliedChartSnapshot()
    {
        if (Entry?.Chart != null)
        {
            return Entry.Chart;
        }

        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return ChartFileProjection.FromBmsFile(bmsFile);
        }

        if (Chart == null)
        {
            return null;
        }

        if (ClearInstallDestinationState)
        {
            return ChartFileProjection.WithPackageState(
                Chart,
                null,
                string.Empty,
                string.Empty,
                [],
                [.. (Chart.Warnings ?? []).Where(warning => warning?.Category != ChartWarningCategory.InstallEstimation)]);
        }

        return ChartFileProjection.WithPackageState(
            Chart,
            NewInstallDestination,
            Chart.InstallDestinationTitle,
            Chart.InstallDestinationArtist,
            Chart.InstallDestinationSuggestions,
            Chart.Warnings);
    }
}

internal sealed class LibraryInstalledPackagePathChange
{
    public ChartPackage Package { get; set; }

    public string NewPath { get; set; }
}
