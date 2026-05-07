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
        LibraryResourceIndex index = new LibraryResourceIndex();
        Stopwatch stopwatch = Stopwatch.StartNew();
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        stopwatch.Stop();
        index.ResourceLookupMs = stopwatch.ElapsedMilliseconds;
        index.BuildMs = stopwatch.ElapsedMilliseconds;
        return index;
    }

    public static LibraryResourceIndex CreateFromNativeCanonical(
        BmsScanResult scanResult,
        IDictionary<uint, string[]> audioRelativeReverseDirectories,
        IDictionary<uint, string[]> imageRelativeReverseDirectories,
        IDictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        LibraryResourceIndex index = new LibraryResourceIndex();
        Stopwatch stopwatch = Stopwatch.StartNew();
        index.Source = "native_canonical";
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromNativeCanonical(
            scanResult?.ChartDirectories,
            scanResult?.AudioBaseNameHashesByChartDirectory,
            scanResult?.ImageBaseNameHashesByChartDirectory,
            scanResult?.MovieBaseNameHashesByChartDirectory,
            scanResult?.AudioRelativePathHashesByChartDirectory,
            scanResult?.ImageRelativePathHashesByChartDirectory,
            scanResult?.MovieRelativePathHashesByChartDirectory,
            scanResult?.SelfOwnedAudioBaseNameHashesByChartDirectory,
            scanResult?.SelfOwnedImageBaseNameHashesByChartDirectory,
            scanResult?.SelfOwnedMovieBaseNameHashesByChartDirectory,
            scanResult?.SelfOwnedAudioRelativePathHashesByChartDirectory,
            scanResult?.SelfOwnedImageRelativePathHashesByChartDirectory,
            scanResult?.SelfOwnedMovieRelativePathHashesByChartDirectory,
            audioRelativeReverseDirectories,
            imageRelativeReverseDirectories,
            movieRelativeReverseDirectories);
        stopwatch.Stop();
        index.ResourceLookupMs = stopwatch.ElapsedMilliseconds;
        index.BuildMs = stopwatch.ElapsedMilliseconds;
        return index;
    }

    public static LibraryResourceIndex CreateFromNativeCanonicalArrays(
        string[] chartDirectories,
        uint[][] audioBaseNameHashesByDirectoryIndex,
        uint[][] imageBaseNameHashesByDirectoryIndex,
        uint[][] movieBaseNameHashesByDirectoryIndex,
        uint[][] audioRelativePathHashesByDirectoryIndex,
        uint[][] imageRelativePathHashesByDirectoryIndex,
        uint[][] movieRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedAudioBaseNameHashesByDirectoryIndex,
        uint[][] selfOwnedImageBaseNameHashesByDirectoryIndex,
        uint[][] selfOwnedMovieBaseNameHashesByDirectoryIndex,
        uint[][] selfOwnedAudioRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedImageRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedMovieRelativePathHashesByDirectoryIndex,
        Dictionary<uint, string[]> audioRelativeReverseDirectories,
        Dictionary<uint, string[]> imageRelativeReverseDirectories,
        Dictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        LibraryResourceIndex index = new LibraryResourceIndex();
        Stopwatch stopwatch = Stopwatch.StartNew();
        index.Source = "native_canonical";
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            chartDirectories,
            audioBaseNameHashesByDirectoryIndex,
            imageBaseNameHashesByDirectoryIndex,
            movieBaseNameHashesByDirectoryIndex,
            audioRelativePathHashesByDirectoryIndex,
            imageRelativePathHashesByDirectoryIndex,
            movieRelativePathHashesByDirectoryIndex,
            selfOwnedAudioBaseNameHashesByDirectoryIndex,
            selfOwnedImageBaseNameHashesByDirectoryIndex,
            selfOwnedMovieBaseNameHashesByDirectoryIndex,
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
