using System.Collections.Generic;

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

    public bool ScanLimitExceeded { get; set; }

    public int VisitedFileSystemEntryCount { get; set; }

    public int MaxVisitedFileSystemEntryCount { get; set; }
}

internal sealed class PendingEstimateSourceBatchPackageState
{
    public ChartPackage Package { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string SourceDirectory { get; set; } = string.Empty;

    public List<PackageChartEntry> PackageEntries { get; set; } = [];

    public List<PackageChartEntry> AlreadyInstalledEntries { get; set; } = [];

    /// <summary>未所持群、または全既所持時の全対象です。</summary>
    public List<PackageChartEntry> TargetEntries { get; set; } = [];

    public ChartResourceSnapshot ChartResources { get; set; } = new ChartResourceSnapshot();

    public ChartInstallationEstimateMode EstimateMode { get; set; }

    public InstalledDirectoryLookupResult PreparationInstalledResolution { get; set; }


    public SourceSurfaceEntryView SourceSurface { get; set; }

    /// <summary>音源完全でハッシュ候補がない入力の新規リソース探索を省略します。既知ハッシュ候補のStrong評価や手動推定では設定しません。</summary>
    public bool SkipAutomaticEstimation { get; set; }

    public bool UsesBatchSourceSurface { get; set; }

    /// <summary>評価する使用資源の譜面群を持つかを返します。</summary>
    public bool HasTargetEntries => TargetEntries.Count > 0;

    public bool AttemptInstalledResolve => AlreadyInstalledEntries.Count > 0 && HasTargetEntries;
}

internal sealed class PendingEstimateSourceBatchSnapshot
{
    private readonly Dictionary<ChartPackage, PendingEstimateSourceBatchPackageState> packageStatesByPackage = [];

    public List<PendingEstimateSourceBatchPackageState> PackageStates { get; } = [];


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
