using System;
using System.Collections.Generic;
using System.Linq;
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
    public void AddDir_UpdatesCachedMissAndFullWarmupLookupIncrementally()
    {
        DirectoryResourceLookupCache cache = CreateCache();

        CollectionAssert.AreEquivalent(Array.Empty<string>(), cache.GetDirectoriesByHash(99u).ToArray());

        DirectoryResourceLookupCache.ReverseLookupMutationResult cachedMissMutation = cache.AddDir(
            "C:\\Songs\\C",
            new[] { 99u },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());

        Assert.IsTrue(cachedMissMutation.Changed);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\C" }, cache.GetDirectoriesByHash(99u).ToArray());

        cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult fullMutation = cache.AddDir(
            "C:\\Songs\\D",
            new[] { 2u, 100u },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());

        Assert.IsTrue(fullMutation.MaintainedFullReverseLookup);
        Assert.IsFalse(fullMutation.RequiresDeferredWarmup);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\A", "C:\\Songs\\B", "C:\\Songs\\D" }, cache.GetDirectoriesByHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\D" }, cache.GetDirectoriesByHash(100u).ToArray());
    }

    [TestMethod]
    public void RemoveDir_AfterFullWarmup_RemovesSharedAndOwnedHashesIncrementally()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.RemoveDirWithResult("C:\\Songs\\A");

        Assert.IsTrue(mutation.MaintainedFullReverseLookup);
        Assert.IsFalse(mutation.RequiresDeferredWarmup);
        CollectionAssert.AreEquivalent(Array.Empty<string>(), cache.GetDirectoriesByHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByHash(3u).ToArray());
    }

    [TestMethod]
    public void ReplaceDir_AfterFullWarmup_ReplacesCachedDirectoryPath()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.ReplaceDirWithResult("C:\\Songs\\A", "C:\\Songs\\RenamedA");

        Assert.IsTrue(mutation.Changed);
        Assert.IsTrue(mutation.MaintainedFullReverseLookup);
        Assert.IsFalse(mutation.RequiresDeferredWarmup);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\RenamedA" }, cache.GetDirectoriesByHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\RenamedA", "C:\\Songs\\B" }, cache.GetDirectoriesByHash(2u).ToArray());
    }

    [TestMethod]
    public void RelativeReverseLookup_AddRemoveReplace_DoesNotLeaveStaleDirectories()
    {
        string dirA = @"C:\Songs\A";
        string dirB = @"C:\Songs\B";
        string dirC = @"C:\Songs\C";
        string dirRenamed = @"C:\Songs\RenamedC";
        uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"sound\bgm1.wav");
        uint imageRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"image\logo.png");
        uint movieRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"bga\movie.mpg");
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        cache.AddDir(dirA, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { audioRelativeHash }, new[] { imageRelativeHash }, Array.Empty<uint>());
        cache.AddDir(dirB, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { audioRelativeHash }, Array.Empty<uint>(), new[] { movieRelativeHash });

        cache.EnsureAudioRelativeDirectoriesByHashes(new[] { audioRelativeHash });
        cache.EnsureImageRelativeDirectoriesByHashes(new[] { imageRelativeHash });
        cache.EnsureMovieRelativeDirectoriesByHashes(new[] { movieRelativeHash });

        cache.AddDir(dirC, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { audioRelativeHash }, new[] { imageRelativeHash }, new[] { movieRelativeHash });
        cache.RemoveDirWithResult(dirA);
        cache.ReplaceDirWithResult(dirC, dirRenamed);

        CollectionAssert.AreEquivalent(new[] { dirB, dirRenamed }, cache.GetDirectoriesByAudioRelativeHash(audioRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirRenamed }, cache.GetDirectoriesByImageRelativeHash(imageRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirB, dirRenamed }, cache.GetDirectoriesByMovieRelativeHash(movieRelativeHash).ToArray());
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

    private static DirectoryResourceLookupCache CreateNativeCanonicalCache()
    {
        string dirA = "C:\\Songs\\A";
        string dirB = "C:\\Songs\\B";
        return DirectoryResourceLookupCache.CreateFromNativeCanonical(
            new[] { dirA, dirB },
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { dirA, new uint[2] { 1u, 2u } },
                { dirB, new uint[2] { 2u, 3u } }
            },
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<uint, string[]>
            {
                { 1u, new[] { dirA } },
                { 2u, new[] { dirA, dirB } },
                { 3u, new[] { dirB } }
            },
            new Dictionary<uint, string[]>(),
            new Dictionary<uint, string[]>(),
            new Dictionary<uint, string[]>());
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
        CollectionAssert.AreEquivalent(expected.SelfOwnedAllBaseNameHashArray, actual.SelfOwnedAllBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedAudioBaseNameHashArray, actual.SelfOwnedAudioBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedImageBaseNameHashArray, actual.SelfOwnedImageBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedMovieBaseNameHashArray, actual.SelfOwnedMovieBaseNameHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedAudioRelativePathHashArray, actual.SelfOwnedAudioRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedImageRelativePathHashArray, actual.SelfOwnedImageRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedMovieRelativePathHashArray, actual.SelfOwnedMovieRelativePathHashArray);
    }
}
