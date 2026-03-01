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

    private static BMSTableEntry CreateEntry(string md5, string folder, string memo = "", DateTime? addDate = null, bool isRemoved = false)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry
        {
            folder = folder,
            memo = memo,
            adddate = addDate ?? new DateTime(2024, 1, 1, 0, 0, 0),
            is_removed = isRemoved
        };
        entry.SetMd5(md5);
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
    }
}
