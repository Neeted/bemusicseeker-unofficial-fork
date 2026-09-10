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

    [TestMethod]
    public void CloneForMutation_EmptyResourceEntryChangesOnlyTheDirectoryMap()
    {
        DirectoryResourceLookupCache original = CreateNativeCanonicalCache();
        var writes = new List<(ChartResourceKind Kind, uint Hash)>();
        var entryCopies = new List<int>();
        original.ReverseBucketWrittenObserver = (kind, hash) => writes.Add((kind, hash));
        original.EntriesRootCopiedObserver = count => entryCopies.Add(count);
        DirectoryResourceLookupCache next = original.CloneForMutation();

        DirectoryResourceLookupCache.ReverseLookupMutationResult result = next.AddDir(
            @"C:\Songs\NoResources", [], [], []);
        next.AddDir(@"C:\Songs\AlsoEmpty", [], [], []);

        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, result.UpdatedHashCount);
        Assert.AreEqual(0, writes.Count);
        CollectionAssert.AreEqual(new[] { 2 }, entryCopies);
        Assert.AreEqual(2, original.Count);
        Assert.AreEqual(4, next.Count);
        Assert.IsNull(original.GetEntryOrNull(@"C:\Songs\NoResources"));
        Assert.IsNotNull(next.GetEntryOrNull(@"C:\Songs\NoResources"));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CloneForMutation_ActualResourceChangeWritesOnlyItsCategory(int category)
    {
        DirectoryResourceLookupCache original = CreateNativeCanonicalCache();
        var writes = new List<(ChartResourceKind Kind, uint Hash)>();
        original.ReverseBucketWrittenObserver = (kind, hash) => writes.Add((kind, hash));
        DirectoryResourceLookupCache next = original.CloneForMutation();
        uint[][] hashes = [[], [], []];
        hashes[category] = [41u];
        string added = @"C:\Songs\Resources";
        ChartResourceKind expectedKind = new[]
        {
            ChartResourceKind.Audio, ChartResourceKind.Image, ChartResourceKind.Movie
        }[category];

        next.AddDir(added, hashes[0], hashes[1], hashes[2]);

        CollectionAssert.AreEqual(new[] { (expectedKind, 41u) }, writes);
        Assert.AreEqual(0, GetCandidates(original, category, 41u).Length);
        CollectionAssert.AreEqual(new[] { added }, GetCandidates(next, category, 41u));
        Assert.IsTrue(next.IsFullReverseLookupBuilt);
        DirectoryResourceLookupCache removed = next.CloneForMutation();
        writes.Clear();
        removed.RemoveDir(added);
        CollectionAssert.AreEqual(new[] { (expectedKind, 41u) }, writes);
        Assert.AreEqual(0, GetCandidates(removed, category, 41u).Length);
        CollectionAssert.AreEqual(new[] { added }, GetCandidates(next, category, 41u));
    }

    [TestMethod]
    public void SelfOwnedOnlyChange_PreservesEntryChangeWithoutRewritingIdenticalCandidates()
    {
        const string directory = @"C:\Songs\Only";
        DirectoryResourceLookupCache original = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            [directory], [[11u]], [[22u]], [[33u]], [[11u]], [[22u]], [[33u]],
            new Dictionary<uint, string[]> { [11u] = [directory] },
            new Dictionary<uint, string[]> { [22u] = [directory] },
            new Dictionary<uint, string[]> { [33u] = [directory] });
        var writes = new List<uint>();
        original.ReverseBucketWrittenObserver = (_, hash) => writes.Add(hash);
        DirectoryResourceLookupCache next = original.CloneForMutation();

        DirectoryResourceLookupCache.ReverseLookupMutationResult result = next.AddDir(
            directory, [11u], [22u], [33u], [], [22u], [33u]);

        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, writes.Count);
        CollectionAssert.AreEqual(new uint[] { 11u }, original.GetEntryOrNull(directory).SelfOwnedAudioRelativePathHashArray);
        Assert.AreEqual(0, next.GetEntryOrNull(directory).SelfOwnedAudioRelativePathHashArray.Length);
        CollectionAssert.AreEqual(new[] { directory }, next.GetDirectoriesByAudioRelativeHash(11u).ToArray());
    }

    /// <summary>
    /// Characterizes the retained candidate-order contract: a SelfOwned-only replacement
    /// still moves the directory to the tail, but writes nothing if it was already last.
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SelfOwnedOnlyChange_MultipleCandidatesPreserveRemoveThenAppendOrder(bool changeLastCandidate)
    {
        const string first = @"C:\Songs\First";
        const string second = @"C:\Songs\Second";
        DirectoryResourceLookupCache original = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            [first, second], [[11u], [11u]], [[22u], [22u]], [[33u], [33u]],
            [[11u], [11u]], [[22u], [22u]], [[33u], [33u]],
            new Dictionary<uint, string[]> { [11u] = [first, second] },
            new Dictionary<uint, string[]> { [22u] = [first, second] },
            new Dictionary<uint, string[]> { [33u] = [first, second] });
        var writes = new List<(ChartResourceKind Kind, uint Hash)>();
        original.ReverseBucketWrittenObserver = (kind, hash) => writes.Add((kind, hash));
        DirectoryResourceLookupCache next = original.CloneForMutation();
        string changedDirectory = changeLastCandidate ? second : first;

        DirectoryResourceLookupCache.ReverseLookupMutationResult result = next.AddDir(
            changedDirectory, [11u], [22u], [33u], [], [22u], [33u]);

        Assert.IsTrue(result.Changed);
        if (changeLastCandidate)
        {
            Assert.AreEqual(0, writes.Count);
        }
        else
        {
            CollectionAssert.AreEquivalent(new[]
            {
                (ChartResourceKind.Audio, 11u),
                (ChartResourceKind.Image, 22u),
                (ChartResourceKind.Movie, 33u)
            }, writes);
        }
        string[] expectedCandidates = changeLastCandidate ? [first, second] : [second, first];
        uint[] hashes = [11u, 22u, 33u];
        for (int category = 0; category < hashes.Length; category++)
        {
            CollectionAssert.AreEqual(new[] { first, second }, GetCandidates(original, category, hashes[category]));
            CollectionAssert.AreEqual(expectedCandidates, GetCandidates(next, category, hashes[category]));
        }
        CollectionAssert.AreEqual(new uint[] { 11u },
            original.GetEntryOrNull(changedDirectory).SelfOwnedAudioRelativePathHashArray);
        Assert.AreEqual(0, next.GetEntryOrNull(changedDirectory).SelfOwnedAudioRelativePathHashArray.Length);
        string unchangedDirectory = changeLastCandidate ? first : second;
        AssertEntriesEqual(original.GetEntryOrNull(unchangedDirectory), next.GetEntryOrNull(unchangedDirectory));
    }

    [TestMethod]
    public void DirectoryReplacement_PreservesRemoveThenAppendCandidateOrder()
    {
        DirectoryResourceLookupCache original = CreateNativeCanonicalCache();
        DirectoryResourceLookupCache next = original.CloneForMutation();
        next.AddDir(@"C:\Songs\A", [1u, 2u, 4u], [], []);

        CollectionAssert.AreEqual(new[] { @"C:\Songs\A", @"C:\Songs\B" },
            original.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\Songs\B", @"C:\Songs\A" },
            next.GetDirectoriesByAudioRelativeHash(2u).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\Songs\A" },
            next.GetDirectoriesByAudioRelativeHash(4u).ToArray());
    }

    [TestMethod]
    public void LazyMutation_UpdatesCachedMissButDoesNotInventAnUncachedEmptyResult()
    {
        const string first = @"C:\Songs\First";
        const string second = @"C:\Songs\Second";
        var original = new DirectoryResourceLookupCache();
        original.AddDir(first, [17u, 18u], [], []);
        Assert.AreEqual(0, original.GetDirectoriesByAudioRelativeHash(19u).Count);
        var writes = new List<uint>();
        original.ReverseBucketWrittenObserver = (_, hash) => writes.Add(hash);
        DirectoryResourceLookupCache next = original.CloneForMutation();

        next.AddDir(second, [17u, 19u], [], []);

        CollectionAssert.AreEqual(new uint[] { 19u }, writes);
        CollectionAssert.AreEqual(new[] { second }, next.GetDirectoriesByAudioRelativeHash(19u).ToArray());
        Assert.AreEqual(0, original.GetDirectoriesByAudioRelativeHash(19u).Count);
        CollectionAssert.AreEquivalent(new[] { first, second }, next.GetDirectoriesByAudioRelativeHash(17u).ToArray());
        CollectionAssert.AreEqual(new[] { first }, original.GetDirectoriesByAudioRelativeHash(17u).ToArray());
        Assert.AreEqual(0, next.GetDirectoriesByAudioRelativeHash(0u).Count);
        Assert.IsFalse(next.IsFullReverseLookupBuilt);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResourceMutationSequence_MatchesIndependentMembershipFactsInEveryRetainedGeneration(bool native)
    {
        const string a = @"C:\Songs\A";
        const string b = @"C:\Songs\B";
        const string c = @"C:\Songs\C";
        var facts = new Dictionary<string, uint[][]>(StringComparer.OrdinalIgnoreCase)
        {
            [a] = [[1u, 2u], [10u], [20u]],
            [b] = [[2u], [11u], []]
        };
        DirectoryResourceLookupCache cache;
        if (native)
        {
            cache = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
                [a, b], [[1u, 2u], [2u]], [[10u], [11u]], [[20u], []],
                [[1u, 2u], [2u]], [[10u], [11u]], [[20u], []],
                new Dictionary<uint, string[]> { [1u] = [a], [2u] = [a, b] },
                new Dictionary<uint, string[]> { [10u] = [a], [11u] = [b] },
                new Dictionary<uint, string[]> { [20u] = [a] });
        }
        else
        {
            cache = new DirectoryResourceLookupCache();
            cache.AddDir(a, facts[a][0], facts[a][1], facts[a][2]);
            cache.AddDir(b, facts[b][0], facts[b][1], facts[b][2]);
        }
        var retained = new List<(DirectoryResourceLookupCache Cache, Dictionary<string, uint[][]> Facts)>();
        for (int step = 0; step < 5; step++)
        {
            retained.Add((cache, new Dictionary<string, uint[][]>(facts, StringComparer.OrdinalIgnoreCase)));
            // Oracle membership is a direct relation in fixture facts, not the incremental algorithm.
            foreach (var generation in retained)
            {
                CollectionAssert.AreEquivalent(generation.Facts.Keys.ToArray(), generation.Cache.Keys.ToArray());
                foreach (uint hash in new uint[] { 0u, 1u, 2u, 3u, 10u, 11u, 20u, 21u, 99u })
                {
                    for (int category = 0; category < 3; category++)
                    {
                        string[] expected = generation.Facts.Where(pair => hash != 0u && pair.Value[category].Contains(hash))
                            .Select(pair => pair.Key).ToArray();
                        CollectionAssert.AreEquivalent(expected, GetCandidates(generation.Cache, category, hash));
                    }
                }
            }
            if (step == 4)
            {
                break;
            }
            cache = cache.CloneForMutation();
            if (step == 0)
            {
                facts[a] = [[2u, 3u], [10u, 11u], [21u]];
                cache.AddDir(a, facts[a][0], facts[a][1], facts[a][2]);
            }
            else if (step == 1)
            {
                facts[c] = [[1u, 3u], [], [20u]];
                cache.AddDir(c, facts[c][0], facts[c][1], facts[c][2]);
            }
            else if (step == 2)
            {
                facts.Remove(b);
                cache.RemoveDir(b);
            }
            else
            {
                facts[@"C:\Songs\Empty"] = [[], [], []];
                cache.AddDir(@"C:\Songs\Empty", [], [], []);
            }
        }
    }

    private static string[] GetCandidates(DirectoryResourceLookupCache cache, int category, uint hash)
    {
        return (category switch
        {
            0 => cache.GetDirectoriesByAudioRelativeHash(hash),
            1 => cache.GetDirectoriesByImageRelativeHash(hash),
            2 => cache.GetDirectoriesByMovieRelativeHash(hash),
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        }).ToArray();
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
