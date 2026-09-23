using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryResourceIndexOwnerTests
{
    [TestMethod]
    public void ReplaceThenMoveFolderReferences_MutatesOnlyCurrentIndex()
    {
        string initialDirectory = @"C:\Library\Initial";
        string oldDirectory = @"C:\Library\Old";
        string nestedDirectory = @"C:\Library\Old\Nested";
        string newDirectory = @"C:\Library\New";
        LibraryResourceIndex initialIndex = CreateIndex(initialDirectory);
        var owner = new LibraryResourceIndexOwner(initialIndex);
        LibraryResourceIndex replacementIndex = CreateIndex(oldDirectory, nestedDirectory);

        LibraryResourceIndexSnapshot replacementSnapshot = owner.Replace(replacementIndex);
        LibraryResourceIndexMutationReceipt moveReceipt = owner.MoveFolderReferences(oldDirectory, newDirectory);

        Assert.AreSame(replacementIndex, replacementSnapshot.Index);
        Assert.AreEqual(1L, replacementSnapshot.Generation);
        Assert.AreEqual(2L, moveReceipt.Snapshot.Generation);
        Assert.IsTrue(moveReceipt.MutationResult.Changed);
        Assert.AreNotSame(replacementSnapshot.Index, moveReceipt.Snapshot.Index);
        Assert.AreNotSame(replacementSnapshot.DirectoryLookupCache, moveReceipt.Snapshot.DirectoryLookupCache);
        Assert.IsNotNull(replacementSnapshot.DirectoryLookupCache.GetEntryOrNull(oldDirectory));
        Assert.IsNotNull(replacementSnapshot.DirectoryLookupCache.GetEntryOrNull(nestedDirectory));
        Assert.IsNull(replacementSnapshot.DirectoryLookupCache.GetEntryOrNull(newDirectory));
        Assert.IsNull(moveReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(oldDirectory));
        Assert.IsNull(moveReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(nestedDirectory));
        Assert.IsNotNull(moveReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(newDirectory));
        Assert.IsNotNull(moveReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(newDirectory + @"\Nested"));
        Assert.IsNotNull(initialIndex.DirectoryLookupCache.GetEntryOrNull(initialDirectory));
        Assert.IsNull(initialIndex.DirectoryLookupCache.GetEntryOrNull(newDirectory));
    }

    [TestMethod]
    public void CurrentMutationCommands_AdvanceGenerationForReportedChanges()
    {
        string directory = @"C:\Library\Chart";
        var owner = new LibraryResourceIndexOwner(new LibraryResourceIndex());

        LibraryResourceIndexMutationReceipt addReceipt = owner.AddDirectory(directory, new[] { "sound.wav" });
        LibraryResourceIndexMutationReceipt duplicateAddReceipt = owner.AddDirectory(directory, new[] { "sound.wav" });
        LibraryResourceIndexMutationReceipt removeReceipt = owner.RemoveUnderSourceDirectory(directory);
        LibraryResourceIndexMutationReceipt missingRemoveReceipt = owner.RemoveUnderSourceDirectory(directory);

        Assert.IsTrue(addReceipt.MutationResult.Changed);
        Assert.AreEqual(1L, addReceipt.Snapshot.Generation);
        Assert.IsNotNull(addReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(directory));
        Assert.IsFalse(duplicateAddReceipt.MutationResult.Changed);
        Assert.AreEqual(1L, duplicateAddReceipt.Snapshot.Generation);
        Assert.AreSame(addReceipt.Snapshot.Index, duplicateAddReceipt.Snapshot.Index);
        Assert.AreSame(addReceipt.Snapshot.DirectoryLookupCache, duplicateAddReceipt.Snapshot.DirectoryLookupCache);
        Assert.IsNotNull(addReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(directory));
        Assert.IsTrue(removeReceipt.MutationResult.Changed);
        Assert.AreEqual(2L, removeReceipt.Snapshot.Generation);
        Assert.IsNotNull(duplicateAddReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(directory));
        Assert.IsNull(removeReceipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(directory));
        Assert.IsFalse(missingRemoveReceipt.MutationResult.Changed);
        Assert.AreEqual(2L, missingRemoveReceipt.Snapshot.Generation);
        Assert.AreSame(removeReceipt.Snapshot.Index, missingRemoveReceipt.Snapshot.Index);
        Assert.AreSame(removeReceipt.Snapshot.DirectoryLookupCache, missingRemoveReceipt.Snapshot.DirectoryLookupCache);
        Assert.IsFalse(missingRemoveReceipt.Snapshot.DirectoryLookupCache.Keys.Any());
    }

    [TestMethod]
    public void AddDirectory_SemanticallyEqualSixCategories_IsNoOp()
    {
        string directory = @"C:\Library\SixCategories";
        var owner = new LibraryResourceIndexOwner(new LibraryResourceIndex());
        LibraryResourceIndexMutationReceipt first = owner.AddDirectory(
            directory,
            [2u, 1u, 2u],
            [4u, 3u],
            [6u, 5u],
            [8u, 7u],
            [10u, 9u],
            [12u, 11u]);

        LibraryResourceIndexMutationReceipt duplicate = owner.AddDirectory(
            directory,
            [1u, 2u],
            [3u, 4u, 3u],
            [5u, 6u],
            [7u, 8u, 7u],
            [9u, 10u],
            [11u, 12u, 11u]);
        LibraryResourceIndexMutationReceipt changed = owner.AddDirectory(
            directory,
            [1u, 2u],
            [3u, 4u],
            [5u, 6u],
            [7u, 8u],
            [9u, 10u],
            [11u, 13u]);

        Assert.IsFalse(duplicate.MutationResult.Changed);
        Assert.AreEqual(first.Snapshot.Generation, duplicate.Snapshot.Generation);
        Assert.AreSame(first.Snapshot.Index, duplicate.Snapshot.Index);
        Assert.AreSame(first.Snapshot.DirectoryLookupCache, duplicate.Snapshot.DirectoryLookupCache);
        Assert.IsTrue(changed.MutationResult.Changed);
        Assert.AreEqual(first.Snapshot.Generation + 1L, changed.Snapshot.Generation);
    }

    [TestMethod]
    public void AddDirectory_EachCategoryChangePublishesOnlyNewSnapshotPayload()
    {
        string directory = @"C:\Library\CategoryChange";
        for (int changedCategory = 0; changedCategory < 6; changedCategory++)
        {
            var owner = new LibraryResourceIndexOwner(new LibraryResourceIndex());
            LibraryResourceIndexMutationReceipt first = AddSixCategories(
                owner,
                directory,
                [[1u], [2u], [3u], [4u], [5u], [6u]]);
            uint[][] changedValues = [[1u], [2u], [3u], [4u], [5u], [6u]];
            changedValues[changedCategory] = [100u + (uint)changedCategory];

            LibraryResourceIndexMutationReceipt changed =
                AddSixCategories(owner, directory, changedValues);

            Assert.IsTrue(changed.MutationResult.Changed, $"category {changedCategory}");
            Assert.AreEqual(2L, changed.Snapshot.Generation, $"category {changedCategory}");
            CollectionAssert.AreEqual(
                new[] { (uint)(changedCategory + 1) },
                GetCategory(first.Snapshot.DirectoryLookupCache.GetEntryOrNull(directory), changedCategory));
            CollectionAssert.AreEqual(
                changedValues[changedCategory],
                GetCategory(changed.Snapshot.DirectoryLookupCache.GetEntryOrNull(directory), changedCategory));
        }
    }

    [TestMethod]
    public void AddDirectory_WhenFileEnumerationThrows_PreservesPublishedSnapshot()
    {
        var owner = new LibraryResourceIndexOwner(CreateIndex(@"C:\Library\Existing"));
        LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();

        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.AddDirectory(@"C:\Library\Failing", new ThrowingFileNames()));

        LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
        Assert.AreSame(before.Index, after.Index);
        Assert.AreSame(before.DirectoryLookupCache, after.DirectoryLookupCache);
        Assert.AreEqual(before.Generation, after.Generation);
        Assert.IsNull(after.DirectoryLookupCache.GetEntryOrNull(@"C:\Library\Failing"));
    }

    [TestMethod]
    public void AddDirectory_DoesNotRetainSingletonHashArrayAlias()
    {
        uint[] callerOwnedHashes = [11u];
        var scan = new ChartScanResult();
        scan.ChartDirectories.Add(@"C:\Library\Chart");
        scan.AudioRelativePathHashesByChartDirectory[@"C:\Library\Chart"] = callerOwnedHashes;
        var owner = new LibraryResourceIndexOwner(new LibraryResourceIndex());

        LibraryResourceIndexMutationReceipt receipt = owner.AddDirectory(@"C:\Library\Chart", scan);
        callerOwnedHashes[0] = 99u;

        CollectionAssert.AreEqual(
            new uint[] { 11u },
            receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(@"C:\Library\Chart").AudioRelativePathHashArray);
    }

    [TestMethod]
    public void AddDirectory_SeparatesOldAndNewLazyReverseLookupBuckets()
    {
        const uint hash = 17u;
        var owner = new LibraryResourceIndexOwner(CreateIndex(@"C:\Library\Existing"));
        LibraryResourceIndexSnapshot oldSnapshot = owner.CaptureSnapshot();
        CollectionAssert.AreEquivalent(
            Array.Empty<string>(),
            oldSnapshot.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(hash).ToArray());

        LibraryResourceIndexMutationReceipt receipt = owner.AddDirectory(
            @"C:\Library\Added",
            [hash],
            [],
            []);

        CollectionAssert.AreEquivalent(
            Array.Empty<string>(),
            oldSnapshot.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(hash).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { @"C:\Library\Added" },
            receipt.Snapshot.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(hash).ToArray());
    }

    [TestMethod]
    public void ReplaceSourceDirectoryWithScan_PublishesCombinedMutationOnce()
    {
        string source = @"C:\Library\Source";
        string destination = @"C:\Library\Destination";
        var owner = new LibraryResourceIndexOwner(CreateIndex(source, source + @"\Nested"));
        var replacementScan = new ChartScanResult();
        replacementScan.ChartDirectories.Add(destination);
        replacementScan.AudioRelativePathHashesByChartDirectory[destination] = [23u];

        LibraryResourceIndexSnapshot prior = owner.CaptureSnapshot();
        LibraryResourceIndexMutationReceipt receipt =
            owner.ReplaceSourceDirectoryWithScan(source, replacementScan);

        Assert.IsTrue(receipt.MutationResult.Changed);
        Assert.AreEqual(1L, receipt.Snapshot.Generation);
        Assert.IsNull(receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(source));
        Assert.IsNull(receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(source + @"\Nested"));
        Assert.IsNotNull(receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(destination));
        Assert.IsNotNull(prior.DirectoryLookupCache.GetEntryOrNull(source));
        Assert.IsNotNull(prior.DirectoryLookupCache.GetEntryOrNull(source + @"\Nested"));
        Assert.IsNull(prior.DirectoryLookupCache.GetEntryOrNull(destination));
    }

    [TestMethod]
    public void ReplaceSourceDirectoryWithScan_SemanticallyIdenticalReplacementIsNoOp()
    {
        string source = @"C:\Library\Source";
        string nested = source + @"\Nested";
        var initialScan = new ChartScanResult();
        initialScan.ChartDirectories.Add(source);
        initialScan.ChartDirectories.Add(nested);
        initialScan.AudioRelativePathHashesByChartDirectory[source] = [11u, 12u];
        initialScan.ImageRelativePathHashesByChartDirectory[nested] = [21u];
        var initialIndex = LibraryResourceIndex.CreateFromScanResult(initialScan);
        var owner = new LibraryResourceIndexOwner(initialIndex);
        LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();

        LibraryResourceIndexMutationReceipt receipt =
            owner.ReplaceSourceDirectoryWithScan(source, initialScan);

        Assert.IsFalse(receipt.MutationResult.Changed);
        Assert.AreEqual(before.Generation, receipt.Snapshot.Generation);
        Assert.AreSame(before.Index, receipt.Snapshot.Index);
        Assert.AreSame(before.DirectoryLookupCache, receipt.Snapshot.DirectoryLookupCache);
        CollectionAssert.AreEqual(
            new uint[] { 11u, 12u },
            receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(source).AudioRelativePathHashArray);
        CollectionAssert.AreEqual(
            new uint[] { 21u },
            receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(nested).ImageRelativePathHashArray);
    }

    [TestMethod]
    public void ReplaceSourceDirectoryWithDirectories_WhenEnumerationThrows_PreservesPriorSnapshot()
    {
        string source = @"C:\Library\Source";
        var owner = new LibraryResourceIndexOwner(CreateIndex(source));
        LibraryResourceIndexSnapshot prior = owner.CaptureSnapshot();

        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.ReplaceSourceDirectoryWithDirectories(
                source,
                new ThrowingDirectories(),
                new ChartScanResult()));

        LibraryResourceIndexSnapshot current = owner.CaptureSnapshot();
        Assert.AreSame(prior.Index, current.Index);
        Assert.AreSame(prior.DirectoryLookupCache, current.DirectoryLookupCache);
        Assert.AreEqual(prior.Generation, current.Generation);
        Assert.IsNotNull(current.DirectoryLookupCache.GetEntryOrNull(source));
        Assert.IsNull(current.DirectoryLookupCache.GetEntryOrNull(@"C:\Library\Destination"));
        Assert.IsNull(prior.DirectoryLookupCache.GetEntryOrNull(@"C:\Library\Destination"));
    }

    [TestMethod]
    public void AddDirectories_PublishesOneGenerationAndPreservesPriorSnapshot()
    {
        string firstDirectory = @"C:\Library\First";
        string secondDirectory = @"C:\Library\Second";
        var scan = new ChartScanResult();
        scan.ChartDirectories.Add(firstDirectory);
        scan.ChartDirectories.Add(secondDirectory);
        scan.AudioRelativePathHashesByChartDirectory[firstDirectory] = [11u];
        scan.AudioRelativePathHashesByChartDirectory[secondDirectory] = [22u];
        var owner = new LibraryResourceIndexOwner(new LibraryResourceIndex());
        LibraryResourceIndexSnapshot priorSnapshot = owner.CaptureSnapshot();

        LibraryResourceIndexMutationReceipt receipt = owner.AddDirectories(
            [firstDirectory, secondDirectory],
            scan);

        Assert.AreEqual(0L, priorSnapshot.Generation);
        Assert.AreEqual(1L, receipt.Snapshot.Generation);
        Assert.AreEqual(0, priorSnapshot.DirectoryLookupCache.Count);
        Assert.AreEqual(2, receipt.Snapshot.DirectoryLookupCache.Count);
        Assert.IsNull(priorSnapshot.DirectoryLookupCache.GetEntryOrNull(firstDirectory));
        Assert.IsNull(priorSnapshot.DirectoryLookupCache.GetEntryOrNull(secondDirectory));
        Assert.IsNotNull(receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(firstDirectory));
        Assert.IsNotNull(receipt.Snapshot.DirectoryLookupCache.GetEntryOrNull(secondDirectory));
    }

    [TestMethod]
    public void RemoveUnderSourceDirectories_PublishesSuccessfulSubtreesAndUpdatesEntriesLocally()
    {
        const string first = @"C:\Library\First";
        const string nested = @"C:\Library\First\Nested";
        const string failed = @"C:\Library\Failed";
        const string last = @"C:\Library\Last";
        var owner = new LibraryResourceIndexOwner(CreateFullResourceIndex(first, nested, failed, last));
        LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
        var entryMutations = new List<string>();
        var entryVisits = new List<string>();
        var ancestorChecks = new List<(string Entry, string Ancestor)>();
        before.DirectoryLookupCache.EntryStoreMutationObserver = path => entryMutations.Add(path);
        before.DirectoryLookupCache.EntryStoreEntryVisitedObserver = path => entryVisits.Add(path);
        before.DirectoryLookupCache.EntryAncestorCheckObserver =
            (entry, ancestor) => ancestorChecks.Add((entry, ancestor));

        // This is the confirmed filesystem result, not the original selection containing Failed.
        LibraryResourceIndexMutationReceipt receipt = owner.RemoveUnderSourceDirectories(
            [
                nested,
                first,
                first.ToUpperInvariant(),
                last,
                @"C:\Library\UnrelatedA",
                @"C:\Library\UnrelatedB"
            ]);

        Assert.IsTrue(receipt.MutationResult.Changed);
        Assert.AreEqual(before.Generation + 1, receipt.Snapshot.Generation);
        Assert.AreEqual(3, receipt.MutationResult.RemovedDirectoryCount);
        CollectionAssert.AreEqual(new[] { first, nested, last }, entryMutations);
        CollectionAssert.AreEqual(new[] { first, nested, failed, last }, entryVisits);
        Assert.AreEqual(ancestorChecks.Count, ancestorChecks.Distinct().Count());
        Assert.AreEqual(
            entryVisits.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ancestorChecks
                .Select(check => check.Entry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        CollectionAssert.AreEquivalent(new[] { first, nested, failed, last }, before.DirectoryLookupCache.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { failed }, receipt.Snapshot.DirectoryLookupCache.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { failed }, receipt.Snapshot.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(17u).ToArray());
        CollectionAssert.AreEqual(new[] { failed }, receipt.Snapshot.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(23u).ToArray());
        CollectionAssert.AreEqual(new[] { failed }, receipt.Snapshot.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(31u).ToArray());
        Assert.AreSame(receipt.Snapshot.Index, owner.CaptureSnapshot().Index);
    }

    [TestMethod]
    public void RemoveUnderSourceDirectories_PreservesSiblingWithSharedPrefixAndDeduplicatesRoots()
    {
        const string source = @"C:\Library\Source";
        const string nested = source + @"\Nested";
        const string deep = nested + @"\Deep";
        const string sibling = @"C:\Library\SourceBackup";
        const string keep = @"C:\Library\Keep";
        var owner = new LibraryResourceIndexOwner(CreateFullResourceIndex(
            source, nested, deep, sibling, keep));
        LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();

        LibraryResourceIndexMutationReceipt receipt = owner.RemoveUnderSourceDirectories(
            [nested + @"\", source.ToUpperInvariant(), source, nested]);

        Assert.IsTrue(receipt.MutationResult.Changed);
        Assert.AreEqual(3, receipt.MutationResult.RemovedDirectoryCount);
        CollectionAssert.AreEquivalent(
            new[] { sibling, keep },
            receipt.Snapshot.DirectoryLookupCache.Keys.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { source, nested, deep, sibling, keep },
            before.DirectoryLookupCache.Keys.ToArray());
    }

    /// <summary>
    /// 保持したentry順に対して、同一command内の削除slot再利用とfull/lazy逆引き順を確認する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReplaceSourceDirectoryWithScan_PreservesEntryOrderForLazyAndFull(bool full)
    {
        const uint hash = 73u;
        const string source = @"C:\Library\Source";
        string[] initial =
        [
            @"C:\Library\L",
            source + @"\P",
            @"C:\Library\M",
            source + @"\Q",
            @"C:\Library\R"
        ];
        string[] replacements = [@"C:\Library\X", @"C:\Library\Y"];
        LibraryResourceIndexOwner owner = new(CreateOrderedResourceIndex(full, hash, initial));
        LibraryResourceIndexSnapshot oldSnapshot = owner.CaptureSnapshot();
        ChartScanResult replacementScan = CreateOrderedScan(hash, replacements);

        LibraryResourceIndexMutationReceipt receipt = owner.ReplaceSourceDirectoryWithScan(
            source,
            replacementScan);

        string[] expected = full
            ? [initial[0], initial[2], initial[4], replacements[0], replacements[1]]
            : [initial[0], replacements[1], initial[2], replacements[0], initial[4]];
        AssertAllCategoryCandidates(receipt.Snapshot.DirectoryLookupCache, hash, expected);
        AssertAllCategoryCandidates(oldSnapshot.DirectoryLookupCache, hash, initial);
    }

    /// <summary>
    /// commandをまたぐforkでは過去の空slotを持ち越さず、削除後の追加を末尾順に保つ。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveThenAddScan_DiscardsPriorCommandFreeSlotsAndPreservesFinalOrder(bool full)
    {
        const uint hash = 73u;
        const string source = @"C:\Library\Source";
        string[] initial =
        [
            @"C:\Library\L",
            source + @"\P",
            @"C:\Library\M",
            source + @"\Q",
            @"C:\Library\R"
        ];
        string[] replacements = [@"C:\Library\X", @"C:\Library\Y"];
        LibraryResourceIndexOwner owner = new(CreateOrderedResourceIndex(full, hash, initial));
        LibraryResourceIndexSnapshot oldSnapshot = owner.CaptureSnapshot();
        ChartScanResult replacementScan = CreateOrderedScan(hash, replacements);

        LibraryResourceIndexMutationReceipt removed = owner.RemoveUnderSourceDirectories([source]);

        LibraryResourceIndexMutationReceipt added = owner.AddScanDirectories(replacementScan);
        string[] remaining = [initial[0], initial[2], initial[4]];
        string[] final = [.. remaining, replacements[0], replacements[1]];
        AssertAllCategoryCandidates(removed.Snapshot.DirectoryLookupCache, hash, remaining);
        AssertAllCategoryCandidates(added.Snapshot.DirectoryLookupCache, hash, final);
        AssertAllCategoryCandidates(oldSnapshot.DirectoryLookupCache, hash, initial);
    }

    [TestMethod]
    public void RemoveUnderSourceDirectories_EmptyAndMissingInputsDoNotPublishOrCopy()
    {
        var owner = new LibraryResourceIndexOwner(CreateFullResourceIndex(@"C:\Library\Keep"));
        LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
        var entryMutations = new List<string>();
        var writes = new List<uint>();
        before.DirectoryLookupCache.EntryStoreMutationObserver = path => entryMutations.Add(path);
        before.DirectoryLookupCache.ReverseBucketWrittenObserver = (_, hash) => writes.Add(hash);

        LibraryResourceIndexMutationReceipt empty = owner.RemoveUnderSourceDirectories([]);
        LibraryResourceIndexMutationReceipt missing = owner.RemoveUnderSourceDirectories(
            ["", " ", @"C:\Library\Missing", @"C:\Library\Missing"]);

        Assert.IsFalse(empty.MutationResult.Changed);
        Assert.IsFalse(missing.MutationResult.Changed);
        Assert.AreSame(before.Index, owner.CaptureSnapshot().Index);
        Assert.AreEqual(before.Generation, owner.CaptureSnapshot().Generation);
        Assert.AreEqual(0, entryMutations.Count);
        Assert.AreEqual(0, writes.Count);
    }

    [TestMethod]
    public void RemoveUnderSourceDirectories_EnumerationFailureDoesNotPublishItsPrefix()
    {
        const string first = @"C:\Library\First";
        const string second = @"C:\Library\Second";
        var owner = new LibraryResourceIndexOwner(CreateFullResourceIndex(first, second));
        LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
        var failure = new InvalidOperationException("confirmed-result enumeration failed");
        IEnumerable<string> FailingInput()
        {
            yield return first;
            throw failure;
        }

        Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() =>
            owner.RemoveUnderSourceDirectories(FailingInput())));

        LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
        Assert.AreSame(before.Index, after.Index);
        Assert.AreSame(before.DirectoryLookupCache, after.DirectoryLookupCache);
        Assert.AreEqual(before.Generation, after.Generation);
        CollectionAssert.AreEqual(new[] { first, second }, after.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(17u).ToArray());
    }

    [TestMethod]
    public void ResourcePackages_KeepPerPackageGenerationsAndEarlierCandidateSnapshots()
    {
        const string existing = @"C:\Library\Existing";
        const string first = @"C:\Library\Package1";
        const string second = @"C:\Library\Package2";
        var owner = new LibraryResourceIndexOwner(CreateFullResourceIndex(existing));
        LibraryResourceIndexSnapshot initial = owner.CaptureSnapshot();
        LibraryResourceIndexSnapshot one = owner.AddDirectory(first, [17u, 18u], [23u], [31u]).Snapshot;
        LibraryResourceIndexSnapshot two = owner.AddDirectory(second, [17u], [23u, 24u], [31u]).Snapshot;

        Assert.AreEqual(initial.Generation + 1, one.Generation);
        Assert.AreEqual(one.Generation + 1, two.Generation);
        CollectionAssert.AreEqual(new[] { existing }, initial.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(17u).ToArray());
        CollectionAssert.AreEqual(new[] { existing, first }, one.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(17u).ToArray());
        CollectionAssert.AreEqual(new[] { existing, first, second }, two.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(17u).ToArray());
        Assert.AreEqual(0, initial.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(18u).Count);
        CollectionAssert.AreEqual(new[] { first }, two.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(18u).ToArray());
        Assert.AreEqual(0, one.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(24u).Count);
        CollectionAssert.AreEqual(new[] { second }, two.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(24u).ToArray());
    }

    private static LibraryResourceIndex CreateFullResourceIndex(params string[] directories)
    {
        return LibraryResourceIndex.CreateFromNativeCanonicalArrays(
            directories,
            directories.Select(_ => new uint[] { 17u }).ToArray(),
            directories.Select(_ => new uint[] { 23u }).ToArray(),
            directories.Select(_ => new uint[] { 31u }).ToArray(),
            directories.Select(_ => new uint[] { 17u }).ToArray(),
            directories.Select(_ => new uint[] { 23u }).ToArray(),
            directories.Select(_ => new uint[] { 31u }).ToArray(),
            new Dictionary<uint, string[]> { [17u] = directories.ToArray() },
            new Dictionary<uint, string[]> { [23u] = directories.ToArray() },
            new Dictionary<uint, string[]> { [31u] = directories.ToArray() });
    }

    private static LibraryResourceIndex CreateOrderedResourceIndex(
        bool full,
        uint hash,
        params string[] directories)
    {
        if (full)
        {
            return LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                directories,
                directories.Select(_ => new[] { hash }).ToArray(),
                directories.Select(_ => new[] { hash }).ToArray(),
                directories.Select(_ => new[] { hash }).ToArray(),
                directories.Select(_ => new[] { hash }).ToArray(),
                directories.Select(_ => new[] { hash }).ToArray(),
                directories.Select(_ => new[] { hash }).ToArray(),
                new Dictionary<uint, string[]> { [hash] = directories.ToArray() },
                new Dictionary<uint, string[]> { [hash] = directories.ToArray() },
                new Dictionary<uint, string[]> { [hash] = directories.ToArray() });
        }

        return LibraryResourceIndex.CreateFromScanResult(CreateOrderedScan(hash, directories));
    }

    private static ChartScanResult CreateOrderedScan(uint hash, params string[] directories)
    {
        var scan = new ChartScanResult
        {
            ResourceHashArraysAreSortedDistinct = true
        };
        foreach (string directory in directories)
        {
            scan.ChartDirectories.Add(directory);
            scan.AudioRelativePathHashesByChartDirectory[directory] = [hash];
            scan.ImageRelativePathHashesByChartDirectory[directory] = [hash];
            scan.MovieRelativePathHashesByChartDirectory[directory] = [hash];
            scan.SelfOwnedAudioRelativePathHashesByChartDirectory[directory] = [hash];
            scan.SelfOwnedImageRelativePathHashesByChartDirectory[directory] = [hash];
            scan.SelfOwnedMovieRelativePathHashesByChartDirectory[directory] = [hash];
        }
        return scan;
    }

    private static void AssertAllCategoryCandidates(
        DirectoryResourceLookupCache cache,
        uint hash,
        string[] expected)
    {
        CollectionAssert.AreEqual(
            expected,
            cache.GetDirectoriesByAudioRelativeHash(hash).ToArray());
        CollectionAssert.AreEqual(
            expected,
            cache.GetDirectoriesByImageRelativeHash(hash).ToArray());
        CollectionAssert.AreEqual(
            expected,
            cache.GetDirectoriesByMovieRelativeHash(hash).ToArray());
    }

    private static LibraryResourceIndex CreateIndex(params string[] directories)
    {
        var index = new LibraryResourceIndex();
        foreach (string directory in directories ?? Array.Empty<string>())
        {
            index.DirectoryLookupCache.AddDir(directory, new[] { "sound.wav" });
        }
        return index;
    }

    private static LibraryResourceIndexMutationReceipt AddSixCategories(
        LibraryResourceIndexOwner owner,
        string directory,
        uint[][] categories)
    {
        return owner.AddDirectory(
            directory,
            categories[0],
            categories[1],
            categories[2],
            categories[3],
            categories[4],
            categories[5]);
    }

    private static uint[] GetCategory(DirectoryResourceLookupCache.Entry entry, int category)
    {
        return category switch
        {
            0 => entry.AudioRelativePathHashArray,
            1 => entry.ImageRelativePathHashArray,
            2 => entry.MovieRelativePathHashArray,
            3 => entry.SelfOwnedAudioRelativePathHashArray,
            4 => entry.SelfOwnedImageRelativePathHashArray,
            5 => entry.SelfOwnedMovieRelativePathHashArray,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
    }

    private sealed class ThrowingFileNames : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator()
        {
            yield return "sound.wav";
            throw new InvalidOperationException("enumeration failed");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class ThrowingDirectories : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator()
        {
            yield return @"C:\Library\Destination";
            throw new InvalidOperationException("directory enumeration failed");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
