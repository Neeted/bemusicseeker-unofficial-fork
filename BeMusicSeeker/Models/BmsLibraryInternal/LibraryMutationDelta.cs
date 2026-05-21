using System.Collections.Generic;
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
}

internal sealed class LibraryChartPathChange
{
    public ChartFile Chart { get; set; }

    public string NewPath { get; set; }

    public string OldPath { get; set; }

    public bool CalcFolderParent { get; set; } = true;

    public BMSFile BmsFile => Chart?.GetBmsStorageOwner();

    public LR2SongDBExtended.bmson_song BmsonSong => Chart?.GetBmsonStorageOwner();
}

internal sealed class LibraryFolderPathChange
{
    public string NewFolderPath { get; set; }

    public string OldFolderPath { get; set; }
}

internal sealed class LibraryInstallDestinationChange
{
    public BMSFile BmsFile { get; set; }

    public PackageChartEntry Entry { get; set; }

    public string NewInstallDestination { get; set; }
}

internal sealed class LibraryInstalledPackagePathChange
{
    public ChartPackage Package { get; set; }

    public string NewPath { get; set; }
}
