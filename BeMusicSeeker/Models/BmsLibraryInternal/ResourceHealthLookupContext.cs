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

internal sealed class ResourceHealthLookupContext(DirectoryResourceLookupCache directoryLookupCache)
{
    private readonly ConcurrentDictionary<ResourceHealthCacheKey, bool> cacheResolvedResourceExistsByKey = new();

    private long cacheHitCount;

    private long fileExistsFallbackCount;

    private long audioFileExistsFallbackCount;

    private long imageFileExistsFallbackCount;

    private long movieFileExistsFallbackCount;

    private long optionalImageFileExistsFallbackCount;

    private long unknownFileExistsFallbackCount;

    public DirectoryResourceLookupCache DirectoryLookupCache { get; } = directoryLookupCache;

    public int SharedResourceCacheEntryCount => cacheResolvedResourceExistsByKey.Count;

    public long CacheHitCount => Interlocked.Read(ref cacheHitCount);

    public long FileExistsFallbackCount => Interlocked.Read(ref fileExistsFallbackCount);

    public long AudioFileExistsFallbackCount => Interlocked.Read(ref audioFileExistsFallbackCount);

    public long ImageFileExistsFallbackCount => Interlocked.Read(ref imageFileExistsFallbackCount);

    public long MovieFileExistsFallbackCount => Interlocked.Read(ref movieFileExistsFallbackCount);

    public long OptionalImageFileExistsFallbackCount => Interlocked.Read(ref optionalImageFileExistsFallbackCount);

    public long UnknownFileExistsFallbackCount => Interlocked.Read(ref unknownFileExistsFallbackCount);

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

    public void RecordCacheHit()
    {
        Interlocked.Increment(ref cacheHitCount);
    }

    public void AddCacheHits(long count)
    {
        if (count > 0L)
        {
            Interlocked.Add(ref cacheHitCount, count);
        }
    }

    public void AddCounters(ResourceHealthLookupContext source)
    {
        if (source == null)
        {
            return;
        }
        AddCacheHits(source.CacheHitCount);
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
