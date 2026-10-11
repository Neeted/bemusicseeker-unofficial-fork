using System;
using System.Collections;
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
    /// <summary>
    /// 保証：未正規化の走査ハッシュは整列・重複除去し、正規化済み入力も三分類の値を欠落なく索引へ渡す。
    /// </summary>
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

        var cache = DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
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

        var trustedCache = DirectoryResourceLookupCache.CreateFromScanResult(trustedScanResult);
        DirectoryResourceLookupCache.Entry trustedEntry = trustedCache.GetEntryOrNull(trustedDirectory);

        CollectionAssert.AreEqual(trustedHashes, trustedEntry.AudioRelativePathHashArray);
        CollectionAssert.AreEqual(trustedHashes, trustedEntry.ImageRelativePathHashArray);
        CollectionAssert.AreEqual(trustedHashes, trustedEntry.MovieRelativePathHashArray);
    }

    /// <summary>
    /// 保証：要求した音声ハッシュの逆引きを重複なく構築し、各ハッシュへ定義したフォルダだけを返す。
    /// </summary>
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

    /// <summary>
    /// 保証：追加フォルダを計算済み欠落と全構築の逆引きへ直接反映し、追加後も古い空候補や再構築待ちを残さない。
    /// </summary>
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

    /// <summary>
    /// 保証：全構築済みの削除は自己固有のハッシュを消し、共有ハッシュでは残るフォルダだけを維持して再構築を要求しない。
    /// </summary>
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

    /// <summary>
    /// 保証：フォルダ置換は全構築済み候補を新パスへ変え、旧フォルダ名や遅延再構築を残さない。
    /// </summary>
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

    /// <summary>
    /// 保証：複数フォルダの一括置換は全対象の項目と共有候補を新パスへそろえ、未反映の旧パスを残さない。
    /// </summary>
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

    /// <summary>
    /// 保証：移動は対象項目の音声・画像・動画ハッシュbucketだけを更新し、無関係な逆引きの全列挙・書込みを行わない。
    /// </summary>
    [TestMethod]
    public void ReplaceDirsWithResult_RewritesOnlyBucketsForMovedEntryHashes()
    {
        const string first = @"C:\Songs\First";
        const string second = @"C:\Songs\Second";
        const string moved = @"C:\Renamed\First";
        var cache = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
            [first, second],
            [[11u], [12u]],
            [[21u], []],
            [[31u], []],
            [[11u], [12u]],
            [[21u], []],
            [[31u], []],
            new NonEnumerableReverseBaseline(new Dictionary<uint, string[]>
            {
                [11u] = [first],
                [12u] = [second],
                [99u] = [@"C:\Songs\Unrelated"]
            }),
            new Dictionary<uint, string[]> { [21u] = [first] },
            new Dictionary<uint, string[]> { [31u] = [first] });
        var writes = new List<(ChartResourceKind Kind, uint Hash)>();
        cache.ReverseBucketWrittenObserver = (kind, hash) => writes.Add((kind, hash));

        DirectoryResourceLookupCache next = cache.CloneForMutation();
        DirectoryResourceLookupCache.ReverseLookupMutationResult result = next.ReplaceDirsWithResult(
        [
            new KeyValuePair<string, string>(first, moved)
        ]);

        Assert.IsTrue(result.Changed);
        CollectionAssert.AreEquivalent(
            new[]
            {
                (ChartResourceKind.Audio, 11u),
                (ChartResourceKind.Image, 21u),
                (ChartResourceKind.Movie, 31u)
            },
            writes);
        Assert.IsFalse(writes.Contains((ChartResourceKind.Audio, 99u)));
        CollectionAssert.AreEqual(new[] { moved }, next.GetDirectoriesByAudioRelativeHash(11u).ToArray());
        CollectionAssert.AreEqual(new[] { moved }, next.GetDirectoriesByImageRelativeHash(21u).ToArray());
        CollectionAssert.AreEqual(new[] { moved }, next.GetDirectoriesByMovieRelativeHash(31u).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\Songs\Unrelated" }, next.GetDirectoriesByAudioRelativeHash(99u).ToArray());
        CollectionAssert.AreEqual(new[] { first }, cache.GetDirectoriesByAudioRelativeHash(11u).ToArray());
    }

    /// <summary>
    /// 保証：移動先の既存フォルダも同時移動する要求では先に退避して両方の対応を保持し、連鎖置換で内容を上書きしない。
    /// </summary>
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

    /// <summary>
    /// 保証：別フォルダへの上書き移動では旧逆引きを失効して再構築を要求し、上書きされたリソース候補を残さない。
    /// </summary>
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

    /// <summary>
    /// 保証：階層付き音声・画像・動画を追加・削除・改名しても、全分類の逆引きから古いフォルダを除く。
    /// </summary>
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

    /// <summary>
    /// 保証：同じ走査入力からの索引作成と単独追加は全分類で同じ項目を作り、入口別の分類欠落を起こさない。
    /// </summary>
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

    /// <summary>
    /// 保証：音声・画像・動画それぞれの指定ハッシュから、分類に対応するフォルダ候補を返す。
    /// </summary>
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

    /// <summary>
    /// 保証：変更用分岐で移動・削除・追加しても元の項目と全逆引きを保持し、新世代だけへ実変更を反映する。
    /// </summary>
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

    /// <summary>
    /// 保証：旧snapshotの遅延逆引き計算中に所有者が追加しても新旧の候補を分離し、旧世代へ追加フォルダを混入させない。
    /// </summary>
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

    /// <summary>
    /// 保証：公開ハッシュ配列の書換えや集合への追加では索引の原本を変更できず、逆引きも共有可変配列を返さない。
    /// </summary>
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

    /// <summary>
    /// 保証：リソース零件の追加は項目格納だけを変更し、基底項目の列挙や逆引き書込みなしに新世代へ反映する。
    /// </summary>
    [TestMethod]
    public void CloneForMutation_EmptyResourceEntryChangesOnlyTheEntryStore()
    {
        DirectoryResourceLookupCache original = CreateNativeCanonicalCache();
        var writes = new List<(ChartResourceKind Kind, uint Hash)>();
        var entryMutations = new List<string>();
        var entryVisits = new List<string>();
        original.ReverseBucketWrittenObserver = (kind, hash) => writes.Add((kind, hash));
        original.EntryStoreMutationObserver = path => entryMutations.Add(path);
        original.EntryStoreEntryVisitedObserver = path => entryVisits.Add(path);
        DirectoryResourceLookupCache next = original.CloneForMutation();

        DirectoryResourceLookupCache.ReverseLookupMutationResult result = next.AddDir(
            @"C:\Songs\NoResources", [], [], []);
        next.AddDir(@"C:\Songs\AlsoEmpty", [], [], []);

        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, result.UpdatedHashCount);
        Assert.AreEqual(0, writes.Count);
        CollectionAssert.AreEqual(
            new[] { @"C:\Songs\NoResources", @"C:\Songs\AlsoEmpty" },
            entryMutations);
        Assert.AreEqual(0, entryVisits.Count);
        Assert.AreEqual(2, original.Count);
        Assert.AreEqual(4, next.Count);
        Assert.IsNull(original.GetEntryOrNull(@"C:\Songs\NoResources"));
        Assert.IsNotNull(next.GetEntryOrNull(@"C:\Songs\NoResources"));
        Assert.IsNotNull(next.GetEntryOrNull(@"C:\Songs\AlsoEmpty"));
        Assert.AreEqual(0, entryVisits.Count);
    }

    /// <summary>
    /// 保証：音声・画像・動画の各単独変更は該当分類だけの逆引きを更新し、旧世代と他分類の候補を保持する。
    /// </summary>
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

    /// <summary>
    /// 保証：自己所有だけの変化は項目へ反映し、候補列が同じなら逆引きを書き換えず旧世代の自己所有値を維持する。
    /// </summary>
    [TestMethod]
    public void SelfOwnedOnlyChange_PreservesEntryChangeWithoutRewritingIdenticalCandidates()
    {
        const string directory = @"C:\Songs\Only";
        var original = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
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
    /// 保証：自己所有だけの置換でも候補を除去後に末尾へ追加し、既に末尾の場合だけ逆引き無変更として旧候補順を保持する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SelfOwnedOnlyChange_MultipleCandidatesPreserveRemoveThenAppendOrder(bool changeLastCandidate)
    {
        const string first = @"C:\Songs\First";
        const string second = @"C:\Songs\Second";
        var original = DirectoryResourceLookupCache.CreateFromNativeCanonicalArrays(
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

    /// <summary>
    /// 保証：ディレクトリのリソース置換では共通候補も除去後追加の順へ変え、新しいハッシュだけでなく既存候補順を反映する。
    /// </summary>
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

    /// <summary>
    /// 保証：遅延逆引きは計算済み欠落だけを追加時に更新し、未計算ハッシュを空候補として保存せず新旧世代を分離する。
    /// </summary>
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

    /// <summary>
    /// 保証：管理走査・native入力の両方で変更列の各世代が独立した所属事実へ一致し、共有・固有・零ハッシュの誤反映を検出する。
    /// </summary>
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
            foreach ((DirectoryResourceLookupCache Cache, Dictionary<string, uint[][]> Facts) generation in retained)
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

    // 所有済みread-only baseを本番factoryへ渡し、移動時に関連bucketだけを参照することを確認する。
    // reverse map全体の列挙が行われた場合はここで失敗する。
    private sealed class NonEnumerableReverseBaseline(IReadOnlyDictionary<uint, string[]> values)
        : IReadOnlyDictionary<uint, string[]>
    {
        public int Count => values.Count;
        public string[] this[uint key] => values[key];
        public IEnumerable<uint> Keys => throw new AssertFailedException("reverse base keys were enumerated");
        public IEnumerable<string[]> Values => throw new AssertFailedException("reverse base values were enumerated");
        public bool ContainsKey(uint key) => values.ContainsKey(key);
        public bool TryGetValue(uint key, out string[] value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<uint, string[]>> GetEnumerator() =>
            throw new AssertFailedException("reverse base entries were enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
