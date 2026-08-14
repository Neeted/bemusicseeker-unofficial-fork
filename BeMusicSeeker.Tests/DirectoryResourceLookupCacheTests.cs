using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DirectoryResourceLookupCacheTests
{
    [TestMethod]
    public void Entry_DoesNotExposeMutableHashArrayToAssemblyConsumers()
    {
        PropertyInfo[] assemblyVisibleMutableArrayProperties = typeof(DirectoryResourceLookupCache.Entry)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(property => property.PropertyType == typeof(uint[]))
            .Where(property => property.GetMethod?.IsAssembly == true
                || property.GetMethod?.IsFamilyOrAssembly == true
                || property.GetMethod?.IsFamilyAndAssembly == true)
            .ToArray();

        Assert.AreEqual(
            0,
            assemblyVisibleMutableArrayProperties.Length,
            string.Join(", ", assemblyVisibleMutableArrayProperties.Select(property => property.Name)));
    }

    [TestMethod]
    public void CreateFromScanResult_TrustsCanonicalArraysAndNormalizesUntrustedHashes()
    {
        string directory = @"C:\Songs\Unsorted";
        var scanResult = new ChartScanResult
        {
            ChartDirectories = new HashSet<string>([directory], StringComparer.OrdinalIgnoreCase),
            AudioRelativePathHashesByChartDirectory =
                new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [directory] = [30u, 10u, 20u, 10u]
                }
        };

        DirectoryResourceLookupCache cache = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
        DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(directory);

        CollectionAssert.AreEqual(new uint[] { 10u, 20u, 30u }, entry.AudioRelativePathHashArray);
        Assert.IsTrue(entry.AudioRelativePathHashes.Contains(10u));
        Assert.IsTrue(entry.AudioRelativePathHashes.Contains(20u));
        Assert.IsTrue(entry.AudioRelativePathHashes.Contains(30u));

        string trustedDirectory = @"C:\Songs\Trusted";
        uint[] trustedHashes = [10u, 20u, 30u];
        var trustedScanResult = new ChartScanResult
        {
            ResourceHashArraysAreSortedDistinct = true,
            ChartDirectories = new HashSet<string>([trustedDirectory], StringComparer.OrdinalIgnoreCase),
            AudioRelativePathHashesByChartDirectory =
                new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [trustedDirectory] = trustedHashes
                },
            ImageRelativePathHashesByChartDirectory =
                new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [trustedDirectory] = trustedHashes
                },
            MovieRelativePathHashesByChartDirectory =
                new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [trustedDirectory] = trustedHashes
                }
        };

        DirectoryResourceLookupCache trustedCache = DirectoryResourceLookupCache.CreateFromScanResult(trustedScanResult);
        DirectoryResourceLookupCache.Entry trustedEntry = trustedCache.GetEntryOrNull(trustedDirectory);

        CollectionAssert.AreEqual(trustedHashes, trustedEntry.AudioRelativePathHashArray);
        CollectionAssert.AreEqual(trustedHashes, trustedEntry.ImageRelativePathHashArray);
        CollectionAssert.AreEqual(trustedHashes, trustedEntry.MovieRelativePathHashArray);
    }

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

    [TestMethod]
    public void CloneForMutation_CopiesEntriesAndFullReverseLookupIndependently()
    {
        DirectoryResourceLookupCache original = CreateNativeCanonicalCache();
        DirectoryResourceLookupCache clone = original.CloneForMutation();

        clone.ReplaceDirWithResult(@"C:\Songs\A", @"C:\Songs\RenamedA");
        clone.RemoveDirWithResult(@"C:\Songs\B");
        clone.AddDir(@"C:\Songs\C", [4u], [], []);

        Assert.IsTrue(original.IsFullReverseLookupBuilt);
        Assert.IsTrue(clone.IsFullReverseLookupBuilt);
        Assert.IsNotNull(original.GetEntryOrNull(@"C:\Songs\A"));
        Assert.IsNotNull(original.GetEntryOrNull(@"C:\Songs\B"));
        Assert.IsNull(original.GetEntryOrNull(@"C:\Songs\RenamedA"));
        Assert.IsNull(original.GetEntryOrNull(@"C:\Songs\C"));
        CollectionAssert.AreEquivalent(
            new[] { @"C:\Songs\A", @"C:\Songs\B" },
            original.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { @"C:\Songs\RenamedA" },
            clone.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { @"C:\Songs\C" },
            clone.GetDirectoriesByAudioRelativeHash(4u).ToArray());
    }

    [TestMethod]
    public void LazyReverseLookupOverlappingOwnerMutation_PreservesBothGenerations()
    {
        const uint sharedHash = 73u;
        string oldDirectory = @"C:\Songs\Old";
        string addedDirectory = @"C:\Songs\Added";
        var initialIndex = new LibraryResourceIndex();
        initialIndex.DirectoryLookupCache.AddDir(oldDirectory, [sharedHash], [], []);
        var owner = new LibraryResourceIndexOwner(initialIndex);
        LibraryResourceIndexSnapshot oldSnapshot = owner.CaptureSnapshot();
        LibraryResourceIndexMutationReceipt? addReceipt = null;
        SetLazyReverseLookupEntrySnapshotObserver(
            oldSnapshot.DirectoryLookupCache,
            () => addReceipt = owner.AddDirectory(addedDirectory, [sharedHash], [], []));

        string[] oldDirectories =
            oldSnapshot.DirectoryLookupCache
                .GetDirectoriesByAudioRelativeHash(sharedHash)
                .ToArray();
        Assert.IsTrue(addReceipt.HasValue);
        LibraryResourceIndexMutationReceipt observedReceipt = addReceipt.Value;
        string[] currentDirectories = observedReceipt.Snapshot.DirectoryLookupCache
            .GetDirectoriesByAudioRelativeHash(sharedHash)
            .ToArray();

        CollectionAssert.AreEquivalent(new[] { oldDirectory }, oldDirectories);
        CollectionAssert.AreEquivalent(new[] { oldDirectory }, oldSnapshot.DirectoryLookupCache
            .GetDirectoriesByAudioRelativeHash(sharedHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { oldDirectory, addedDirectory }, currentDirectories);
        Assert.AreEqual(0L, oldSnapshot.Generation);
        Assert.AreEqual(1L, observedReceipt.Snapshot.Generation);
        Assert.AreNotSame(oldSnapshot.DirectoryLookupCache, observedReceipt.Snapshot.DirectoryLookupCache);
    }

    [TestMethod]
    public void PublishedEntryAndReverseLookupSurfaces_DoNotExposeSharedMutablePayloads()
    {
        DirectoryResourceLookupCache cache = CreateNativeCanonicalCache();
        DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(@"C:\Songs\A");
        uint[] exposedHashes = entry.AudioRelativePathHashArray;

        exposedHashes[0] = 999u;

        Assert.AreNotEqual(999u, entry.AudioRelativePathHashArray[0]);
        Assert.ThrowsException<NotSupportedException>(() => entry.AudioRelativePathHashes.Add(999u));
        Assert.IsFalse(cache.GetDirectoriesByAudioRelativeHash(2u) is string[]);
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

    private static void SetLazyReverseLookupEntrySnapshotObserver(
        DirectoryResourceLookupCache cache,
        Action observer)
    {
        FieldInfo field = typeof(DirectoryResourceLookupCache).GetField(
            "lazyReverseLookupEntrySnapshotCapturedObserver",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(cache, observer);
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
