using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DirectoryResourceLookupCacheTests
{
    [TestMethod]
    public void EnsureDirectoriesByHashes_BuildsRequestedHashesOnceAndCachesResults()
    {
        DirectoryResourceLookupCache cache = CreateCache();

        Assert.AreEqual(0, cache.LazyHashCacheEntryCount);

        cache.EnsureDirectoriesByHashes(new uint[3] { 2u, 3u, 2u });

        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\A", "C:\\Songs\\B" }, cache.GetDirectoriesByHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByHash(3u).ToArray());
        Assert.AreEqual(2, cache.LazyHashCacheEntryCount);
        Assert.AreEqual(2L, cache.LazyHashLookupCount);
        Assert.IsTrue(cache.LazyHashBuildMs >= 0L);
    }

    [TestMethod]
    public void WarmupReverseLookupStep_BuildsRemainingHashesAndInvalidationResetsState()
    {
        DirectoryResourceLookupCache cache = CreateCache();

        cache.EnsureDirectoriesByHashes(new uint[1] { 1u });
        Assert.AreEqual(1, cache.LazyHashCacheEntryCount);

        int totalEntries = cache.PrepareWarmupState();
        Assert.AreEqual(2, totalEntries);

        DirectoryResourceLookupCache.ReverseLookupWarmupStepResult firstStep = cache.WarmupReverseLookupStep(1, 1000, CancellationToken.None);

        Assert.AreEqual(1, firstStep.ChunkEntryCount);
        Assert.AreEqual(1, firstStep.ProcessedEntryCount);
        Assert.AreEqual(2, firstStep.TotalEntryCount);
        Assert.AreEqual(2, firstStep.BuiltHashCount);
        Assert.IsFalse(firstStep.Completed);
        Assert.AreEqual(1, cache.LazyHashCacheEntryCount);

        DirectoryResourceLookupCache.ReverseLookupWarmupStepResult secondStep = cache.WarmupReverseLookupStep(10, 1000, CancellationToken.None);

        Assert.AreEqual(1, secondStep.ChunkEntryCount);
        Assert.AreEqual(2, secondStep.ProcessedEntryCount);
        Assert.AreEqual(2, secondStep.TotalEntryCount);
        Assert.AreEqual(3, secondStep.BuiltHashCount);
        Assert.IsTrue(secondStep.Completed);
        Assert.AreEqual(3, cache.LazyHashCacheEntryCount);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\A", "C:\\Songs\\B" }, cache.GetDirectoriesByHash(2u).ToArray());

        int warmupVersionBeforeInvalidate = cache.WarmupVersion;
        bool removed = cache.RemoveDir("C:\\Songs\\A");

        Assert.IsTrue(removed);
        Assert.AreEqual(0, cache.LazyHashCacheEntryCount);
        Assert.AreEqual(1, cache.PrepareWarmupState());
        Assert.IsTrue(cache.WarmupVersion > warmupVersionBeforeInvalidate);
        Assert.AreEqual(0L, cache.LazyHashBuildMs);
        Assert.AreEqual(0L, cache.LazyHashLookupCount);
    }

    [TestMethod]
    public void CreateFromScanResult_AndAddDirFromSameScanResult_ProduceEquivalentEntriesIncludingRelativeHashes()
    {
        string chartDir = "C:\\Songs\\Relative";
        uint audioBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav");
        uint imageBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("logo.bmp");
        uint movieBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("logo.mpg");
        uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\bgm1.wav");
        uint imageRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("image\\logo.png");
        uint movieRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("bga\\logo.mpg");
        BmsScanResult scanResult = new BmsScanResult
        {
            ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDir },
            AllResourceBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { audioBaseHash, imageBaseHash, movieBaseHash } }
            },
            AudioBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { audioBaseHash } }
            },
            ImageBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { imageBaseHash } }
            },
            MovieBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { movieBaseHash } }
            },
            AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { audioRelativeHash } }
            },
            ImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { imageRelativeHash } }
            },
            MovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { movieRelativeHash } }
            }
        };

        DirectoryResourceLookupCache fromCreate = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        DirectoryResourceLookupCache fromAdd = new DirectoryResourceLookupCache();
        fromAdd.AddDir(chartDir, scanResult);

        AssertEntriesEqual(fromCreate.GetEntryOrNull(chartDir), fromAdd.GetEntryOrNull(chartDir));
    }

    [TestMethod]
    public void EnsureRelativeDirectoriesByHashes_BuildsCategorySpecificReverseLookups()
    {
        string dirA = @"C:\Songs\A";
        string dirB = @"C:\Songs\B";
        uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"sound\bgm1.wav");
        uint imageRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"image\logo.png");
        uint movieRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"bga\movie.mpg");
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        cache.AddDir(dirA, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { audioRelativeHash }, new[] { imageRelativeHash }, Array.Empty<uint>());
        cache.AddDir(dirB, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { movieRelativeHash });

        cache.EnsureAudioRelativeDirectoriesByHashes(new[] { audioRelativeHash });
        cache.EnsureImageRelativeDirectoriesByHashes(new[] { imageRelativeHash });
        cache.EnsureMovieRelativeDirectoriesByHashes(new[] { movieRelativeHash });

        CollectionAssert.AreEquivalent(new[] { dirA }, cache.GetDirectoriesByAudioRelativeHash(audioRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirA }, cache.GetDirectoriesByImageRelativeHash(imageRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirB }, cache.GetDirectoriesByMovieRelativeHash(movieRelativeHash).ToArray());
    }

    private static DirectoryResourceLookupCache CreateCache()
    {
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        cache.AddDir(
            "C:\\Songs\\A",
            new uint[2] { 1u, 2u },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());
        cache.AddDir(
            "C:\\Songs\\B",
            new uint[2] { 2u, 3u },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());
        return cache;
    }

    private static void AssertEntriesEqual(DirectoryResourceLookupCache.Entry expected, DirectoryResourceLookupCache.Entry actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        CollectionAssert.AreEquivalent(expected.AllBaseNameHashArray, actual.AllBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.AudioBaseNameHashArray, actual.AudioBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.ImageBaseNameHashArray, actual.ImageBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.MovieBaseNameHashArray, actual.MovieBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.AudioRelativePathHashArray, actual.AudioRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.ImageRelativePathHashArray, actual.ImageRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.MovieRelativePathHashArray, actual.MovieRelativePathHashArray);
    }
}
