using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Identifies the in-memory inputs from which a pending install estimate was produced.
/// Retry callers capture this stamp in the same read boundary as the package's
/// installed/missing partition so a newer stamp can never validate an older partition.
/// </summary>
internal readonly record struct PendingInstallEstimateCurrentnessStamp(
    long ResourceIndexGeneration,
    int OwnedCollectionVersion,
    long InstalledLookupGeneration,
    long DigestMutationGeneration);

internal sealed class SourceSurfaceEntryView
{
    public string SourceDirectory { get; set; } = string.Empty;

    public DirectoryResourceLookupCache.Entry ResourceEntry { get; set; } = new DirectoryResourceLookupCache.Entry();

    public int ChartFileCount { get; set; }

    public int ResourceFileCount { get; set; }

    public int TrackedFileCount { get; set; }

    public long ScanMs { get; set; }

    public long HashMaterializeMs { get; set; }

    public string ScanBackend { get; set; } = string.Empty;

    public bool ScanLimitExceeded { get; set; }

    public int VisitedFileSystemEntryCount { get; set; }

    public int MaxVisitedFileSystemEntryCount { get; set; }
}

internal sealed class SourceBaselinePrefilterResult
{
    public bool Deferred { get; set; }

    public int PrimaryHealth { get; set; }
}

internal sealed class PendingEstimateSourceBatchPackageState
{
    public ChartPackage Package { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string SourceDirectory { get; set; } = string.Empty;

    public List<PackageChartEntry> PackageEntries { get; set; } = [];

    public List<PackageChartEntry> AlreadyInstalledEntries { get; set; } = [];

    public List<PackageChartEntry> MissingEntries { get; set; } = [];

    public ChartResourceSnapshot ChartResources { get; set; } = new ChartResourceSnapshot();

    public ChartInstallationEstimateMode EstimateMode { get; set; }

    public InstalledDirectoryLookupResult PreparationInstalledResolution { get; set; }

    /// <summary>
    /// Gets or sets the runtime inputs used for <see cref="PreparationInstalledResolution"/>.
    /// </summary>
    public PendingInstallEstimateCurrentnessStamp? PreparationCurrentnessStamp { get; set; }

    public SourceSurfaceEntryView SourceSurface { get; set; }

    public SourceBaselinePrefilterResult BaselinePrefilter { get; set; } = new SourceBaselinePrefilterResult();

    public bool UsesBatchSourceSurface { get; set; }

    public bool HasMissingFiles => MissingEntries.Count > 0;

    public bool AttemptInstalledResolve => AlreadyInstalledEntries.Count > 0 && HasMissingFiles;
}

internal sealed class PendingEstimateSourceBatchSnapshot
{
    private readonly Dictionary<ChartPackage, PendingEstimateSourceBatchPackageState> packageStatesByPackage = [];

    public List<PendingEstimateSourceBatchPackageState> PackageStates { get; } = [];

    /// <summary>
    /// Gets or sets the versioned inputs shared by every package state in this batch. The stamp
    /// guards the prepared installed/missing partition and source-derived state as one unit.
    /// </summary>
    public PendingInstallEstimateCurrentnessStamp PreparationCurrentnessStamp { get; set; }

    public int RootCount { get; set; }

    public int ChunkCount { get; set; }

    public long NativeBridgeMs { get; set; }

    public long ManagedDecodeMs { get; set; }

    public long ManagedMaterializeMs { get; set; }

    public int TrackedFileCount { get; set; }

    public int ResourceFileCount { get; set; }

    public bool ScanLimitExceeded { get; set; }

    public int VisitedFileSystemEntryCount { get; set; }

    public int MaxVisitedFileSystemEntryCount { get; set; }

    public long ElapsedMs { get; set; }

    public long PrefilterMs { get; set; }

    public string ScanBackend { get; set; } = string.Empty;

    public void AddState(PendingEstimateSourceBatchPackageState state)
    {
        if (state?.Package == null)
        {
            return;
        }

        PackageStates.Add(state);
        packageStatesByPackage[state.Package] = state;
    }

    public bool TryGetState(ChartPackage package, out PendingEstimateSourceBatchPackageState state)
    {
        state = null;
        return package != null && packageStatesByPackage.TryGetValue(package, out state);
    }
}
