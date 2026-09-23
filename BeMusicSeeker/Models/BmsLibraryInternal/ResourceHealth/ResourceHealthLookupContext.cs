using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum ResourceHealthFallbackKind
{
    Audio,
    Image,
    Movie,
    OptionalImage,
    Unknown
}

internal sealed class ResourceHealthLookupContext
{
    private readonly ConcurrentDictionary<ResourceHealthCacheKey, bool> cacheResolvedResourceExistsByKey;

    private readonly ConcurrentDictionary<ResourceHealthSetCacheKey, ResourceHealthCounts> resourceHealthCountsByKey;

    private long cacheHitCount;

    private long resourceIndexHitCount;

    private long resourceHealthSetCacheHitCount;

    private long fileExistsFallbackCount;

    private long audioFileExistsFallbackCount;

    private long imageFileExistsFallbackCount;

    private long movieFileExistsFallbackCount;

    private long optionalImageFileExistsFallbackCount;

    private long unknownFileExistsFallbackCount;

    public ResourceHealthLookupContext(DirectoryResourceLookupCache directoryLookupCache)
        : this(
            directoryLookupCache,
            new ConcurrentDictionary<ResourceHealthCacheKey, bool>(),
            new ConcurrentDictionary<ResourceHealthSetCacheKey, ResourceHealthCounts>())
    {
    }

    private ResourceHealthLookupContext(
        DirectoryResourceLookupCache directoryLookupCache,
        ConcurrentDictionary<ResourceHealthCacheKey, bool> cacheResolvedResourceExistsByKey,
        ConcurrentDictionary<ResourceHealthSetCacheKey, ResourceHealthCounts> resourceHealthCountsByKey)
    {
        DirectoryLookupCache = directoryLookupCache;
        this.cacheResolvedResourceExistsByKey = cacheResolvedResourceExistsByKey ?? new ConcurrentDictionary<ResourceHealthCacheKey, bool>();
        this.resourceHealthCountsByKey = resourceHealthCountsByKey ?? new ConcurrentDictionary<ResourceHealthSetCacheKey, ResourceHealthCounts>();
    }

    public DirectoryResourceLookupCache DirectoryLookupCache { get; }

    public int SharedResourceCacheEntryCount => cacheResolvedResourceExistsByKey.Count;

    public int ResourceHealthSetCacheEntryCount => resourceHealthCountsByKey.Count;

    public long CacheHitCount => Interlocked.Read(ref cacheHitCount);

    public long ResourceIndexHitCount => Interlocked.Read(ref resourceIndexHitCount);

    public long ResourceHealthSetCacheHitCount => Interlocked.Read(ref resourceHealthSetCacheHitCount);

    public long FileExistsFallbackCount => Interlocked.Read(ref fileExistsFallbackCount);

    public long AudioFileExistsFallbackCount => Interlocked.Read(ref audioFileExistsFallbackCount);

    public long ImageFileExistsFallbackCount => Interlocked.Read(ref imageFileExistsFallbackCount);

    public long MovieFileExistsFallbackCount => Interlocked.Read(ref movieFileExistsFallbackCount);

    public long OptionalImageFileExistsFallbackCount => Interlocked.Read(ref optionalImageFileExistsFallbackCount);

    public long UnknownFileExistsFallbackCount => Interlocked.Read(ref unknownFileExistsFallbackCount);

    public ResourceHealthLookupContext CreateCounterScope()
    {
        return new ResourceHealthLookupContext(DirectoryLookupCache, cacheResolvedResourceExistsByKey, resourceHealthCountsByKey);
    }

    public DirectoryResourceLookupCache.Entry GetResourceEntryOrNull(string directoryPath)
    {
        return DirectoryLookupCache?.GetEntryOrNull(directoryPath);
    }

    public bool TryGetSharedResourceExists(
        string directoryPath,
        ChartResourceKind resourceKind,
        uint relativePathHash,
        out bool exists)
    {
        exists = false;
        return relativePathHash != 0u
            && cacheResolvedResourceExistsByKey.TryGetValue(
                new ResourceHealthCacheKey(directoryPath, resourceKind, relativePathHash),
                out exists);
    }

    public void SetSharedResourceExists(
        string directoryPath,
        ChartResourceKind resourceKind,
        uint relativePathHash,
        bool exists)
    {
        if (relativePathHash == 0u)
        {
            return;
        }

        cacheResolvedResourceExistsByKey.TryAdd(
            new ResourceHealthCacheKey(directoryPath, resourceKind, relativePathHash),
            exists);
    }

    public bool TryGetResourceHealthCounts(string directoryPath, ResourceHealthSetSignature resourceSetSignature, out ResourceHealthCounts counts)
    {
        bool found = resourceHealthCountsByKey.TryGetValue(
            new ResourceHealthSetCacheKey(directoryPath, resourceSetSignature),
            out counts);
        if (found)
        {
            Interlocked.Increment(ref resourceHealthSetCacheHitCount);
        }
        return found;
    }

    public void SetResourceHealthCounts(string directoryPath, ResourceHealthSetSignature resourceSetSignature, ResourceHealthCounts counts)
    {
        resourceHealthCountsByKey.TryAdd(
            new ResourceHealthSetCacheKey(directoryPath, resourceSetSignature),
            counts);
    }

    public void RecordCacheHit()
    {
        Interlocked.Increment(ref cacheHitCount);
    }

    public void RecordResourceIndexHit()
    {
        Interlocked.Increment(ref cacheHitCount);
        Interlocked.Increment(ref resourceIndexHitCount);
    }

    public void AddCacheHits(long count)
    {
        if (count > 0L)
        {
            Interlocked.Add(ref cacheHitCount, count);
        }
    }

    public void AddResourceIndexHits(long count)
    {
        if (count > 0L)
        {
            Interlocked.Add(ref resourceIndexHitCount, count);
        }
    }

    public void AddResourceHealthSetCacheHits(long count)
    {
        if (count > 0L)
        {
            Interlocked.Add(ref resourceHealthSetCacheHitCount, count);
        }
    }

    public void AddCounters(ResourceHealthLookupContext source)
    {
        if (source == null)
        {
            return;
        }
        AddCacheHits(source.CacheHitCount);
        AddResourceIndexHits(source.ResourceIndexHitCount);
        AddResourceHealthSetCacheHits(source.ResourceHealthSetCacheHitCount);
        AddFileExistsFallbacks(ResourceHealthFallbackKind.Audio, source.AudioFileExistsFallbackCount);
        AddFileExistsFallbacks(ResourceHealthFallbackKind.Image, source.ImageFileExistsFallbackCount);
        AddFileExistsFallbacks(ResourceHealthFallbackKind.Movie, source.MovieFileExistsFallbackCount);
        AddFileExistsFallbacks(ResourceHealthFallbackKind.OptionalImage, source.OptionalImageFileExistsFallbackCount);
        AddFileExistsFallbacks(ResourceHealthFallbackKind.Unknown, source.UnknownFileExistsFallbackCount);
    }

    public void RecordFileExistsFallback()
    {
        RecordFileExistsFallback(ResourceHealthFallbackKind.Unknown);
    }

    public void RecordFileExistsFallback(ResourceHealthFallbackKind kind)
    {
        Interlocked.Increment(ref fileExistsFallbackCount);
        switch (kind)
        {
            case ResourceHealthFallbackKind.Audio:
                Interlocked.Increment(ref audioFileExistsFallbackCount);
                break;
            case ResourceHealthFallbackKind.Image:
                Interlocked.Increment(ref imageFileExistsFallbackCount);
                break;
            case ResourceHealthFallbackKind.Movie:
                Interlocked.Increment(ref movieFileExistsFallbackCount);
                break;
            case ResourceHealthFallbackKind.OptionalImage:
                Interlocked.Increment(ref optionalImageFileExistsFallbackCount);
                break;
            default:
                Interlocked.Increment(ref unknownFileExistsFallbackCount);
                break;
        }
    }

    public void AddFileExistsFallbacks(long count)
    {
        if (count > 0L)
        {
            Interlocked.Add(ref fileExistsFallbackCount, count);
            Interlocked.Add(ref unknownFileExistsFallbackCount, count);
        }
    }

    public void AddFileExistsFallbacks(ResourceHealthFallbackKind kind, long count)
    {
        if (count <= 0L)
        {
            return;
        }
        Interlocked.Add(ref fileExistsFallbackCount, count);
        switch (kind)
        {
            case ResourceHealthFallbackKind.Audio:
                Interlocked.Add(ref audioFileExistsFallbackCount, count);
                break;
            case ResourceHealthFallbackKind.Image:
                Interlocked.Add(ref imageFileExistsFallbackCount, count);
                break;
            case ResourceHealthFallbackKind.Movie:
                Interlocked.Add(ref movieFileExistsFallbackCount, count);
                break;
            case ResourceHealthFallbackKind.OptionalImage:
                Interlocked.Add(ref optionalImageFileExistsFallbackCount, count);
                break;
            default:
                Interlocked.Add(ref unknownFileExistsFallbackCount, count);
                break;
        }
    }

    internal readonly struct ResourceHealthCounts(
        int audioDefined,
        int audioExisting,
        int visualDefined,
        int visualExisting,
        int movieDefined,
        int movieExisting,
        bool stagefileDefined,
        bool stagefileExisting,
        bool backbmpDefined,
        bool backbmpExisting,
        bool bannerDefined,
        bool bannerExisting)
    {
        public int AudioDefined { get; } = audioDefined;

        public int AudioExisting { get; } = audioExisting;

        public int VisualDefined { get; } = visualDefined;

        public int VisualExisting { get; } = visualExisting;

        public int MovieDefined { get; } = movieDefined;

        public int MovieExisting { get; } = movieExisting;

        public bool StagefileDefined { get; } = stagefileDefined;

        public bool StagefileExisting { get; } = stagefileExisting;

        public bool BackbmpDefined { get; } = backbmpDefined;

        public bool BackbmpExisting { get; } = backbmpExisting;

        public bool BannerDefined { get; } = bannerDefined;

        public bool BannerExisting { get; } = bannerExisting;
    }

    internal readonly struct ResourceHealthSetSignature : IEquatable<ResourceHealthSetSignature>
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        private readonly string[] audioPaths;

        private readonly string[] imagePaths;

        private readonly string[] moviePaths;

        private readonly string stagefilePath;

        private readonly string backbmpPath;

        private readonly string bannerPath;

        private readonly int hashCode;

        public ResourceHealthSetSignature(
            IEnumerable<string> audioPaths,
            IEnumerable<string> imagePaths,
            IEnumerable<string> moviePaths,
            string stagefilePath,
            string backbmpPath,
            string bannerPath)
        {
            this.audioPaths = MaterializeSortedPaths(audioPaths);
            this.imagePaths = MaterializeSortedPaths(imagePaths);
            this.moviePaths = MaterializeSortedPaths(moviePaths);
            this.stagefilePath = stagefilePath ?? string.Empty;
            this.backbmpPath = backbmpPath ?? string.Empty;
            this.bannerPath = bannerPath ?? string.Empty;
            hashCode = ComputeHashCode(
                this.audioPaths,
                this.imagePaths,
                this.moviePaths,
                this.stagefilePath,
                this.backbmpPath,
                this.bannerPath);
        }

        public bool Equals(ResourceHealthSetSignature other)
        {
            return PathComparer.Equals(stagefilePath ?? string.Empty, other.stagefilePath ?? string.Empty)
                && PathComparer.Equals(backbmpPath ?? string.Empty, other.backbmpPath ?? string.Empty)
                && PathComparer.Equals(bannerPath ?? string.Empty, other.bannerPath ?? string.Empty)
                && PathsEqual(audioPaths, other.audioPaths)
                && PathsEqual(imagePaths, other.imagePaths)
                && PathsEqual(moviePaths, other.moviePaths);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceHealthSetSignature other && Equals(other);
        }

        public override int GetHashCode()
        {
            return hashCode;
        }

        private static string[] MaterializeSortedPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return [];
            }
            List<string> pathList = [];
            foreach (string path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    pathList.Add(path);
                }
            }
            if (pathList.Count == 0)
            {
                return [];
            }
            string[] pathArray = [.. pathList.Distinct(PathComparer)];
            if (pathArray.Length > 1)
            {
                Array.Sort(pathArray, PathComparer);
            }
            return pathArray;
        }

        private static bool PathsEqual(string[] left, string[] right)
        {
            left ??= [];
            right ??= [];
            if (left.Length != right.Length)
            {
                return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                if (!PathComparer.Equals(left[i], right[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static int ComputeHashCode(
            string[] audioPaths,
            string[] imagePaths,
            string[] moviePaths,
            string stagefilePath,
            string backbmpPath,
            string bannerPath)
        {
            unchecked
            {
                int hash = 0;
                hash = CombinePaths(hash, audioPaths);
                hash = CombinePaths(hash, imagePaths);
                hash = CombinePaths(hash, moviePaths);
                hash = CombinePath(hash, stagefilePath);
                hash = CombinePath(hash, backbmpPath);
                hash = CombinePath(hash, bannerPath);
                return hash;
            }
        }

        private static int CombinePaths(int hash, string[] paths)
        {
            unchecked
            {
                hash = (hash * 397) ^ (paths?.Length ?? 0);
                foreach (string path in paths ?? [])
                {
                    hash = CombinePath(hash, path);
                }
                return hash;
            }
        }

        private static int CombinePath(int hash, string path)
        {
            unchecked
            {
                return (hash * 397) ^ (string.IsNullOrEmpty(path) ? 0 : PathComparer.GetHashCode(path));
            }
        }
    }

    private readonly struct ResourceHealthSetCacheKey : IEquatable<ResourceHealthSetCacheKey>
    {
        private readonly string directoryPath;

        private readonly ResourceHealthSetSignature resourceSetSignature;

        public ResourceHealthSetCacheKey(string directoryPath, ResourceHealthSetSignature resourceSetSignature)
        {
            this.directoryPath = directoryPath ?? string.Empty;
            this.resourceSetSignature = resourceSetSignature;
        }

        public bool Equals(ResourceHealthSetCacheKey other)
        {
            return string.Equals(directoryPath, other.directoryPath, StringComparison.OrdinalIgnoreCase)
                && resourceSetSignature.Equals(other.resourceSetSignature);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceHealthSetCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(directoryPath);
                hash = (hash * 397) ^ resourceSetSignature.GetHashCode();
                return hash;
            }
        }
    }

    private readonly struct ResourceHealthCacheKey : IEquatable<ResourceHealthCacheKey>
    {
        private readonly string directoryPath;

        private readonly ChartResourceKind resourceKind;

        private readonly uint relativePathHash;

        public ResourceHealthCacheKey(string directoryPath, ChartResourceKind resourceKind, uint relativePathHash)
        {
            this.directoryPath = directoryPath ?? string.Empty;
            this.resourceKind = resourceKind;
            this.relativePathHash = relativePathHash;
        }

        public bool Equals(ResourceHealthCacheKey other)
        {
            return relativePathHash == other.relativePathHash
                && resourceKind == other.resourceKind
                && string.Equals(directoryPath, other.directoryPath, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is ResourceHealthCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(directoryPath);
                hash = (hash * 397) ^ (int)resourceKind;
                hash = (hash * 397) ^ (int)relativePathHash;
                return hash;
            }
        }
    }
}
