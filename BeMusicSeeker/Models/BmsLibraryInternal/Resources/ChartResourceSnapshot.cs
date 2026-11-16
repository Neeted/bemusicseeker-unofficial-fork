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

    private readonly HashSet<(int Category, string Directory, string Raw)> unmatchable = new(new UnmatchableComparer());
    private int unmatchableAudio;
    private int unmatchableVisual;
    private int unmatchableMovie;
    private int unmatchableOptional;

    private sealed class UnmatchableComparer : IEqualityComparer<(int Category, string Directory, string Raw)>
    {
        public bool Equals((int Category, string Directory, string Raw) x, (int Category, string Directory, string Raw) y)
            => x.Category == y.Category && StringComparer.OrdinalIgnoreCase.Equals(x.Directory, y.Directory)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Raw, y.Raw);
        public int GetHashCode((int Category, string Directory, string Raw) value)
            => HashCode.Combine(value.Category, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Directory), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Raw));
    }

    /// <summary>同じカテゴリ・譜面相対配置・原文の非照合要求を一件として数えます。検索キーは作りません。</summary>
    internal int UnmatchableAudioReferenceCount => unmatchableAudio;
    internal int UnmatchableVisualReferenceCount => unmatchableVisual;
    internal int UnmatchableMovieReferenceCount => unmatchableMovie;

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

    public int AudioReferenceCount => AudioRelativePaths.Count + unmatchableAudio;

    public int VisualReferenceCount => VisualRelativePaths.Count + unmatchableVisual;

    public int MovieReferenceCount => MovieRelativePaths.Count + unmatchableMovie;

    public int OptionalImageReferenceCount => OptionalImageRelativePaths.Count + unmatchableOptional;

    public int TotalReferenceCount => AudioReferenceCount + VisualReferenceCount + MovieReferenceCount + OptionalImageReferenceCount;

    /// <summary>使用参照に独立した <c>..</c> セグメントがあるかを返します。未使用定義のLR2診断は全原文から別に評価します。</summary>
    public bool HasUnsupportedParentTraversalReference => resourceReferences.Any(reference => reference.RestrictsAutomaticDestination);

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

    /// <summary>解析入口で確定済みの使用参照を索引化します。発見・グループ化だけはincludeUnusedで全定義を一時取得します。I/Oや再正規化を行いません。</summary>
    internal static ChartResourceSnapshot Create(IReadOnlyList<ChartResourceReference> resources, bool includeUnused = false)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var snapshot = new ChartResourceSnapshot();
        foreach (ChartResourceReference reference in resources)
        {
            snapshot.AddReference(reference, includeUnused);
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

    /// <summary>
    /// 譜面別の取得済み参照を実導入のパッケージルート座標へ集約します。
    /// 個別の原参照・用途・解析状態は保持し、索引キーだけに譜面の相対ディレクトリを一度付けます。
    /// 未捕捉の参照を空集合へ置き換えず、I/Oや拡張子の再解析は行いません。
    /// </summary>
    internal static ChartResourceSnapshot CreatePlacedAggregate(IEnumerable<(IReadOnlyList<ChartResourceReference> Resources, string RelativeDirectory)> charts)
    {
        ArgumentNullException.ThrowIfNull(charts);
        var aggregate = new ChartResourceSnapshot();
        foreach ((IReadOnlyList<ChartResourceReference> resources, string relativeDirectory) in charts)
        {
            if (resources == null) { throw new InvalidOperationException("Resource references have not been acquired."); }
            foreach (ChartResourceReference reference in resources)
            {
                aggregate.AddReference(reference, relativeDirectory: relativeDirectory);
            }
        }
        aggregate.PathSegmentReferenceCount = aggregate.EnumerateAllRelativePaths().Count(IsNormalizedPathAware);
        return aggregate;
    }

    private void AddReference(ChartResourceReference reference, bool includeUnused = false, string relativeDirectory = null)
    {
        resourceReferences.Add(reference);
        if ((!includeUnused && !reference.IsUsed) || reference.Classification == ChartResourceClassification.Excluded) { return; }
        if (reference.Classification == ChartResourceClassification.Unmatchable)
        {
            bool optional = reference.Usage != ChartResourceUsage.Normal;
            int category = optional ? 4 : (int)reference.Kind;
            if (unmatchable.Add((category, relativeDirectory ?? string.Empty, reference.RawPath.Trim().Replace('/', '\\'))))
            {
                if (optional) { unmatchableOptional++; }
                else if (reference.Kind == ChartResourceKind.Audio) { unmatchableAudio++; }
                else if (reference.Kind == ChartResourceKind.Image) { unmatchableVisual++; }
                else if (reference.Kind == ChartResourceKind.Movie) { unmatchableMovie++; }
            }
            return;
        }

        string lookupKey = string.IsNullOrEmpty(relativeDirectory) ? reference.LookupKey : Path.Combine(relativeDirectory, reference.LookupKey);
        var key = new ResourceReference(lookupKey,
            ChartResourceKeyHash.GetLookupHash(lookupKey), IsNormalizedPathAware(lookupKey));
        if (reference.Usage != ChartResourceUsage.Normal)
        {
            AddResourceKey(OptionalImageRelativePaths, OptionalImageRelativePathHashes, OptionalImagePathAwareRelativePaths, OptionalImagePathAwareRelativePathHashes, optionalImageReferences, key);

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
