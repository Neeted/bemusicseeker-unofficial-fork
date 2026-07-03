using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// プレイリスト再同期時の差分マージが、全リロードと個別リロードで同じ規則になることを検証します。
/// </summary>
[TestClass]
public sealed class PlaylistReloadMergeTests
{
    /// <summary>
    /// 再取得側に更新日時が無く差分も無い場合、既存の更新日時とローカル状態を維持することを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_NoStructuralChanges_PreservesLastUpdateAndLocalState()
    {
        var existingLastUpdate = new DateTime(2024, 5, 10, 11, 22, 33);
        var preservedAddDate = new DateTime(2023, 7, 15, 9, 30, 0);

        BMSTable oldTable = CreateTable(
            "Playlist A",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA", memo: "memo-a", addDate: preservedAddDate),
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "FolderB", isRemoved: true));
        BMSTable reloadedTable = CreateTable(
            "Playlist A",
            default,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update, "No-change reload must preserve the previous last_update.");

        BMSTableEntry keptEntry = mergedTable.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" && !entry.is_removed);
        Assert.AreEqual("memo-a", keptEntry.memo, "Matched entries must keep memo.");
        Assert.AreEqual(preservedAddDate, keptEntry.adddate, "Matched entries must keep adddate.");

        BMSTableEntry restoredRemovedEntry = mergedTable.entries.Single(entry => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" && entry.is_removed);
        Assert.AreEqual("FolderB", restoredRemovedEntry.folder, "Removed entries must be carried forward.");
    }

    /// <summary>
    /// entry fingerprint 差分だけでは更新日時を再採番しない一方、entry は永続化対象になることを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_EntryFingerprintOnlyChangeDoesNotRefreshLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 5, 10, 11, 22, 33);
        BMSTable oldTable = CreateTable(
            "Playlist B",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        BMSTable reloadedTable = CreateTable(
            "Playlist B",
            default,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"),
            CreateEntry("cccccccccccccccccccccccccccccccc", "FolderC"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_EntryFingerprintChangeWithUnchangedDataHashPersistsEntries()
    {
        var existingLastUpdate = new DateTime(2024, 5, 10, 11, 22, 33);
        string dataHash = new string('b', 64);
        BMSTable oldTable = CreateTable(
            "Playlist B Hash",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        oldTable.data_sha256 = dataHash;
        BMSTable reloadedTable = CreateTable(
            "Playlist B Hash",
            default,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"),
            CreateEntry("dddddddddddddddddddddddddddddddd", "FolderD"));
        reloadedTable.data_sha256 = dataHash;

        BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.DataKnownChanged);
        Assert.IsFalse(decision.DataHashInitialized);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.IsTrue(decision.NeedsEntryPersistence);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_Sha256Fallback_PreservesLocalState()
    {
        var preservedAddDate = new DateTime(2023, 8, 20, 12, 34, 56);
        BMSTable oldTable = CreateTable(
            "Playlist C",
            new DateTime(2024, 5, 10, 11, 22, 33),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA", sha256: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", memo: "memo-c", addDate: preservedAddDate, isRemoved: true));
        BMSTable reloadedTable = CreateTable(
            "Playlist C",
            default,
            CreateEntry(null, "FolderA", sha256: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single(entry => entry.sha256 == "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" && entry.folder == "FolderA");

        Assert.IsNull(matchedEntry.md5);
        Assert.AreEqual("memo-c", matchedEntry.memo);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
        Assert.IsFalse(matchedEntry.is_removed);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_TitleOnlyComparableRow_PreservesLastUpdateAndLocalState()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        var preservedAddDate = new DateTime(2023, 8, 20, 12, 34, 56);
        BMSTable oldTable = CreateTable(
            "Playlist D",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same", addDate: preservedAddDate));
        BMSTable reloadedTable = CreateTable(
            "Playlist D",
            default,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single(entry => entry.title == "Title Only" && !entry.is_removed);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_CommentChangeWithoutDataHashChangeDoesNotRefreshLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist E",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "old"));
        BMSTable reloadedTable = CreateTable(
            "Playlist E",
            default,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "new"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_HashInitializationPersistsWithoutRefreshingLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist Hash Init",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        oldTable.playlist_id = 1;
        BMSTable reloadedTable = CreateTable(
            "Playlist Hash Init",
            default,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        reloadedTable.header_sha256 = new string('a', 64);
        reloadedTable.data_sha256 = new string('b', 64);

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges,
            out bool hasStateToPersist);

        Assert.IsFalse(hasContentChanges);
        Assert.IsTrue(hasStateToPersist);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_HeaderHashInitializationPersistsHeaderOnly()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist Header Hash Init",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        oldTable.playlist_id = 1;
        BMSTable reloadedTable = CreateTable(
            "Playlist Header Hash Init",
            default,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        reloadedTable.header_sha256 = new string('a', 64);

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.HeaderHashInitialized);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsFalse(decision.NeedsEntryPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_DataHashInitializationPersistsEntriesWithoutRefreshingLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist Data Hash Init",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        oldTable.playlist_id = 1;
        BMSTable reloadedTable = CreateTable(
            "Playlist Data Hash Init",
            default,
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "FolderB"));
        reloadedTable.data_sha256 = new string('b', 64);

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.DataHashInitialized);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
        Assert.IsTrue(mergedTable.entries.Any(entry => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_KnownHeaderHashChangeRefreshesLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist Hash Change",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        oldTable.playlist_id = 1;
        oldTable.header_sha256 = new string('a', 64);
        BMSTable reloadedTable = CreateTable(
            "Playlist Hash Change",
            default,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        reloadedTable.header_sha256 = new string('b', 64);

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges,
            out bool hasStateToPersist,
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(hasContentChanges);
        Assert.IsTrue(hasStateToPersist);
        Assert.IsTrue(decision.HeaderKnownChanged);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsFalse(decision.NeedsEntryPersistence);
        Assert.IsTrue(mergedTable.last_update > existingLastUpdate);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_LegacyRawHeaderHashMigrationDoesNotRefreshLastUpdate()
    {
        string headerJson = "{\"name\":\"Playlist Hash Migration\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"score.json\",\"level_order\":[1]}";
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist Hash Migration",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "EXTERNAL 1"));
        oldTable.playlist_id = 1;
        oldTable.header_sha256 = BMSTable.ComputeSha256Hex(headerJson);
        BMSTable reloadedTable = new();
        reloadedTable.LoadHeaderJSON(headerJson);
        reloadedTable.entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "EXTERNAL 1")];

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges,
            out bool hasStateToPersist,
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);

        Assert.IsFalse(hasContentChanges);
        Assert.IsTrue(hasStateToPersist);
        Assert.IsFalse(decision.HeaderKnownChanged);
        Assert.IsTrue(decision.HeaderHashMigrated);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_KnownDataHashChangeRefreshesLastUpdateAndPersistsEntries()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist Data Hash Change",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        oldTable.playlist_id = 1;
        oldTable.data_sha256 = new string('a', 64);
        BMSTable reloadedTable = CreateTable(
            "Playlist Data Hash Change",
            default,
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "FolderB"));
        reloadedTable.data_sha256 = new string('b', 64);

        DateTime beforeMerge = DateTime.Now;
        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out BMSPlaylist.PlaylistReloadPersistenceDecision decision);
        DateTime afterMerge = DateTime.Now;

        Assert.IsTrue(decision.DataKnownChanged);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.IsTrue(decision.UpdatesLastUpdate);
        Assert.IsTrue(mergedTable.last_update >= beforeMerge && mergedTable.last_update <= afterMerge);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_FolderDifferenceOnly_DoesNotRefreshLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist F",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "LOCAL Prefix"));
        BMSTable reloadedTable = CreateTable(
            "Playlist F",
            default,
            CreateComparableOnlyEntry("Title Only", "Artist", "Remote Folder"));

        Assert.AreEqual(
            BMSPlaylist.CreateComparablePlaylistEntryRow(oldTable.entries.Single())?.Fingerprint,
            BMSPlaylist.CreateComparablePlaylistEntryRow(reloadedTable.entries.Single())?.Fingerprint);
        Assert.IsFalse(BMSPlaylist.HasPlaylistContentChanges(
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            BMSPlaylist.BuildComparablePlaylistEntryRows(reloadedTable.entries)));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges);

        Assert.IsFalse(hasContentChanges);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_OrgMd5DifferenceOnly_DoesNotRefreshLastUpdate()
    {
        var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist G",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", orgMd5: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        BMSTable reloadedTable = CreateTable(
            "Playlist G",
            default,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA"));

        Assert.AreEqual(
            BMSPlaylist.CreateComparablePlaylistEntryRow(oldTable.entries.Single())?.Fingerprint,
            BMSPlaylist.CreateComparablePlaylistEntryRow(reloadedTable.entries.Single())?.Fingerprint);
        Assert.IsFalse(BMSPlaylist.HasPlaylistContentChanges(
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            BMSPlaylist.BuildComparablePlaylistEntryRows(reloadedTable.entries)));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges);

        Assert.IsFalse(hasContentChanges);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CreateComparablePlaylistEntryRow_NullControlCharacters_AreIgnoredForFingerprint()
    {
        BMSTableEntry cleanEntry = CreateComparableOnlyEntry("Crash || Nothing\r\n [", "Artist", "FolderA", comment: "memo");
        BMSTableEntry dirtyEntry = CreateComparableOnlyEntry("Crash || Nothing\r\n [\0]\r", "Artist", "FolderA", comment: "memo\0");

        Assert.AreEqual(
            BMSPlaylist.CreateComparablePlaylistEntryRow(cleanEntry)?.Fingerprint,
            BMSPlaylist.CreateComparablePlaylistEntryRow(dirtyEntry)?.Fingerprint);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_RemovedEntryReappearsInJson_BecomesActiveAndPreservesLocalState()
    {
        var preservedAddDate = new DateTime(2023, 8, 20, 12, 34, 56);
        BMSTable oldTable = CreateTable(
            "Playlist H",
            new DateTime(2024, 5, 10, 11, 22, 33),
            CreateEntry("f2eac2c1eb70512eb9785aa33bfc817e", "2026/03", memo: "revived", addDate: preservedAddDate, isRemoved: true));
        BMSTable reloadedTable = CreateTable(
            "Playlist H",
            default,
            CreateEntry("f2eac2c1eb70512eb9785aa33bfc817e", "2026/03"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single(entry => entry.md5 == "f2eac2c1eb70512eb9785aa33bfc817e");

        Assert.AreEqual("revived", matchedEntry.memo);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
        Assert.IsFalse(matchedEntry.is_removed);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_NoDifference_ReturnsEmptySamples()
    {
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> rows = BMSPlaylist.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same")
        ]);

        BMSPlaylist.PlaylistContentDiffResult result = BMSPlaylist.AnalyzePlaylistContentDiff(rows, rows);

        Assert.IsFalse(result.HasChanges);
        Assert.AreEqual(0, result.PersistedOnlyCount);
        Assert.AreEqual(0, result.ReloadedOnlyCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), result.PersistedOnlySamples.ToArray());
        CollectionAssert.AreEqual(Array.Empty<string>(), result.ReloadedOnlySamples.ToArray());
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_CommentDifference_ReturnsOneSamplePerSide()
    {
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> persistedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "old")
        ]);
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> reloadedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "new")
        ]);

        BMSPlaylist.PlaylistContentDiffResult result = BMSPlaylist.AnalyzePlaylistContentDiff(persistedRows, reloadedRows);

        Assert.IsTrue(result.HasChanges);
        Assert.AreEqual(1, result.PersistedOnlyCount);
        Assert.AreEqual(1, result.ReloadedOnlyCount);
        Assert.AreEqual(1, result.PersistedOnlySamples.Count);
        Assert.AreEqual(1, result.ReloadedOnlySamples.Count);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_SampleCount_IsLimitedToThree()
    {
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> persistedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title 1", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 2", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 3", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 4", "Artist", "FolderA", comment: "old")
        ]);
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> reloadedRows = BMSPlaylist.BuildComparablePlaylistEntryRows([]);

        BMSPlaylist.PlaylistContentDiffResult result = BMSPlaylist.AnalyzePlaylistContentDiff(persistedRows, reloadedRows);

        Assert.IsTrue(result.HasChanges);
        Assert.AreEqual(4, result.PersistedOnlyCount);
        Assert.AreEqual(0, result.ReloadedOnlyCount);
        Assert.AreEqual(3, result.PersistedOnlySamples.Count);
        CollectionAssert.AreEqual(result.PersistedOnlySamples.OrderBy(item => item, StringComparer.Ordinal).ToArray(), result.PersistedOnlySamples.ToArray());
    }

    private static BMSTable CreateTable(string name, DateTime lastUpdate, params BMSTableEntry[] entries)
    {
        var table = new BMSTable
        {
            name = name,
            last_update = lastUpdate,
            entries = [.. entries]
        };
        return table;
    }

    private static BMSTableEntry CreateEntry(string? md5, string folder, string? sha256 = null, string memo = "", DateTime? addDate = null, bool isRemoved = false)
    {
        var entry = new TestablePlaylistEntry
        {
            folder = folder,
            memo = memo,
            adddate = addDate ?? new DateTime(2024, 1, 1, 0, 0, 0),
            is_removed = isRemoved
        };
        if (md5 != null)
        {
            entry.SetMd5(md5);
        }
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return entry;
    }

    private static BMSTableEntry CreateComparableOnlyEntry(string title, string artist, string folder, string comment = "", DateTime? addDate = null, string? orgMd5 = null)
    {
        var entry = new TestablePlaylistEntry
        {
            folder = folder,
            adddate = addDate ?? new DateTime(2024, 1, 1, 0, 0, 0)
        };
        entry.SetTitle(title);
        entry.SetArtist(artist);
        entry.SetComment(comment);
        if (!string.IsNullOrWhiteSpace(orgMd5))
        {
            entry.Org_md5.Add(orgMd5);
        }
        return entry;
    }

    /// <summary>
    /// テスト用に protected setter へ値を流し込むための派生型です。
    /// </summary>
    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        /// <summary>
        /// MD5 を初期化します。
        /// </summary>
        /// <param name="value">設定する MD5。</param>
        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }

        public void SetArtist(string value)
        {
            artist = value;
        }

        public void SetComment(string value)
        {
            comment = value;
        }
    }
}
