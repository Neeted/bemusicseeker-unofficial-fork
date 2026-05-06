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
    private long cacheHitCount;

    private long fileExistsFallbackCount;

    private long audioFileExistsFallbackCount;

    private long imageFileExistsFallbackCount;

    private long movieFileExistsFallbackCount;

    private long optionalImageFileExistsFallbackCount;

    private long unknownFileExistsFallbackCount;

    public ResourceHealthLookupContext(DirectoryResourceLookupCache directoryLookupCache)
    {
        DirectoryLookupCache = directoryLookupCache;
    }

    public DirectoryResourceLookupCache DirectoryLookupCache { get; }

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
}
