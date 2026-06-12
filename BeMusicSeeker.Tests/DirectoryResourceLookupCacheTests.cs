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

        cache.EnsureAudioRelativeDirectoriesByHashes([2u, 3u, 2u]);

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
            [99u],
            [],
            []);

        Assert.IsTrue(cachedMissMutation.Changed);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\C" }, cache.GetDirectoriesByAudioRelativeHash(99u).ToArray());

        cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult fullMutation = cache.AddDir(
            "C:\\Songs\\D",
            [2u, 100u],
            [],
            []);

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
    public void ReplaceDirsWithResult_AfterFullCategoryReverseLookup_RewritesDirectoryPathsInBulk()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.ReplaceDirsWithResult(
        [
            new KeyValuePair<string, string>("C:\\Songs\\A", "C:\\Renamed\\A"),
            new KeyValuePair<string, string>("C:\\Songs\\B", "C:\\Renamed\\B")
        ]);

        Assert.IsTrue(mutation.Changed);
        Assert.IsTrue(mutation.MaintainedFullReverseLookup);
        Assert.IsFalse(mutation.RequiresDeferredWarmup);
        Assert.AreEqual(2, mutation.ReplacedDirectoryCount);
        CollectionAssert.AreEquivalent(new[] { "C:\\Renamed\\A" }, cache.GetDirectoriesByAudioRelativeHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Renamed\\A", "C:\\Renamed\\B" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Renamed\\B" }, cache.GetDirectoriesByAudioRelativeHash(3u).ToArray());
        Assert.IsNull(cache.GetEntryOrNull("C:\\Songs\\A"));
        Assert.IsNull(cache.GetEntryOrNull("C:\\Songs\\B"));
        Assert.IsNotNull(cache.GetEntryOrNull("C:\\Renamed\\A"));
        Assert.IsNotNull(cache.GetEntryOrNull("C:\\Renamed\\B"));
    }

    [TestMethod]
    public void ReplaceDirsWithResult_MovesExistingDestinationAwayBeforeApplyingReplacement()
    {
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir("C:\\Songs\\A", [1u], [], []);
        cache.AddDir("C:\\Songs\\B", [2u], [], []);
        cache.EnsureAudioRelativeDirectoriesByHashes([1u, 2u]);

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.ReplaceDirsWithResult(
        [
            new KeyValuePair<string, string>("C:\\Songs\\A", "C:\\Songs\\B"),
            new KeyValuePair<string, string>("C:\\Songs\\B", "C:\\Songs\\C")
        ]);

        Assert.IsTrue(mutation.Changed);
        Assert.AreEqual(2, mutation.ReplacedDirectoryCount);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\C" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        Assert.IsNotNull(cache.GetEntryOrNull("C:\\Songs\\B"));
        Assert.IsNotNull(cache.GetEntryOrNull("C:\\Songs\\C"));
    }

    [TestMethod]
    public void ReplaceDirsWithResult_ExternalOverwriteInvalidatesCachedReverseLookup()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();

        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = cache.ReplaceDirsWithResult(
        [
            new KeyValuePair<string, string>("C:\\Songs\\A", "C:\\Songs\\B")
        ]);

        Assert.IsTrue(mutation.Changed);
        Assert.IsFalse(mutation.MaintainedFullReverseLookup);
        Assert.IsTrue(mutation.RequiresDeferredWarmup);
        Assert.IsFalse(cache.IsFullReverseLookupBuilt);
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(1u).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Songs\\B" }, cache.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(Array.Empty<string>(), cache.GetDirectoriesByAudioRelativeHash(3u).ToArray());
        Assert.IsNull(cache.GetEntryOrNull("C:\\Songs\\A"));
        Assert.IsNotNull(cache.GetEntryOrNull("C:\\Songs\\B"));
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
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(dirA, [audioRelativeHash], [imageRelativeHash], []);
        cache.AddDir(dirB, [audioRelativeHash], [], [movieRelativeHash]);

        cache.EnsureAudioRelativeDirectoriesByHashes([audioRelativeHash]);
        cache.EnsureImageRelativeDirectoriesByHashes([imageRelativeHash]);
        cache.EnsureMovieRelativeDirectoriesByHashes([movieRelativeHash]);

        cache.AddDir(dirC, [audioRelativeHash], [imageRelativeHash], [movieRelativeHash]);
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
        var scanResult = new ChartScanResult
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

        var fromCreate = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        var fromAdd = new DirectoryResourceLookupCache();
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
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(dirA, [audioRelativeHash], [imageRelativeHash], []);
        cache.AddDir(dirB, [], [], [movieRelativeHash]);

        cache.EnsureAudioRelativeDirectoriesByHashes([audioRelativeHash]);
        cache.EnsureImageRelativeDirectoriesByHashes([imageRelativeHash]);
        cache.EnsureMovieRelativeDirectoriesByHashes([movieRelativeHash]);

        CollectionAssert.AreEquivalent(new[] { dirA }, cache.GetDirectoriesByAudioRelativeHash(audioRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirA }, cache.GetDirectoriesByImageRelativeHash(imageRelativeHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { dirB }, cache.GetDirectoriesByMovieRelativeHash(movieRelativeHash).ToArray());
    }

    private static DirectoryResourceLookupCache CreateCache()
    {
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(
            "C:\\Songs\\A",
            [1u, 2u],
            [],
            []);
        cache.AddDir(
            "C:\\Songs\\B",
            [2u, 3u],
            [],
            []);
        return cache;
    }

    private static DirectoryResourceLookupCache CreateNativeCanonicalCache()
    {
        string dirA = "C:\\Songs\\A";
        string dirB = "C:\\Songs\\B";
        return DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            [dirA, dirB],
            [[1u, 2u], [2u, 3u]],
            [[], []],
            [[], []],
            [[], []],
            [[], []],
            [[], []],
            new Dictionary<uint, string[]>
            {
                { 1u, new[] { dirA } },
                { 2u, new[] { dirA, dirB } },
                { 3u, new[] { dirB } }
            },
            [],
            []);
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
