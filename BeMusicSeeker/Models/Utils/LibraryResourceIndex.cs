using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// Canonical resource index built from one library file enumeration.
/// Phase 3A keeps legacy views inside this type so production code has a single build boundary.
/// </summary>
internal sealed class LibraryResourceIndex
{
    public BMSDirectoryFileNameHash FolderAllFileList { get; private set; } = new BMSDirectoryFileNameHash();

    public DirectoryResourceLookupCache DirectoryLookupCache { get; private set; } = new DirectoryResourceLookupCache();

    public DirectoryRelativePathHashIndex RelativePathHashIndex { get; private set; } = new DirectoryRelativePathHashIndex();

    public long BuildMs { get; private set; }

    public long FolderHashIndexMs { get; private set; }

    public long ResourceLookupMs { get; private set; }

    public long RelativePathIndexMs { get; private set; }

    public int DirectoryCount => FolderAllFileList?.Keys.Count ?? 0;

    public static LibraryResourceIndex CreateFromScanResult(BmsScanResult scanResult)
    {
        LibraryResourceIndex index = new LibraryResourceIndex();
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        Stopwatch folderStopwatch = Stopwatch.StartNew();
        index.FolderAllFileList = BMSDirectoryFileNameHash.CreateFromHashedDirectories(
            scanResult?.ChartDirectories,
            (scanResult?.SelfOwnedAllResourceBaseNameHashesByChartDirectory?.Count ?? 0) > 0
                ? scanResult.SelfOwnedAllResourceBaseNameHashesByChartDirectory
                : scanResult?.AllResourceBaseNameHashesByChartDirectory);
        folderStopwatch.Stop();
        index.FolderHashIndexMs = folderStopwatch.ElapsedMilliseconds;

        Stopwatch lookupStopwatch = Stopwatch.StartNew();
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        lookupStopwatch.Stop();
        index.ResourceLookupMs = lookupStopwatch.ElapsedMilliseconds;

        Stopwatch relativeStopwatch = Stopwatch.StartNew();
        index.RelativePathHashIndex = DirectoryRelativePathHashIndex.CreateFromScanResult(scanResult);
        relativeStopwatch.Stop();
        index.RelativePathIndexMs = relativeStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        index.BuildMs = totalStopwatch.ElapsedMilliseconds;
        return index;
    }
}
