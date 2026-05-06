using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
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

    public long BuildMs { get; private set; }

    public long FolderHashIndexMs { get; private set; }

    public long ResourceLookupMs { get; private set; }

    public string Source { get; private set; } = "managed";

    public int DirectoryCount => FolderAllFileList?.Keys.Count ?? 0;

    public static LibraryResourceIndex CreateFromScanResult(BmsScanResult scanResult)
    {
        LibraryResourceIndex index = new LibraryResourceIndex();
        Stopwatch totalStopwatch = Stopwatch.StartNew();

        Stopwatch folderStopwatch = Stopwatch.StartNew();
        index.FolderAllFileList = BMSDirectoryFileNameHash.CreateFromHashedDirectories(
            scanResult?.ChartDirectories,
            scanResult?.CreateResourceUnionHashesByChartDirectory(selfOwned: true)
                ?? scanResult?.CreateResourceUnionHashesByChartDirectory(selfOwned: false));
        folderStopwatch.Stop();
        index.FolderHashIndexMs = folderStopwatch.ElapsedMilliseconds;

        Stopwatch lookupStopwatch = Stopwatch.StartNew();
        index.DirectoryLookupCache = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        lookupStopwatch.Stop();
        index.ResourceLookupMs = lookupStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        index.BuildMs = totalStopwatch.ElapsedMilliseconds;
        return index;
    }

    public static LibraryResourceIndex CreateFromNativeCanonical(
        BmsScanResult scanResult,
        IDictionary<uint, string[]> audioRelativeReverseDirectories,
        IDictionary<uint, string[]> imageRelativeReverseDirectories,
        IDictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        LibraryResourceIndex index = new LibraryResourceIndex();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        index.Source = "native_canonical";

        Stopwatch folderStopwatch = Stopwatch.StartNew();
        index.FolderAllFileList = BMSDirectoryFileNameHash.CreateFromHashedDirectories(
            scanResult?.ChartDirectories,
            scanResult?.CreateResourceUnionHashesByChartDirectory(selfOwned: true)
                ?? scanResult?.CreateResourceUnionHashesByChartDirectory(selfOwned: false));
        folderStopwatch.Stop();
        index.FolderHashIndexMs = folderStopwatch.ElapsedMilliseconds;

        Stopwatch lookupStopwatch = Stopwatch.StartNew();
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
        lookupStopwatch.Stop();
        index.ResourceLookupMs = lookupStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        index.BuildMs = totalStopwatch.ElapsedMilliseconds;
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
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        index.Source = "native_canonical";

        Stopwatch folderStopwatch = Stopwatch.StartNew();
        index.FolderAllFileList = BMSDirectoryFileNameHash.CreateFromNativeSortedArrays(
            chartDirectories,
            CreateFolderUnionHashesByDirectoryIndex(
                selfOwnedAudioBaseNameHashesByDirectoryIndex,
                selfOwnedImageBaseNameHashesByDirectoryIndex,
                selfOwnedMovieBaseNameHashesByDirectoryIndex,
                audioBaseNameHashesByDirectoryIndex,
                imageBaseNameHashesByDirectoryIndex,
                movieBaseNameHashesByDirectoryIndex));
        folderStopwatch.Stop();
        index.FolderHashIndexMs = folderStopwatch.ElapsedMilliseconds;

        Stopwatch lookupStopwatch = Stopwatch.StartNew();
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
        lookupStopwatch.Stop();
        index.ResourceLookupMs = lookupStopwatch.ElapsedMilliseconds;

        totalStopwatch.Stop();
        index.BuildMs = totalStopwatch.ElapsedMilliseconds;
        return index;
    }

    private static uint[][] CreateFolderUnionHashesByDirectoryIndex(
        uint[][] selfAudio,
        uint[][] selfImage,
        uint[][] selfMovie,
        uint[][] audio,
        uint[][] image,
        uint[][] movie)
    {
        int count = new[]
        {
            selfAudio?.Length ?? 0,
            selfImage?.Length ?? 0,
            selfMovie?.Length ?? 0,
            audio?.Length ?? 0,
            image?.Length ?? 0,
            movie?.Length ?? 0
        }.Max();
        uint[][] result = new uint[count][];
        for (int i = 0; i < count; i++)
        {
            uint[] selfUnion = CreateSortedDistinctUnionFast(
                GetHashes(selfAudio, i),
                GetHashes(selfImage, i),
                GetHashes(selfMovie, i));
            result[i] = selfUnion.Length > 0
                ? selfUnion
                : CreateSortedDistinctUnionFast(GetHashes(audio, i), GetHashes(image, i), GetHashes(movie, i));
        }
        return result;
    }

    private static uint[] CreateSortedDistinctUnionFast(uint[] first, uint[] second, uint[] third)
    {
        first ??= Array.Empty<uint>();
        second ??= Array.Empty<uint>();
        third ??= Array.Empty<uint>();

        uint[] single = null;
        int nonEmptyCount = 0;
        if (first.Length > 0)
        {
            single = first;
            nonEmptyCount++;
        }
        if (second.Length > 0)
        {
            single = second;
            nonEmptyCount++;
        }
        if (third.Length > 0)
        {
            single = third;
            nonEmptyCount++;
        }
        if (nonEmptyCount == 0)
        {
            return Array.Empty<uint>();
        }
        if (nonEmptyCount == 1)
        {
            return single;
        }

        uint[] merged = new uint[first.Length + second.Length + third.Length];
        int cursor = 0;
        int i = 0;
        int j = 0;
        int k = 0;
        while (i < first.Length || j < second.Length || k < third.Length)
        {
            uint value = uint.MaxValue;
            if (i < first.Length && first[i] < value)
            {
                value = first[i];
            }
            if (j < second.Length && second[j] < value)
            {
                value = second[j];
            }
            if (k < third.Length && third[k] < value)
            {
                value = third[k];
            }

            if (value != 0u && (cursor == 0 || merged[cursor - 1] != value))
            {
                merged[cursor++] = value;
            }

            while (i < first.Length && first[i] == value)
            {
                i++;
            }
            while (j < second.Length && second[j] == value)
            {
                j++;
            }
            while (k < third.Length && third[k] == value)
            {
                k++;
            }
        }

        if (cursor == merged.Length)
        {
            return merged;
        }
        Array.Resize(ref merged, cursor);
        return merged;
    }

    private static uint[] GetHashes(uint[][] hashesByDirectoryIndex, int index)
    {
        if (hashesByDirectoryIndex == null || index < 0 || index >= hashesByDirectoryIndex.Length)
        {
            return Array.Empty<uint>();
        }
        return hashesByDirectoryIndex[index] ?? Array.Empty<uint>();
    }
}
