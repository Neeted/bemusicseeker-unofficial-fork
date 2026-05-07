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
    public void EnsureAudioRelativeDirectoriesByHashes_BuildsCategoryReverseLookup()
    {
        DirectoryResourceLookupCache cache = CreateCache();

        cache.EnsureAudioRelativeDirectoriesByHashes(new uint[3] { 2u, 3u, 2u });

        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\A", "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(3u).ToArray());
        Assert.AreEqual(2, cache.CategoryReverseLookupEntryCount);
        Assert.AreEqual(0, cache.LazyHashCacheEntryCount);
    }

    [TestMethod]
    public void AddDir_UpdatesCachedMissAndFullCategoryReverseLookupIncrementally()
    {
        DirectoryResourceLookupCache cache = CreateCache();

        CollectionAssert.AreEquivalent(Array.Empty<string>(), cache.GetDirectoriesByAudioRelativeHash(99u).ToArray());

        DirectoryResourceLookupCache.ReverseLookupMutationResult cachedMissMutation = cache.AddDir(
            "C:\\Songs\\C",
            new[] { 99u },
            Array.Empty<uint>(),
            Array.Empty<uint>());

        Assert.IsTrue(cachedMissMutation.Changed);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\C" }, cache.GetDirectoriesByAudioRelativeHash(99u).ToArray());

        cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult fullMutation = cache.AddDir(
            "C:\\Songs\\D",
            new[] { 2u, 100u },
            Array.Empty<uint>(),
            Array.Empty<uint>());

        Assert.IsTrue(fullMutation.MaintainedFullReverseLookup);
        Assert.IsFalse(fullMutation.RequiresDeferredWarmup);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\A", "C:\\Songs\\B", "C:\\Songs\\D" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\D" }, cache.GetDirectoriesByAudioRelativeHash(100u).ToArray());
    }

    [TestMethod]
    public void RemoveDir_AfterFullCategoryReverseLookup_RemovesSharedAndOwnedHashesIncrementally()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.RemoveDirWithResult("C:\\Songs\\A");

        Assert.IsTrue(mutation.MaintainedFullReverseLookup);
        Assert.IsFalse(mutation.RequiresDeferredWarmup);
        CollectionAssert.AreEquivalent(Array.Empty<string>(), cache.GetDirectoriesByAudioRelativeHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(3u).ToArray());
    }

    [TestMethod]
    public void ReplaceDir_AfterFullCategoryReverseLookup_ReplacesCachedDirectoryPath()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.ReplaceDirWithResult("C:\\Songs\\A", "C:\\Songs\\RenamedA");

        Assert.IsTrue(mutation.Changed);
        Assert.IsTrue(mutation.MaintainedFullReverseLookup);
        Assert.IsFalse(mutation.RequiresDeferredWarmup);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\RenamedA" }, cache.GetDirectoriesByAudioRelativeHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\RenamedA", "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
    }

    [TestMethod]
    public void RelativeReverseLookup_AddRemoveReplace_DoesNotLeaveStaleDirectories()
    {
        string dirA = @"C:\Songs\A";
        string dirB = @"C:\Songs\B";
        string dirC = @"C:\Songs\C";
        string dirRenamed = @"C:\Songs\RenamedC";
        uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash(@"sound\bgm1.wav");
        uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash(@"image\logo.png");
        uint movieRelativeHash = ChartResourceKeyHash.GetLookupHash(@"bga\movie.mpg");
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        cache.AddDir(dirA, new[] { audioRelativeHash }, new[] { imageRelativeHash }, Array.Empty<uint>());
        cache.AddDir(dirB, new[] { audioRelativeHash }, Array.Empty<uint>(), new[] { movieRelativeHash });

        cache.EnsureAudioRelativeDirectoriesByHashes(new[] { audioRelativeHash });
        cache.EnsureImageRelativeDirectoriesByHashes(new[] { imageRelativeHash });
        cache.EnsureMovieRelativeDirectoriesByHashes(new[] { movieRelativeHash });

        cache.AddDir(dirC, new[] { audioRelativeHash }, new[] { imageRelativeHash }, new[] { movieRelativeHash });
        cache.RemoveDirWithResult(dirA);
        cache.ReplaceDirWithResult(dirC, dirRenamed);

        CollectionAssert.AreEquivalent(new[] { dirB, dirRenamed }, cache.GetDirectoriesByAudioRelativeHash(audioRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirRenamed }, cache.GetDirectoriesByImageRelativeHash(imageRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirB, dirRenamed }, cache.GetDirectoriesByMovieRelativeHash(movieRelativeHash).ToArray());
    }

    [TestMethod]
    public void CreateFromScanResult_AndAddDirFromSameScanResult_ProduceEquivalentCategoryEntries()
    {
        string chartDir = "C:\\Songs\\Relative";
        uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\bgm1.wav");
        uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash("image\\logo.png");
        uint movieRelativeHash = ChartResourceKeyHash.GetLookupHash("bga\\logo.mpg");
        BmsScanResult scanResult = new BmsScanResult
        {
            ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDir },
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
        uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash(@"sound\bgm1.wav");
        uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash(@"image\logo.png");
        uint movieRelativeHash = ChartResourceKeyHash.GetLookupHash(@"bga\movie.mpg");
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        cache.AddDir(dirA, new[] { audioRelativeHash }, new[] { imageRelativeHash }, Array.Empty<uint>());
        cache.AddDir(dirB, Array.Empty<uint>(), Array.Empty<uint>(), new[] { movieRelativeHash });

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
            Array.Empty<uint>());
        cache.AddDir(
            "C:\\Songs\\B",
            new uint[2] { 2u, 3u },
            Array.Empty<uint>(),
            Array.Empty<uint>());
        return cache;
    }

    private static DirectoryResourceLookupCache CreateNativeCanonicalCache()
    {
        string dirA = "C:\\Songs\\A";
        string dirB = "C:\\Songs\\B";
        return DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            new[] { dirA, dirB },
            new[] { new uint[2] { 1u, 2u }, new uint[2] { 2u, 3u } },
            new[] { Array.Empty<uint>(), Array.Empty<uint>() },
            new[] { Array.Empty<uint>(), Array.Empty<uint>() },
            new[] { Array.Empty<uint>(), Array.Empty<uint>() },
            new[] { Array.Empty<uint>(), Array.Empty<uint>() },
            new[] { Array.Empty<uint>(), Array.Empty<uint>() },
            new Dictionary<uint, string[]>
            {
                { 1u, new[] { dirA } },
                { 2u, new[] { dirA, dirB } },
                { 3u, new[] { dirB } }
            },
            new Dictionary<uint, string[]>(),
            new Dictionary<uint, string[]>());
    }

    private static void AssertEntriesEqual(DirectoryResourceLookupCache.Entry expected, DirectoryResourceLookupCache.Entry actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        CollectionAssert.AreEquivalent(expected.AudioRelativePathHashArray, actual.AudioRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.ImageRelativePathHashArray, actual.ImageRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.MovieRelativePathHashArray, actual.MovieRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedAudioRelativePathHashArray, actual.SelfOwnedAudioRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedImageRelativePathHashArray, actual.SelfOwnedImageRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.SelfOwnedMovieRelativePathHashArray, actual.SelfOwnedMovieRelativePathHashArray);
    }
}
