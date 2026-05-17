using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

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

    public List<BMSFile> PackageFiles { get; set; } = new List<BMSFile>();

    public List<BMSFile> AlreadyInstalledFiles { get; set; } = new List<BMSFile>();

    public List<BMSFile> MissingFiles { get; set; } = new List<BMSFile>();

    public ChartResourceSnapshot ChartResources { get; set; } = new ChartResourceSnapshot();

    public BmsInstallationEstimateMode EstimateMode { get; set; }

    public InstalledDirectoryLookupResult PreparationInstalledResolution { get; set; }

    public SourceSurfaceEntryView SourceSurface { get; set; }

    public SourceBaselinePrefilterResult BaselinePrefilter { get; set; } = new SourceBaselinePrefilterResult();

    public bool UsesBatchSourceSurface { get; set; }

    public bool HasMissingFiles => MissingFiles.Count > 0;

    public bool AttemptInstalledResolve => AlreadyInstalledFiles.Count > 0 && MissingFiles.Count > 0;
}

internal sealed class PendingEstimateSourceBatchSnapshot
{
    private readonly Dictionary<ChartPackage, PendingEstimateSourceBatchPackageState> packageStatesByPackage = new Dictionary<ChartPackage, PendingEstimateSourceBatchPackageState>();

    public List<PendingEstimateSourceBatchPackageState> PackageStates { get; } = new List<PendingEstimateSourceBatchPackageState>();

    public int RootCount { get; set; }

    public int ChunkCount { get; set; }

    public long NativeBridgeMs { get; set; }

    public long ManagedDecodeMs { get; set; }

    public long ManagedMaterializeMs { get; set; }

    public int TrackedFileCount { get; set; }

    public int ResourceFileCount { get; set; }

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
