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
        DateTime existingLastUpdate = new DateTime(2024, 5, 10, 11, 22, 33);
        DateTime preservedAddDate = new DateTime(2023, 7, 15, 9, 30, 0);

        BMSTable oldTable = CreateTable(
            "Playlist A",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA", memo: "memo-a", addDate: preservedAddDate),
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "FolderB", isRemoved: true));
        BMSTable reloadedTable = CreateTable(
            "Playlist A",
            default(DateTime),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update, "No-change reload must preserve the previous last_update.");

        BMSTableEntry keptEntry = mergedTable.entries.Single((BMSTableEntry entry) => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" && !entry.is_removed);
        Assert.AreEqual("memo-a", keptEntry.memo, "Matched entries must keep memo.");
        Assert.AreEqual(preservedAddDate, keptEntry.adddate, "Matched entries must keep adddate.");

        BMSTableEntry restoredRemovedEntry = mergedTable.entries.Single((BMSTableEntry entry) => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" && entry.is_removed);
        Assert.AreEqual("FolderB", restoredRemovedEntry.folder, "Removed entries must be carried forward.");
    }

    /// <summary>
    /// 再取得側に更新日時が無く構成差分がある場合、現在時刻で更新日時を再採番することを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_StructuralChanges_RefreshesLastUpdate()
    {
        DateTime existingLastUpdate = new DateTime(2024, 5, 10, 11, 22, 33);
        BMSTable oldTable = CreateTable(
            "Playlist B",
            existingLastUpdate,
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"));
        BMSTable reloadedTable = CreateTable(
            "Playlist B",
            default(DateTime),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA"),
            CreateEntry("cccccccccccccccccccccccccccccccc", "FolderC"));

        DateTime beforeMerge = DateTime.Now;
        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        DateTime afterMerge = DateTime.Now;

        Assert.AreNotEqual(default(DateTime), mergedTable.last_update, "Changed reload must not leave last_update at default.");
        Assert.IsTrue(mergedTable.last_update > existingLastUpdate, "Changed reload must move last_update forward.");
        Assert.IsTrue(mergedTable.last_update >= beforeMerge && mergedTable.last_update <= afterMerge, "Changed reload must stamp the current local time.");
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_Sha256Fallback_PreservesLocalState()
    {
        DateTime preservedAddDate = new DateTime(2023, 8, 20, 12, 34, 56);
        BMSTable oldTable = CreateTable(
            "Playlist C",
            new DateTime(2024, 5, 10, 11, 22, 33),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "FolderA", sha256: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", memo: "memo-c", addDate: preservedAddDate, isRemoved: true));
        BMSTable reloadedTable = CreateTable(
            "Playlist C",
            default(DateTime),
            CreateEntry(null, "FolderA", sha256: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single((BMSTableEntry entry) => entry.sha256 == "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" && entry.folder == "FolderA");

        Assert.IsNull(matchedEntry.md5);
        Assert.AreEqual("memo-c", matchedEntry.memo);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
        Assert.IsFalse(matchedEntry.is_removed);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_TitleOnlyComparableRow_PreservesLastUpdateAndLocalState()
    {
        DateTime existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        DateTime preservedAddDate = new DateTime(2023, 8, 20, 12, 34, 56);
        BMSTable oldTable = CreateTable(
            "Playlist D",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same", addDate: preservedAddDate));
        BMSTable reloadedTable = CreateTable(
            "Playlist D",
            default(DateTime),
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single((BMSTableEntry entry) => entry.title == "Title Only" && !entry.is_removed);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_CommentChange_RefreshesLastUpdate()
    {
        DateTime existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist E",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "old"));
        BMSTable reloadedTable = CreateTable(
            "Playlist E",
            default(DateTime),
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "new"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);

        Assert.IsTrue(mergedTable.last_update > existingLastUpdate);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_FolderDifferenceOnly_DoesNotRefreshLastUpdate()
    {
        DateTime existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist F",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "LOCAL Prefix"));
        BMSTable reloadedTable = CreateTable(
            "Playlist F",
            default(DateTime),
            CreateComparableOnlyEntry("Title Only", "Artist", "Remote Folder"));

        Assert.AreEqual(
            BMSPlaylist.CreateComparablePlaylistEntryRow(oldTable.entries.Single())?.Fingerprint,
            BMSPlaylist.CreateComparablePlaylistEntryRow(reloadedTable.entries.Single())?.Fingerprint);
        Assert.IsFalse(BMSPlaylist.HasPlaylistContentChanges(
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)),
            BMSPlaylist.BuildComparablePlaylistEntryRows(reloadedTable.entries)));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)),
            out bool hasContentChanges);

        Assert.IsFalse(hasContentChanges);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_OrgMd5DifferenceOnly_DoesNotRefreshLastUpdate()
    {
        DateTime existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable oldTable = CreateTable(
            "Playlist G",
            existingLastUpdate,
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", orgMd5: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        BMSTable reloadedTable = CreateTable(
            "Playlist G",
            default(DateTime),
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA"));

        Assert.AreEqual(
            BMSPlaylist.CreateComparablePlaylistEntryRow(oldTable.entries.Single())?.Fingerprint,
            BMSPlaylist.CreateComparablePlaylistEntryRow(reloadedTable.entries.Single())?.Fingerprint);
        Assert.IsFalse(BMSPlaylist.HasPlaylistContentChanges(
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)),
            BMSPlaylist.BuildComparablePlaylistEntryRows(reloadedTable.entries)));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            BMSPlaylist.BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)),
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
        DateTime preservedAddDate = new DateTime(2023, 8, 20, 12, 34, 56);
        BMSTable oldTable = CreateTable(
            "Playlist H",
            new DateTime(2024, 5, 10, 11, 22, 33),
            CreateEntry("f2eac2c1eb70512eb9785aa33bfc817e", "2026/03", memo: "revived", addDate: preservedAddDate, isRemoved: true));
        BMSTable reloadedTable = CreateTable(
            "Playlist H",
            default(DateTime),
            CreateEntry("f2eac2c1eb70512eb9785aa33bfc817e", "2026/03"));

        BMSTable mergedTable = BMSPlaylist.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single((BMSTableEntry entry) => entry.md5 == "f2eac2c1eb70512eb9785aa33bfc817e");

        Assert.AreEqual("revived", matchedEntry.memo);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
        Assert.IsFalse(matchedEntry.is_removed);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_NoDifference_ReturnsEmptySamples()
    {
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> rows = BMSPlaylist.BuildComparablePlaylistEntryRows(new[]
        {
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same")
        });

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
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> persistedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(new[]
        {
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "old")
        });
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> reloadedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(new[]
        {
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "new")
        });

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
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> persistedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(new[]
        {
            CreateComparableOnlyEntry("Title 1", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 2", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 3", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 4", "Artist", "FolderA", comment: "old")
        });
        IReadOnlyList<BMSPlaylist.ComparablePlaylistEntryRow> reloadedRows = BMSPlaylist.BuildComparablePlaylistEntryRows(Array.Empty<BMSTableEntry>());

        BMSPlaylist.PlaylistContentDiffResult result = BMSPlaylist.AnalyzePlaylistContentDiff(persistedRows, reloadedRows);

        Assert.IsTrue(result.HasChanges);
        Assert.AreEqual(4, result.PersistedOnlyCount);
        Assert.AreEqual(0, result.ReloadedOnlyCount);
        Assert.AreEqual(3, result.PersistedOnlySamples.Count);
        CollectionAssert.AreEqual(result.PersistedOnlySamples.OrderBy((string item) => item, StringComparer.Ordinal).ToArray(), result.PersistedOnlySamples.ToArray());
    }

    private static BMSTable CreateTable(string name, DateTime lastUpdate, params BMSTableEntry[] entries)
    {
        BMSTable table = new BMSTable
        {
            name = name,
            last_update = lastUpdate
        };
        table.entries = entries.ToList();
        return table;
    }

    private static BMSTableEntry CreateEntry(string md5, string folder, string sha256 = null, string memo = "", DateTime? addDate = null, bool isRemoved = false)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry
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

    private static BMSTableEntry CreateComparableOnlyEntry(string title, string artist, string folder, string comment = "", DateTime? addDate = null, string orgMd5 = null)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry
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
