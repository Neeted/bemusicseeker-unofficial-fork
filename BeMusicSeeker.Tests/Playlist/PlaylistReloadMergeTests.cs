using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>再取得テーブルとローカル状態を統合する保存・更新日時の判断を検証する。</summary>
/// <remarks>全体・個別再同期が共用する統合判断を小さい入力で網羅する。実DB保存・画面公開・取消は再読込み所有者のテストへ分担する。</remarks>
[TestClass]
public sealed class PlaylistReloadMergeTests
{
    /// <summary>空行・全絶対URL行で本文hashが同じでもdata基準差を保存し、path/queryのcaseを捨てる比較と日時・entryの過剰保存を検出します。</summary>
    [DataTestMethod]
    [DataRow("https://example.test/old/rows.json", "https://example.test/new/rows.json", false)]
    [DataRow("https://example.test/old/rows.json", "https://example.test/new/rows.json", true)]
    [DataRow("https://example.test/rows/A.json", "https://example.test/rows/a.json", true)]
    [DataRow("https://example.test/rows.json?token=A", "https://example.test/rows.json?token=a", true)]
    [DataRow("https://example.test/rows.json?Key=1", "https://example.test/rows.json?key=1", true)]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_EffectiveDataUriChangeSavesHeaderWithoutDateOrEntries(string oldData, string newData, bool hasEntry)
    {
        BMSTable original = CreateUriContextTable(hasEntry);
        BMSTable reloaded = CreateUriContextTable(hasEntry);
        original.Header_url = reloaded.Header_url = new Uri("https://example.test/header.json");
        original.Data_url = new Uri(oldData);
        reloaded.Data_url = new Uri(newData);

        BMSTable merged = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(original, reloaded,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(original.entries), out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EffectiveDataUriChanged);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsTrue(decision.NeedsStatePersistence);
        Assert.IsTrue(decision.NeedsBmtExport);
        Assert.IsFalse(decision.NeedsEntryPersistence);
        Assert.IsFalse(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(original.last_update, merged.last_update);
        Assert.AreEqual(original.header_sha256, merged.header_sha256);
        Assert.AreEqual(original.data_sha256, merged.data_sha256);
        Assert.AreEqual(new Uri(newData), merged.GetAbsoluteDataUrl());
    }

    /// <summary>相対/絶対表記、scheme/host、fragmentとfile/UNCの同参照は保存を増やさず、HTTP専用化や文字列表現の比較への退行を検出します。</summary>
    [DataTestMethod]
    [DataRow("https://example.test/defs/header.json", "rows.json", "https://example.test/defs/rows.json")]
    [DataRow("https://example.test/defs/header.json", "https://EXAMPLE.test/rows.json#before", "HTTPS://example.test/rows.json#after")]
    [DataRow("file:///C:/tables/defs/header.json", "../rows.json", "file:///C:/tables/rows.json")]
    [DataRow("file://server/share/defs/header.json", "../rows.json", "file://server/share/rows.json")]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_EquivalentDataUriDoesNotPersist(string header, string oldData, string newData)
    {
        BMSTable original = CreateUriContextTable(hasEntry: true);
        BMSTable reloaded = CreateUriContextTable(hasEntry: true);
        original.Header_url = reloaded.Header_url = new Uri(header);
        original.Data_url = new Uri(oldData, UriKind.RelativeOrAbsolute);
        reloaded.Data_url = new Uri(newData, UriKind.RelativeOrAbsolute);

        PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(original, reloaded,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(original.entries), out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsFalse(decision.EffectiveDataUriChanged);
        Assert.IsFalse(decision.NeedsStatePersistence);
        Assert.IsFalse(decision.NeedsBmtExport);
    }

    /// <summary>絶対dataが同じ場合の無関係なpage/header差と、data文脈なしの手動・内蔵表は従来の保存判断へ委ね、URI三項全体の過剰比較を検出します。</summary>
    [DataTestMethod]
    [DataRow("https://example.test/page", "https://elsewhere.test/page", true)]
    [DataRow(null, null, false)]
    [DataRow("bmseeker://recommendation/before", "bmseeker://recommendation/after", false)]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_UnrelatedSourceContextDoesNotPersist(string? oldPage, string? newPage, bool hasData)
    {
        BMSTable original = CreateUriContextTable(hasEntry: false);
        BMSTable reloaded = CreateUriContextTable(hasEntry: false);
        original.Page_url = oldPage == null ? null : new Uri(oldPage);
        reloaded.Page_url = newPage == null ? null : new Uri(newPage);
        if (hasData)
        {
            original.Header_url = new Uri("https://example.test/old/header.json");
            reloaded.Header_url = new Uri("https://elsewhere.test/new/header.json");
            original.Data_url = reloaded.Data_url = new Uri("https://data.test/rows.json");
        }

        PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(original, reloaded,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(original.entries), out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsFalse(decision.EffectiveDataUriChanged);
        Assert.IsFalse(decision.NeedsStatePersistence);
        Assert.IsFalse(decision.NeedsBmtExport);
    }

    /// <summary>URI以外に差のない、本文hashが既知の表を作ります。絶対entryはdata基準差と行差を分離します。</summary>
    private static BMSTable CreateUriContextTable(bool hasEntry)
    {
        BMSTable table = CreateTable("URI context", new DateTime(2024, 6, 1, 10, 20, 30));
        table.playlist_id = 17;
        table.org_name = table.name;
        table.header_sha256 = new string('b', 64);
        table.data_sha256 = new string('c', 64);
        if (hasEntry)
        {
            BMSTableEntry entry = CreateEntry(new string('a', 32), "1");
            entry.Url = new Uri("https://packages.test/song.zip");
            entry.Url_diff = new Uri("https://packages.test/diff.zip");
            table.entries = [entry];
        }
        return table;
    }

    /// <summary>表示名だけ・取得元名だけ・どちらも不変の条件でヘッダー保存を選び、譜面保存と更新日時の再採番を行わない。名前変更の保存漏れと日時の誤更新を検出する。</summary>
    [DataTestMethod]
    // 表示名だけの変更、取得元名だけの変更、両方不変でヘッダー保存判断を分ける。
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [TestCategory("Playlist")]
    public void MergeReloadedBMSTableState_NameDifferencesSaveOnlyHeaderAndPreserveDate(bool changeName, bool changeOrgName)
    {
        var lastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
        BMSTable original = CreateTable("自動名 ★6.60", lastUpdate, CreateEntry(new string('a', 32), "NORMAL"));
        original.playlist_id = 17;
        original.org_name = "自動名 ★6.60";
        original.header_sha256 = new string('b', 64);
        original.data_sha256 = new string('c', 64);
        BMSTable reloaded = CreateTable(changeName ? "手動名" : original.name, default, CreateEntry(new string('a', 32), "NORMAL"));
        reloaded.org_name = changeOrgName ? "旧方針 ★6.6" : original.org_name;
        reloaded.header_sha256 = original.header_sha256;
        reloaded.data_sha256 = original.data_sha256;
        BMSTable merged = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(original, reloaded,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(original.entries), out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);
        Assert.AreEqual(changeName || changeOrgName, decision.NeedsHeaderPersistence);
        Assert.AreEqual(changeName || changeOrgName, decision.NeedsStatePersistence);
        Assert.IsFalse(decision.NeedsEntryPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(lastUpdate, merged.last_update);
    }

    /// <summary>同じ外部内容の再取得では旧更新日時・メモ・追加日時と過去の削除行を保持する。再同期による利用者状態の損失を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(oldTable, reloadedTable);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update, "内容不変の再取得では旧更新日時を維持する。");

        BMSTableEntry keptEntry = mergedTable.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" && !entry.is_removed);
        Assert.AreEqual("memo-a", keptEntry.memo, "一致行のメモを維持する。");
        Assert.AreEqual(preservedAddDate, keptEntry.adddate, "一致行の追加日時を維持する。");

        BMSTableEntry restoredRemovedEntry = mergedTable.entries.Single(entry => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" && entry.is_removed);
        Assert.AreEqual("FolderB", restoredRemovedEntry.folder, "過去の削除行を引き継ぐ。");
    }

    /// <summary>ハッシュ未初期化でも新エントリの内容差を保存対象にし、更新日時は維持する。内容差の脱落とハッシュ変更なしの日時更新を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>dataハッシュが同じでもエントリ内容差を保存し、ハッシュの変更・初期化や日時更新とは扱わない。ハッシュ一致による譜面変更の取りこぼしを検出する。</summary>
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

        PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.DataKnownChanged);
        Assert.IsFalse(decision.DataHashInitialized);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.IsTrue(decision.NeedsEntryPersistence);
    }

    /// <summary>MD5が欠けた再取得行をSHAで旧削除行へ対応させ、メモ・追加日時を保持して有効化する。SHA照合漏れとローカル状態の損失を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single(entry => entry.sha256 == "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" && entry.folder == "FolderA");

        Assert.IsNull(matchedEntry.md5);
        Assert.AreEqual("memo-c", matchedEntry.memo);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
        Assert.IsFalse(matchedEntry.is_removed);
    }

    /// <summary>ハッシュなしで同じタイトル・作者・コメントの行は旧追加日時と表更新日時を保持する。ハッシュ必須の照合や内容不変の誤更新を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single(entry => entry.title == "Title Only" && !entry.is_removed);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
    }

    /// <summary>ハッシュなし行のコメント差は内容変更として保存するが日時は更新しない。コメントの比較漏れと日時条件の混同を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.EntryFingerprintChanged);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>未設定のheader・dataハッシュを初期化すると状態保存が必要になり、外部内容更新とは扱わず旧日時を保持する。初期化を更新通知の根拠にする誤りを検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges,
            out bool hasStateToPersist);

        Assert.IsFalse(hasContentChanges);
        Assert.IsTrue(hasStateToPersist);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>headerハッシュだけの初期化はヘッダーだけを保存し、譜面行・更新日時を変えない。保存範囲の過剰拡大を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.HeaderHashInitialized);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsFalse(decision.NeedsEntryPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>dataハッシュ初期化と新エントリはヘッダー・譜面行を保存して旧日時を保持する。新譜面の保存漏れと初回ハッシュの誤更新扱いを検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(decision.DataHashInitialized);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
        Assert.IsTrue(mergedTable.entries.Any(entry => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    /// <summary>既知headerハッシュが変わると内容更新としてヘッダー保存と日時更新を行い、譜面行は保存し直さない。ヘッダー更新の未反映と余計な譜面保存を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges,
            out bool hasStateToPersist,
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsTrue(hasContentChanges);
        Assert.IsTrue(hasStateToPersist);
        Assert.IsTrue(decision.HeaderKnownChanged);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsFalse(decision.NeedsEntryPersistence);
        Assert.IsTrue(mergedTable.last_update > existingLastUpdate);
    }

    /// <summary>同じJSONの旧rawハッシュを現行の正規化headerハッシュへ移行して保存し、内容更新や日時更新とは扱わない。互換移行による偽の更新検知を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges,
            out bool hasStateToPersist,
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);

        Assert.IsFalse(hasContentChanges);
        Assert.IsTrue(hasStateToPersist);
        Assert.IsFalse(decision.HeaderKnownChanged);
        Assert.IsTrue(decision.HeaderHashMigrated);
        Assert.IsTrue(decision.NeedsHeaderPersistence);
        Assert.IsFalse(decision.UpdatesLastUpdate);
        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>既知dataハッシュの変化は譜面保存と処理時刻への日時更新を必要にする。外部譜面更新の未保存や取得時刻の残存を検出する。</summary>
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
        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision decision);
        DateTime afterMerge = DateTime.Now;

        Assert.IsTrue(decision.DataKnownChanged);
        Assert.IsTrue(decision.NeedsEntryPersistence);
        Assert.IsTrue(decision.UpdatesLastUpdate);
        Assert.IsTrue(mergedTable.last_update >= beforeMerge && mergedTable.last_update <= afterMerge);
    }

    /// <summary>表示フォルダだけの差は外部譜面内容の変更として扱わず、表更新日時を保持する。ローカル配置名で外部更新を誤検出する処理を検出する。</summary>
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
            PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(oldTable.entries.Single())?.Fingerprint,
            PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(reloadedTable.entries.Single())?.Fingerprint);
        Assert.IsFalse(PlaylistAggregatePersistenceOwner.HasPlaylistContentChanges(
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(reloadedTable.entries)));

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges);

        Assert.IsFalse(hasContentChanges);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>Org_md5の補完差だけでは外部内容を変更扱いせず、更新日時を保持する。所持由来の補完情報を更新判定へ混ぜる誤りを検出する。</summary>
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
            PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(oldTable.entries.Single())?.Fingerprint,
            PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(reloadedTable.entries.Single())?.Fingerprint);
        Assert.IsFalse(PlaylistAggregatePersistenceOwner.HasPlaylistContentChanges(
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(reloadedTable.entries)));

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
            oldTable,
            reloadedTable,
            PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)),
            out bool hasContentChanges);

        Assert.IsFalse(hasContentChanges);

        Assert.AreEqual(existingLastUpdate, mergedTable.last_update);
    }

    /// <summary>タイトルのNUL以降と末尾NUL付きコメントは同じ比較内容になる。旧SQLite文字列と再取得JSONの表現差で偽の更新を検出する誤りを防ぐ。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CreateComparablePlaylistEntryRow_IgnoresSuffixAfterNullCharacter()
    {
        BMSTableEntry cleanEntry = CreateComparableOnlyEntry("Crash || Nothing\r\n [", "Artist", "FolderA", comment: "memo");
        BMSTableEntry dirtyEntry = CreateComparableOnlyEntry("Crash || Nothing\r\n [\0]\r", "Artist", "FolderA", comment: "memo\0");

        Assert.AreEqual(
            PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(cleanEntry)?.Fingerprint,
            PlaylistAggregatePersistenceOwner.CreateComparablePlaylistEntryRow(dirtyEntry)?.Fingerprint);
    }

    /// <summary>外部JSONへ同じMD5が復活すると削除状態を解除し、メモと追加日時を保持する。復活の拒否と旧利用者情報の消失を検出する。</summary>
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

        BMSTable mergedTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(oldTable, reloadedTable);
        BMSTableEntry matchedEntry = mergedTable.entries.Single(entry => entry.md5 == "f2eac2c1eb70512eb9785aa33bfc817e");

        Assert.AreEqual("revived", matchedEntry.memo);
        Assert.AreEqual(preservedAddDate, matchedEntry.adddate);
        Assert.IsFalse(matchedEntry.is_removed);
    }

    /// <summary>同じ行集合は差分なし・両側0件・診断見本なしと判定する。内容不変時の誤更新と架空の診断を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_NoDifference_ReturnsEmptySamples()
    {
        IReadOnlyList<PlaylistAggregatePersistenceOwner.ComparablePlaylistEntryRow> rows = PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "same")
        ]);

        PlaylistAggregatePersistenceOwner.PlaylistContentDiffResult result = PlaylistAggregatePersistenceOwner.AnalyzePlaylistContentDiff(rows, rows);

        Assert.IsFalse(result.HasChanges);
        Assert.AreEqual(0, result.PersistedOnlyCount);
        Assert.AreEqual(0, result.ReloadedOnlyCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), result.PersistedOnlySamples.ToArray());
        CollectionAssert.AreEqual(Array.Empty<string>(), result.ReloadedOnlySamples.ToArray());
    }

    /// <summary>同じ行のコメント変更を旧側・新側の各1差分として数える。一方だけの差分計数とコメント比較漏れを検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_CommentDifferenceCountsOneDifferencePerSide()
    {
        IReadOnlyList<PlaylistAggregatePersistenceOwner.ComparablePlaylistEntryRow> persistedRows = PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "old")
        ]);
        IReadOnlyList<PlaylistAggregatePersistenceOwner.ComparablePlaylistEntryRow> reloadedRows = PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title Only", "Artist", "FolderA", comment: "new")
        ]);

        PlaylistAggregatePersistenceOwner.PlaylistContentDiffResult result = PlaylistAggregatePersistenceOwner.AnalyzePlaylistContentDiff(persistedRows, reloadedRows);

        Assert.IsTrue(result.HasChanges);
        Assert.AreEqual(1, result.PersistedOnlyCount);
        Assert.AreEqual(1, result.ReloadedOnlyCount);
    }

    /// <summary>旧側だけにある複数行の差分を4件、反対側を0件として全数集計する。診断の見本数へ差分全数を切り詰める誤りを検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void AnalyzePlaylistContentDiff_KeepsTotalDifferenceCount()
    {
        IReadOnlyList<PlaylistAggregatePersistenceOwner.ComparablePlaylistEntryRow> persistedRows = PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(
        [
            CreateComparableOnlyEntry("Title 1", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 2", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 3", "Artist", "FolderA", comment: "old"),
            CreateComparableOnlyEntry("Title 4", "Artist", "FolderA", comment: "old")
        ]);
        IReadOnlyList<PlaylistAggregatePersistenceOwner.ComparablePlaylistEntryRow> reloadedRows = PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows([]);

        PlaylistAggregatePersistenceOwner.PlaylistContentDiffResult result = PlaylistAggregatePersistenceOwner.AnalyzePlaylistContentDiff(persistedRows, reloadedRows);

        Assert.IsTrue(result.HasChanges);
        Assert.AreEqual(4, result.PersistedOnlyCount);
        Assert.AreEqual(0, result.ReloadedOnlyCount);
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
