using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 譜面のリソースから用途別の検索キーを作り、実際の解析に由来する非対応理由を保持します。
/// 拡張子除去後の空キーや種類不明だけを、親ディレクトリ参照の理由にはしません。
/// </summary>
internal sealed class ChartResourceSnapshot
{
    /// <summary>拡張子を除去済みのキーと、その索引値です。原文は確定結果で保持します。</summary>
    internal readonly struct ResourceReference(string lookupKey, uint relativePathHash, bool isPathAware)
    {
        public string LookupKey { get; } = lookupKey;
        public uint RelativePathHash { get; } = relativePathHash;
        public bool IsPathAware { get; } = isPathAware;
    }

    private readonly List<ResourceReference> audioReferences = [];

    private readonly List<ResourceReference> visualReferences = [];

    private readonly List<ResourceReference> movieReferences = [];

    private readonly List<ResourceReference> optionalImageReferences = [];

    private readonly List<ChartResourceReference> resourceReferences = [];

    public HashSet<string> AudioRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioRelativePathHashes { get; } = [];

    public HashSet<string> AudioPathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> AudioPathAwareRelativePathHashes { get; } = [];

    public HashSet<string> VisualRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualRelativePathHashes { get; } = [];

    public HashSet<string> VisualPathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> VisualPathAwareRelativePathHashes { get; } = [];

    public HashSet<string> MovieRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MovieRelativePathHashes { get; } = [];

    public HashSet<string> MoviePathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> MoviePathAwareRelativePathHashes { get; } = [];

    public HashSet<string> OptionalImageRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImageRelativePathHashes { get; } = [];

    public HashSet<string> OptionalImagePathAwareRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> OptionalImagePathAwareRelativePathHashes { get; } = [];

    public int AudioReferenceCount => AudioRelativePaths.Count;

    public int VisualReferenceCount => VisualRelativePaths.Count;

    public int MovieReferenceCount => MovieRelativePaths.Count;

    public int OptionalImageReferenceCount => OptionalImageRelativePaths.Count;

    public int TotalReferenceCount => AudioReferenceCount + VisualReferenceCount + MovieReferenceCount + OptionalImageReferenceCount;

    public int UnsupportedResourceReferenceCount => resourceReferences.Count(IsUnsupported);

    /// <summary>独立した <c>..</c> セグメントが解析で検出された非対応参照を含むかを返します。</summary>
    public bool HasUnsupportedParentTraversalReference => resourceReferences.Any(reference => reference.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported);

    public int PathSegmentReferenceCount { get; private set; }

    public int AudioPathAwareReferenceCount => AudioPathAwareRelativePaths.Count;

    public int VisualPathAwareReferenceCount => VisualPathAwareRelativePaths.Count;

    public int MoviePathAwareReferenceCount => MoviePathAwareRelativePaths.Count;

    public int OptionalImagePathAwareReferenceCount => OptionalImagePathAwareRelativePaths.Count;

    public IReadOnlyList<ResourceReference> AudioReferences => audioReferences;

    public IReadOnlyList<ResourceReference> VisualReferences => visualReferences;

    public IReadOnlyList<ResourceReference> MovieReferences => movieReferences;

    public IReadOnlyList<ResourceReference> OptionalImageReferences => optionalImageReferences;

    public IReadOnlyList<ChartResourceReference> ResourceReferences => resourceReferences;

    public IEnumerable<ChartResourceReference> UnsupportedResourceReferences => resourceReferences.Where(IsUnsupported);

    private static bool IsUnsupported(ChartResourceReference reference) => reference.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported
        || reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported;

    public HashSet<uint> EnumerateAllRelativePathHashes()
    {
        return [.. AudioRelativePathHashes
                .Concat(VisualRelativePathHashes)
                .Concat(MovieRelativePathHashes)
                .Concat(OptionalImageRelativePathHashes)];
    }

    public IEnumerable<string> EnumerateAllRelativePaths()
    {
        return AudioRelativePaths
            .Concat(VisualRelativePaths)
            .Concat(MovieRelativePaths)
            .Concat(OptionalImageRelativePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>取得済みの確定結果だけを索引化します。未取得を空成功として扱いません。</summary>
    public static ChartResourceSnapshot Create(ChartFile chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return Create(chart.Resources ?? throw new InvalidOperationException("Resource references have not been acquired."));
    }

    /// <summary>解析入口で確定済みの結果を索引化します。I/Oや再正規化を行いません。</summary>
    internal static ChartResourceSnapshot Create(IReadOnlyList<ChartResourceReference> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var snapshot = new ChartResourceSnapshot();
        foreach (ChartResourceReference reference in resources)
        {
            snapshot.AddReference(reference);
        }

        snapshot.PathSegmentReferenceCount = snapshot.EnumerateAllRelativePaths().Count(IsNormalizedPathAware);
        return snapshot;
    }

    /// <summary>各譜面の取得済み結果を検索集合へ集約し、重複する検索キーの全原文も保持します。未取得は拒否します。</summary>
    public static ChartResourceSnapshot CreateAggregate(IEnumerable<ChartFile> charts)
    {
        var aggregate = new ChartResourceSnapshot();
        foreach (ChartFile chart in (charts ?? []).Where(item => item != null))
        {
            foreach (ChartResourceReference reference in chart.Resources ?? throw new InvalidOperationException("Resource references have not been acquired."))
            {
                aggregate.AddReference(reference);
            }
        }
        aggregate.PathSegmentReferenceCount = aggregate.EnumerateAllRelativePaths().Count(IsNormalizedPathAware);
        return aggregate;
    }

    private void AddReference(ChartResourceReference reference)
    {
        resourceReferences.Add(reference);
        if (reference.Status != ChartResourcePathNormalizationStatus.Valid || string.IsNullOrWhiteSpace(reference.LookupKey))
        {
            return;
        }

        var key = new ResourceReference(reference.LookupKey,
            ChartResourceKeyHash.GetLookupHash(reference.LookupKey), IsNormalizedPathAware(reference.LookupKey));
        if (reference.Usage != ChartResourceUsage.Normal)
        {
            if (reference.Usage != ChartResourceUsage.InputDiagnostic)
            {
                AddResourceKey(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, optionalImageReferences, key);
            }

            return;
        }
        switch (reference.Kind)
        {
            case ChartResourceKind.Audio:
                AddResourceKey(AudioRelativePaths, AudioRelativePathHashes, AudioPathAwareRelativePaths, AudioPathAwareRelativePathHashes, audioReferences, key);
                break;
            case ChartResourceKind.Image:
                AddResourceKey(VisualRelativePaths, VisualRelativePathHashes, VisualPathAwareRelativePaths, VisualPathAwareRelativePathHashes, visualReferences, key);
                break;
            case ChartResourceKind.Movie:
                AddResourceKey(MovieRelativePaths, MovieRelativePathHashes, MoviePathAwareRelativePaths, MoviePathAwareRelativePathHashes, movieReferences, key);
                break;
        }
    }

    /// <summary>任意画像の確定した用途別参照を返します。</summary>
    internal ChartResourceReference? GetOptionalImage(ChartResourceUsage usage)
    {
        foreach (ChartResourceReference reference in resourceReferences)
        {
            if (reference.Usage == usage)
            {
                return reference;
            }
        }

        return null;
    }

    private static void AddResourceKey(ISet<string> paths, ISet<uint> hashes, ISet<string> pathAwarePaths,
        ISet<uint> pathAwareHashes, ICollection<ResourceReference> references, ResourceReference reference)
    {
        if (!paths.Add(reference.LookupKey))
        {
            return;
        }

        hashes.Add(reference.RelativePathHash);
        if (reference.IsPathAware)
        {
            pathAwarePaths.Add(reference.LookupKey);
            pathAwareHashes.Add(reference.RelativePathHash);
        }
        references.Add(reference);
    }

    private static bool IsNormalizedPathAware(string path)
    {
        return path.IndexOf(Path.DirectorySeparatorChar) >= 0 || path.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
    }
}
