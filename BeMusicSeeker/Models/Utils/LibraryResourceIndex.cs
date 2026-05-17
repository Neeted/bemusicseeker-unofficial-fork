using System.Collections.Generic;
using System.Diagnostics;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// Canonical resource index built from one library file enumeration.
/// </summary>
internal sealed class LibraryResourceIndex
{
    public DirectoryResourceLookupCache DirectoryLookupCache { get; private set; } = new DirectoryResourceLookupCache();

    public long BuildMs { get; private set; }

    public long ResourceLookupMs { get; private set; }

    public string Source { get; private set; } = "managed";

    public int DirectoryCount => DirectoryLookupCache?.Count ?? 0;

    public static LibraryResourceIndex CreateFromScanResult(BmsScanResult scanResult)
    {
        var index = new LibraryResourceIndex();
        var stopwatch = Stopwatch.StartNew();
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        stopwatch.Stop();
        index.ResourceLookupMs = stopwatch.ElapsedMilliseconds;
        index.BuildMs = stopwatch.ElapsedMilliseconds;
        return index;
    }

    public static LibraryResourceIndex CreateFromNativeCanonicalArrays(
        string[] chartDirectories,
        uint[][] audioRelativePathHashesByDirectoryIndex,
        uint[][] imageRelativePathHashesByDirectoryIndex,
        uint[][] movieRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedAudioRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedImageRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedMovieRelativePathHashesByDirectoryIndex,
        Dictionary<uint, string[]> audioRelativeReverseDirectories,
        Dictionary<uint, string[]> imageRelativeReverseDirectories,
        Dictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        var index = new LibraryResourceIndex();
        var stopwatch = Stopwatch.StartNew();
        index.Source = "native_canonical";
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            chartDirectories,
            audioRelativePathHashesByDirectoryIndex,
            imageRelativePathHashesByDirectoryIndex,
            movieRelativePathHashesByDirectoryIndex,
            selfOwnedAudioRelativePathHashesByDirectoryIndex,
            selfOwnedImageRelativePathHashesByDirectoryIndex,
            selfOwnedMovieRelativePathHashesByDirectoryIndex,
            audioRelativeReverseDirectories,
            imageRelativeReverseDirectories,
            movieRelativeReverseDirectories);
        stopwatch.Stop();
        index.ResourceLookupMs = stopwatch.ElapsedMilliseconds;
        index.BuildMs = stopwatch.ElapsedMilliseconds;
        return index;
    }
}
