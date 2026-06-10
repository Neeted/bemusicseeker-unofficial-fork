using System;
using System.Collections.Concurrent;
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

    public bool TryGetResourceHealthCounts(string directoryPath, string resourceSetSignature, out ResourceHealthCounts counts)
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

    public void SetResourceHealthCounts(string directoryPath, string resourceSetSignature, ResourceHealthCounts counts)
    {
        if (string.IsNullOrWhiteSpace(resourceSetSignature))
        {
            return;
        }

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

    private readonly struct ResourceHealthSetCacheKey : IEquatable<ResourceHealthSetCacheKey>
    {
        private readonly string directoryPath;

        private readonly string resourceSetSignature;

        public ResourceHealthSetCacheKey(string directoryPath, string resourceSetSignature)
        {
            this.directoryPath = directoryPath ?? string.Empty;
            this.resourceSetSignature = resourceSetSignature ?? string.Empty;
        }

        public bool Equals(ResourceHealthSetCacheKey other)
        {
            return string.Equals(directoryPath, other.directoryPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(resourceSetSignature, other.resourceSetSignature, StringComparison.Ordinal);
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
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(resourceSetSignature);
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
