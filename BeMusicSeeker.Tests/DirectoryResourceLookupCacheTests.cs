using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
        Assert.AreEqual(2, cache.PrepareWarmupState());
        Assert.IsTrue(cache.WarmupVersion > warmupVersionBeforeInvalidate);
        Assert.AreEqual(0L, cache.LazyHashBuildMs);
        Assert.AreEqual(0L, cache.LazyHashLookupCount);
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
}
