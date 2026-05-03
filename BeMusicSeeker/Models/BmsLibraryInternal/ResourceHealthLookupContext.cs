using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthLookupContext
{
    private long cacheHitCount;

    private long fileExistsFallbackCount;

    public ResourceHealthLookupContext(
        BMSDirectoryFileNameHash folderAllFileList,
        DirectoryResourceLookupCache directoryLookupCache,
        DirectoryRelativePathHashIndex relativePathHashIndex)
    {
        FolderAllFileList = folderAllFileList;
        DirectoryLookupCache = directoryLookupCache;
        RelativePathHashIndex = relativePathHashIndex;
    }

    public BMSDirectoryFileNameHash FolderAllFileList { get; }

    public DirectoryResourceLookupCache DirectoryLookupCache { get; }

    public DirectoryRelativePathHashIndex RelativePathHashIndex { get; }

    public long CacheHitCount => Interlocked.Read(ref cacheHitCount);

    public long FileExistsFallbackCount => Interlocked.Read(ref fileExistsFallbackCount);

    public DirectoryResourceLookupCache.Entry GetResourceEntryOrNull(string directoryPath)
    {
        return DirectoryLookupCache?.GetEntryOrNull(directoryPath);
    }

    public DirectoryRelativePathHashIndex.Entry GetRelativePathEntryOrNull(string directoryPath)
    {
        return RelativePathHashIndex?.GetEntryOrNull(directoryPath);
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
        Interlocked.Increment(ref fileExistsFallbackCount);
    }

    public void AddFileExistsFallbacks(long count)
    {
        if (count > 0L)
        {
            Interlocked.Add(ref fileExistsFallbackCount, count);
        }
    }
}
