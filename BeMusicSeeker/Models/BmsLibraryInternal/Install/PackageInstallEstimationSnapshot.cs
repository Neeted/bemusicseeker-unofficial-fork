using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageInstallSurfaceSnapshot
{
    public static PackageInstallSurfaceSnapshot Empty { get; } = new PackageInstallSurfaceSnapshot
    {
        IsComplete = false,
        SourcePath = string.Empty,
        SourceDirectory = string.Empty,
        BundledResources = new DirectoryResourceLookupCache.Entry(),
        SourceCandidateResources = new DirectoryResourceLookupCache.Entry(),
        ScanBackend = string.Empty
    };

    public string SourcePath { get; set; } = string.Empty;

    public string SourceDirectory { get; set; } = string.Empty;

    public DirectoryResourceLookupCache.Entry BundledResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public DirectoryResourceLookupCache.Entry SourceCandidateResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public long ScanMs { get; set; }

    public int ChartFileCount { get; set; }

    public int ResourceFileCount { get; set; }

    public int TrackedFileCount { get; set; }

    public long HashMaterializeMs { get; set; }

    public string ScanBackend { get; set; } = string.Empty;

    /// <summary>同梱資源の不存在を確定できる、成功した完全な列挙結果かを示します。</summary>
    public bool IsComplete { get; set; } = true;

    public bool ScanLimitExceeded { get; set; }

    public int VisitedFileSystemEntryCount { get; set; }

    public int MaxVisitedFileSystemEntryCount { get; set; }
}

internal sealed class PackageChartDiscoverySnapshot
{
    private List<PackageChartEntry> chartEntries = [];

    public string SourcePath { get; set; } = string.Empty;

    /// <summary>列挙が完了し、見つかった全譜面の読取りに成功した場合に限り真です。部分的な譜面一覧は保持します。</summary>
    public bool IsComplete { get; set; }

    public List<PackageChartEntry> ChartEntries
    {
        get
        {
            return [.. chartEntries.Where(entry => entry?.Chart != null)];
        }
        set
        {
            chartEntries = NormalizeChartEntries(value);
        }
    }

    internal void ReplaceChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        chartEntries = NormalizeChartEntries(entries);
    }

    private static List<PackageChartEntry> NormalizeChartEntries(IEnumerable<PackageChartEntry> entries)
    {
        var normalizedEntries = new List<PackageChartEntry>();
        foreach (PackageChartEntry entry in entries ?? [])
        {
            PackageChartEntry snapshot = entry;
            if (snapshot?.Chart == null)
            {
                continue;
            }
            normalizedEntries.Add(snapshot);
        }
        return normalizedEntries;
    }

}

internal sealed class PackageInstallEstimationSnapshot
{
    public ChartFile RepresentativeChart { get; set; }

    /// <summary>取得済みの使用集合です。未捕捉のnullを正常な0件へ補完しません。</summary>
    public ChartResourceSnapshot DefinedResources { get; set; }

    /// <summary>集約充足率で個別譜面の不足を隠さないため、同じ対象群の取得済み使用集合を保持します。</summary>
    public IReadOnlyList<ChartResourceSnapshot> TargetChartResources { get; set; } = [];

    public InstallEstimationMetadataProfile TargetMetadataProfile { get; set; } = InstallEstimationMetadataProfile.Empty;

    /// <summary>捕捉済み同梱索引です。未捕捉のnullと既知の空索引を区別します。</summary>
    public DirectoryResourceLookupCache.Entry BundledResources { get; set; }

    public DirectoryResourceLookupCache.Entry SourceCandidateResources { get; set; } = new DirectoryResourceLookupCache.Entry();

    public string SourceDirectory { get; set; } = string.Empty;

    public int ChartCount { get; set; }

    public int BundledAudioCount => BundledResources?.AudioFileNameHashCount ?? 0;

    public int BundledImageCount => BundledResources?.ImageFileNameHashCount ?? 0;

    public int BundledMovieCount => BundledResources?.MovieFileNameHashCount ?? 0;

    public long SourceSurfaceScanMs { get; set; }

    public int SourceSurfaceChartFileCount { get; set; }

    public int SourceSurfaceResourceFileCount { get; set; }

    public int SourceSurfaceTrackedFileCount { get; set; }

    public long SourceSurfaceHashMaterializeMs { get; set; }

    public bool SourceSurfaceCacheHit { get; set; }

    public bool SourceSurfaceBatchHit { get; set; }

    public string SourceSurfaceScanBackend { get; set; } = string.Empty;

    /// <summary>観測済みの同梱にない参照を欠損と判定できるかを示します。</summary>
    public bool SourceSurfaceIsComplete { get; set; } = true;

    public bool SourceSurfaceScanLimitExceeded { get; set; }

    public int SourceSurfaceVisitedFileSystemEntryCount { get; set; }

    public int SourceSurfaceMaxVisitedFileSystemEntryCount { get; set; }
}

/// <summary>入力元だけの確認状態です。未取得・既知0件・観測下限を区別します。</summary>
internal enum InstallEstimationSourceAudioStatus { Unverifiable, NoAudioReferences, Verified, LowerBound, ReinstallNotApplicable }

/// <summary>候補に依存しない同梱一致の観測結果です。同じ件数を候補の和集合集計へ再利用します。</summary>
internal readonly record struct InstallEstimationSourceAudio(int ObservedMatched, int? Required, InstallEstimationSourceAudioStatus Status)
{
    /// <summary>確定した要求の観測下限で90%以上を証明した場合だけ、両項目一致を要求します。</summary>
    internal InstallEstimationRelatedRule RelatedRule { get; } = Status is InstallEstimationSourceAudioStatus.Verified or InstallEstimationSourceAudioStatus.LowerBound
        && Required > 0 && 100L * ObservedMatched >= 90L * Required.Value
        ? InstallEstimationRelatedRule.BothFields : InstallEstimationRelatedRule.AnyField;

    /// <summary>率未定義の状態を0%や100%へ変換しない、操作内の診断理由です。</summary>
    internal string Reason => Status switch
    {
        InstallEstimationSourceAudioStatus.NoAudioReferences => "no_audio_references",
        InstallEstimationSourceAudioStatus.ReinstallNotApplicable => "reinstall_not_applicable",
        InstallEstimationSourceAudioStatus.Verified => RelatedRule == InstallEstimationRelatedRule.BothFields ? "source_audio_at_least_90" : "source_audio_below_90",
        InstallEstimationSourceAudioStatus.LowerBound when RelatedRule == InstallEstimationRelatedRule.BothFields => "source_audio_lower_bound_at_least_90",
        _ => "source_audio_unverifiable"
    };

    /// <summary>率を捏造せず、通常候補とハッシュ候補の同じ診断へ入力状態を渡します。</summary>
    internal string ToSummary() => $"sourceAudioObservedMatched={ObservedMatched} sourceAudioRequired={Required?.ToString() ?? "unknown"} sourceAudioStatus={Status} relatedRule={RelatedRule} relatedRuleReason={Reason}";

    /// <summary>捕捉済み要求と同梱だけを一回照合します。確認不能でも観測数を保持し、物理I/Oを行いません。</summary>
    internal static InstallEstimationSourceAudio Evaluate(ChartResourceSnapshot resources, DirectoryResourceLookupCache.Entry bundled, bool complete, bool scanLimitExceeded = false)
    {
        ISet<uint> supplied = bundled?.AudioRelativePathHashes;
        int observed = supplied == null || resources == null ? 0 : resources.AudioReferences.Count(reference => supplied.Contains(reference.RelativePathHash));
        bool unresolved = resources?.HasUnresolvedAudio == true;
        if (resources == null || unresolved || scanLimitExceeded)
        { return new(observed, resources == null || unresolved ? null : resources.AudioReferenceCount, InstallEstimationSourceAudioStatus.Unverifiable); }
        if (resources.AudioReferenceCount == 0) { return new(observed, 0, InstallEstimationSourceAudioStatus.NoAudioReferences); }
        return new(observed, resources.AudioReferenceCount, bundled == null ? InstallEstimationSourceAudioStatus.Unverifiable
            : complete || observed == resources.AudioReferenceCount ? InstallEstimationSourceAudioStatus.Verified : InstallEstimationSourceAudioStatus.LowerBound);
    }
}

internal static class PackageInstallEstimationSnapshotBuilder
{
    internal const int DefaultSourceSurfaceMaxVisitedFileSystemEntries = 50000;

    internal static PackageInstallEstimationSnapshot Build(ChartPackage package, IEnumerable<PackageChartEntry> targetEntries, PackageInstallSurfaceSnapshot installSurfaceSnapshot, bool sourceSurfaceCacheHit, bool sourceSurfaceBatchHit = false, ChartResourceSnapshot definedResources = null)
    {
        List<PackageChartEntry> normalizedTargetEntries = NormalizeEntries(targetEntries);
        PackageChartEntry representativeEntry = SelectRepresentativeEntry(normalizedTargetEntries);
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = representativeEntry?.Chart,
            DefinedResources = definedResources ?? ChartResourceSnapshot.CreateAggregate(normalizedTargetEntries.Select(entry => entry.Chart)),
            TargetMetadataProfile = BuildTargetMetadataProfile(normalizedTargetEntries),
            TargetChartResources = normalizedTargetEntries.Select(entry => ChartResourceSnapshot.Create(entry.Chart)).ToArray(),
            BundledResources = installSurfaceSnapshot?.BundledResources?.Clone(),
            SourceCandidateResources = installSurfaceSnapshot?.SourceCandidateResources?.Clone() ?? new DirectoryResourceLookupCache.Entry(),
            SourceDirectory = installSurfaceSnapshot?.SourceDirectory ?? ResolveSourceDirectory(package?.path),
            ChartCount = normalizedTargetEntries.Count,
            SourceSurfaceScanMs = installSurfaceSnapshot?.ScanMs ?? 0L,
            SourceSurfaceChartFileCount = installSurfaceSnapshot?.ChartFileCount ?? 0,
            SourceSurfaceResourceFileCount = installSurfaceSnapshot?.ResourceFileCount ?? 0,
            SourceSurfaceTrackedFileCount = installSurfaceSnapshot?.TrackedFileCount ?? 0,
            SourceSurfaceHashMaterializeMs = installSurfaceSnapshot?.HashMaterializeMs ?? 0L,
            SourceSurfaceCacheHit = sourceSurfaceCacheHit,
            SourceSurfaceBatchHit = sourceSurfaceBatchHit,
            SourceSurfaceScanBackend = installSurfaceSnapshot?.ScanBackend ?? string.Empty,
            SourceSurfaceIsComplete = installSurfaceSnapshot?.IsComplete == true,
            SourceSurfaceScanLimitExceeded = installSurfaceSnapshot?.ScanLimitExceeded ?? false,
            SourceSurfaceVisitedFileSystemEntryCount = installSurfaceSnapshot?.VisitedFileSystemEntryCount ?? 0,
            SourceSurfaceMaxVisitedFileSystemEntryCount = installSurfaceSnapshot?.MaxVisitedFileSystemEntryCount ?? 0
        };
    }

    /// <summary>
    /// Builds a loose-chart estimation snapshot without treating the representative chart's parent as a package surface.
    /// </summary>
    internal static PackageInstallEstimationSnapshot BuildForLooseEntries(IEnumerable<PackageChartEntry> targetEntries)
    {
        List<PackageChartEntry> normalizedTargetEntries = NormalizeEntries(targetEntries);
        PackageChartEntry representativeEntry = SelectRepresentativeEntry(normalizedTargetEntries);
        string sourcePath = representativeEntry?.Chart?.Path ?? string.Empty;
        PackageInstallSurfaceSnapshot sourceSurfaceSnapshot = CreateFileInstallSurfaceSnapshot(sourcePath, ResolveSourceDirectory(sourcePath));
        return new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = representativeEntry?.Chart,
            DefinedResources = ChartResourceSnapshot.CreateAggregate(normalizedTargetEntries.Select(entry => entry.Chart)),
            TargetMetadataProfile = BuildTargetMetadataProfile(normalizedTargetEntries),
            TargetChartResources = normalizedTargetEntries.Select(entry => ChartResourceSnapshot.Create(entry.Chart)).ToArray(),
            BundledResources = sourceSurfaceSnapshot.BundledResources.Clone(),
            SourceCandidateResources = sourceSurfaceSnapshot.SourceCandidateResources.Clone(),
            SourceDirectory = sourceSurfaceSnapshot.SourceDirectory,
            ChartCount = normalizedTargetEntries.Count,
            SourceSurfaceScanMs = sourceSurfaceSnapshot.ScanMs,
            SourceSurfaceChartFileCount = sourceSurfaceSnapshot.ChartFileCount,
            SourceSurfaceResourceFileCount = sourceSurfaceSnapshot.ResourceFileCount,
            SourceSurfaceTrackedFileCount = sourceSurfaceSnapshot.TrackedFileCount,
            SourceSurfaceHashMaterializeMs = sourceSurfaceSnapshot.HashMaterializeMs,
            SourceSurfaceCacheHit = false,
            SourceSurfaceBatchHit = false,
            SourceSurfaceScanBackend = sourceSurfaceSnapshot.ScanBackend,
            SourceSurfaceIsComplete = sourceSurfaceSnapshot.IsComplete,
            SourceSurfaceScanLimitExceeded = sourceSurfaceSnapshot.ScanLimitExceeded,
            SourceSurfaceVisitedFileSystemEntryCount = sourceSurfaceSnapshot.VisitedFileSystemEntryCount,
            SourceSurfaceMaxVisitedFileSystemEntryCount = sourceSurfaceSnapshot.MaxVisitedFileSystemEntryCount
        };
    }

    internal static PackageInstallSurfaceSnapshot BuildSharedInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, SourceSurfaceEntryView sourceSurface, bool includeBundledResources)
    {
        DirectoryResourceLookupCache.Entry resourceEntry = sourceSurface?.ResourceEntry?.Clone() ?? new DirectoryResourceLookupCache.Entry();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath ?? string.Empty,
            SourceDirectory = sourceDirectory ?? string.Empty,
            BundledResources = includeBundledResources ? resourceEntry.Clone() : new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = resourceEntry,
            ScanMs = 0L,
            ChartFileCount = sourceSurface?.ChartFileCount ?? 0,
            ResourceFileCount = sourceSurface?.ResourceFileCount ?? 0,
            TrackedFileCount = sourceSurface?.TrackedFileCount ?? 0,
            HashMaterializeMs = 0L,
            ScanBackend = sourceSurface?.ScanBackend ?? string.Empty,
            IsComplete = !includeBundledResources || sourceSurface?.IsComplete == true,
            ScanLimitExceeded = sourceSurface?.ScanLimitExceeded ?? false,
            VisitedFileSystemEntryCount = sourceSurface?.VisitedFileSystemEntryCount ?? 0,
            MaxVisitedFileSystemEntryCount = sourceSurface?.MaxVisitedFileSystemEntryCount ?? 0
        };
    }

    /// <summary>対象の譜面一覧を取得し、フォルダ内の全譜面を確認できたかを同じ走査の結果として返します。</summary>
    internal static PackageChartDiscoverySnapshot BuildPackageChartDiscoverySnapshot(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return new PackageChartDiscoverySnapshot();
        }

        string normalizedPath;
        try
        {
            normalizedPath = LongPathFileSystem.NormalizePathForStorage(packagePath);
        }
        catch
        {
            return new PackageChartDiscoverySnapshot();
        }

        if (LongPathFileSystem.DirectoryExists(normalizedPath))
        {
            RootFileEnumerationResult enumerationResult = EnumerateChartsOnlyFastOnly(normalizedPath);
            IReadOnlyCollection<string> chartPaths = enumerationResult.GetPaths(ChartDirectoryScanBuilder.ChartGroupName);
            List<PackageChartEntry> entries = CreatePendingChartEntriesFromPaths(chartPaths);
            return new PackageChartDiscoverySnapshot
            {
                SourcePath = normalizedPath,
                IsComplete = enumerationResult.Success && enumerationResult.IsComplete && entries.Count == chartPaths.Count,
                ChartEntries = entries
            };
        }

        List<PackageChartEntry> fileEntries = LongPathFileSystem.FileExists(normalizedPath) && ChartFileKindResolver.IsSupportedChartFilePath(normalizedPath)
            ? [.. new List<PackageChartEntry> { PackageChartEntry.FromPath(normalizedPath) }.Where(entry => entry != null)]
            : [];
        return new PackageChartDiscoverySnapshot
        {
            SourcePath = normalizedPath,
            IsComplete = fileEntries.Count == 1,
            ChartEntries = fileEntries
        };
    }

    /// <summary>
    /// <paramref name="packagePath"/> がディレクトリ型パッケージの場合だけ再帰列挙し、上限超過以外の部分資源は従来どおり推定入力へ渡します。
    /// </summary>
    internal static PackageInstallSurfaceSnapshot BuildPackageInstallSurfaceSnapshot(string packagePath)
    {
        return BuildPackageInstallSurfaceSnapshot(packagePath, DefaultSourceSurfaceMaxVisitedFileSystemEntries);
    }

    /// <summary>
    /// ディレクトリ型パッケージだけに <paramref name="maxVisitedFileSystemEntryCount"/> を適用し、不完全な列挙の状態と観測件数を診断します。
    /// </summary>
    internal static PackageInstallSurfaceSnapshot BuildPackageInstallSurfaceSnapshot(string packagePath, int maxVisitedFileSystemEntryCount)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return PackageInstallSurfaceSnapshot.Empty;
        }

        string normalizedPath;
        try
        {
            normalizedPath = LongPathFileSystem.NormalizePathForStorage(packagePath);
        }
        catch
        {
            return PackageInstallSurfaceSnapshot.Empty;
        }

        if (LongPathFileSystem.DirectoryExists(normalizedPath))
        {
            return CreateDirectoryInstallSurfaceSnapshot(normalizedPath, normalizedPath, maxVisitedFileSystemEntryCount);
        }

        string sourceDirectory = ResolveSourceDirectory(normalizedPath);
        return CreateFileInstallSurfaceSnapshot(normalizedPath, sourceDirectory);
    }

    private static PackageInstallSurfaceSnapshot CreateDirectoryInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, int maxVisitedFileSystemEntryCount)
    {
        return BuildDirectoryInstallSurfaceSnapshot(sourcePath, sourceDirectory, EnumerateSourceSurfaceBounded(sourceDirectory, maxVisitedFileSystemEntryCount));
    }

    /// <summary>同じ列挙結果から観測済み資源と完全性を捕捉します。通常失敗の部分資源を推定入力から除外しません。</summary>
    internal static PackageInstallSurfaceSnapshot BuildDirectoryInstallSurfaceSnapshot(string sourcePath, string sourceDirectory, RootFileEnumerationResult enumerationResult)
    {
        int chartFileCount = enumerationResult.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).Count;
        int resourceFileCount = CountDistinctPaths(
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.AudioGroupName),
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.ImageGroupName),
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.MovieGroupName));
        int trackedFileCount = CountDistinctPaths(
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.ChartGroupName),
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.AudioGroupName),
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.ImageGroupName),
            enumerationResult.GetPaths(ChartDirectoryScanBuilder.MovieGroupName));
        if (!enumerationResult.Success || !enumerationResult.IsComplete || enumerationResult.ScanLimitExceeded)
        {
            try
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn(
                    "package_install_source_surface_incomplete root={0} success={1} complete={2} limitExceeded={3} observedChartFiles={4} observedResourceFiles={5} observedTrackedFiles={6} reason={7}",
                    sourceDirectory, enumerationResult.Success, enumerationResult.IsComplete, enumerationResult.ScanLimitExceeded,
                    chartFileCount, resourceFileCount, trackedFileCount, RootFileEnumerationService.GetNonAuthoritativeReason(enumerationResult));
            }
            catch
            {
                // 任意の診断出力で、通常の部分結果を使う既存の推定条件を変えない。
            }
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // 通常の外部失敗では取得済み資源を保ち、従来どおり上限超過だけで破棄する。
        DirectoryResourceLookupCache.Entry entry = enumerationResult?.ScanLimitExceeded == true
            ? new DirectoryResourceLookupCache.Entry()
            : CreateResourceEntryFromEnumeration(sourceDirectory, enumerationResult);
        stopwatch.Stop();
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = entry,
            SourceCandidateResources = entry.Clone(),
            ScanMs = enumerationResult?.EnumerationMs ?? 0L,
            ChartFileCount = chartFileCount,
            ResourceFileCount = resourceFileCount,
            TrackedFileCount = trackedFileCount,
            HashMaterializeMs = stopwatch.ElapsedMilliseconds,
            ScanBackend = enumerationResult?.BackendName ?? "bounded_fast_source_surface",
            IsComplete = enumerationResult.Success && enumerationResult.IsComplete && !enumerationResult.ScanLimitExceeded,
            ScanLimitExceeded = enumerationResult?.ScanLimitExceeded ?? false,
            VisitedFileSystemEntryCount = enumerationResult?.VisitedFileSystemEntryCount ?? 0,
            MaxVisitedFileSystemEntryCount = enumerationResult?.MaxVisitedFileSystemEntryCount ?? 0
        };
    }

    private static PackageInstallSurfaceSnapshot CreateFileInstallSurfaceSnapshot(string sourcePath, string sourceDirectory)
    {
        return new PackageInstallSurfaceSnapshot
        {
            SourcePath = sourcePath,
            SourceDirectory = sourceDirectory,
            BundledResources = new DirectoryResourceLookupCache.Entry(),
            SourceCandidateResources = new DirectoryResourceLookupCache.Entry(),
            ScanMs = 0L,
            ChartFileCount = 0,
            ResourceFileCount = 0,
            TrackedFileCount = 0,
            HashMaterializeMs = 0L,
            ScanBackend = string.Empty,
            ScanLimitExceeded = false,
            VisitedFileSystemEntryCount = 0,
            MaxVisitedFileSystemEntryCount = 0
        };
    }

    private static DirectoryResourceLookupCache.Entry CreateResourceEntryFromEnumeration(string rootDirectory, RootFileEnumerationResult enumerationResult)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !LongPathFileSystem.DirectoryExists(rootDirectory))
        {
            return new DirectoryResourceLookupCache.Entry();
        }

        return ResourceSurfaceMaterializer.CreateSingleRootEntry(
            rootDirectory,
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName) ?? [],
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName) ?? [],
            enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName) ?? []);
    }

    private static RootFileEnumerationResult EnumerateChartsOnlyFastOnly(string rootDirectory)
    {
        return new FastRootFileEnumerator().EnumerateFiles(
            [rootDirectory],
            [
                new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, ChartDirectoryScanBuilder.ChartExtensions)
            ]);
    }

    private static RootFileEnumerationResult EnumerateSourceSurfaceBounded(string rootDirectory, int maxVisitedFileSystemEntryCount)
    {
        return new BoundedSourceSurfaceEnumerator(maxVisitedFileSystemEntryCount).EnumerateFiles(
            [rootDirectory],
            ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: false));
    }

    private static List<PackageChartEntry> CreatePendingChartEntriesFromPaths(IEnumerable<string> chartPaths)
    {
        return [.. (chartPaths ?? [])
            .Select(PackageChartEntry.FromPath)
            .Where(entry => entry != null)];
    }

    private static int CountDistinctPaths(params IReadOnlyCollection<string>[] groups)
    {
        return (groups ?? [])
            .Where(group => group != null)
            .SelectMany(group => group)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static List<PackageChartEntry> NormalizeEntries(IEnumerable<PackageChartEntry> targetEntries)
    {
        return [.. (targetEntries ?? []).Where(entry => entry?.Chart != null)];
    }

    private static PackageChartEntry SelectRepresentativeEntry(IReadOnlyCollection<PackageChartEntry> targetEntries)
    {
        return targetEntries?
            .Where(entry => entry?.Chart != null)
            .OrderByDescending(entry => entry.ResourceSnapshot.TotalReferenceCount)
            .FirstOrDefault();
    }

    private static string ResolveSourceDirectory(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return string.Empty;
        }

        try
        {
            string normalizedPath = LongPathFileSystem.NormalizePathForStorage(packagePath);
            if (LongPathFileSystem.DirectoryExists(normalizedPath))
            {
                return LongPathFileSystem.TrimTrailingDirectorySeparators(normalizedPath);
            }
            return Path.GetDirectoryName(normalizedPath) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>原題と、BMSの元作者・bmsonの現在の作者から対象群の実在最頻ペアを作り、資源評価とハッシュ候補評価で共用します。</summary>
    internal static InstallEstimationMetadataProfile BuildTargetMetadataProfile(IEnumerable<PackageChartEntry> targetEntries)
    {
        return InstallEstimationMetadataNormalizer.BuildProfile(
            (targetEntries ?? [])
                .Select(entry => entry?.Chart)
                .Where(chart => chart != null)
                .Select(chart => (chart.RawTitle ?? string.Empty,
                    (chart.Kind == ChartFileKind.Bms ? chart.RawArtist : chart.Artist) ?? string.Empty,
                    chart.Path ?? string.Empty)));
    }
}
