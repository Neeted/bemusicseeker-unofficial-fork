using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>所属参照のハッシュ判断・索引更新・ライブラリへの接続を検証する。</summary>
/// <remarks>小さい参照入力でMD5優先とSHA補完を網羅し、最後のケースは実BMSファイルと合成bmsonのパスを使って同一フォルダ参照を検索する。画面表示は実一覧テストへ分担する。</remarks>
[TestClass]
public sealed class BmsLibraryPlaylistReferenceServiceTests
{
    /// <summary>2つの所属MD5に対して一致2件と不一致1件を照合し、一致数を返す。全譜面を所属として数える誤りや参照キーの欠落を検出する。</summary>
    [TestMethod]
    public void BuildReferenceLookupKeys_AndApplyReferenceMap_CountsOnlyMatchingCharts()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        List<ChartFile> files =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateFile("cccccccccccccccccccccccccccccccc")
        ];

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots(files), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(2, lookupKeys.Md5Hashes.Count);
        Assert.AreEqual(0, lookupKeys.Sha256Hashes.Count);
        Assert.AreEqual(2, matchedFiles);
        Assert.AreEqual(2, appliedCharts);
    }

    /// <summary>MD5のないエントリはSHAを参照キーにして一致を数える。SHAのみの譜面を所属検索から落とす誤りを検出する。</summary>
    [TestMethod]
    public void ApplyReferenceMap_UsesSha256WhenMd5IsMissing()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable table = CreateTable(CreateEntry(null, new string('a', 64)));
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('a', 64));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots([file]), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(0, lookupKeys.Md5Hashes.Count);
        Assert.AreEqual(1, lookupKeys.Sha256Hashes.Count);
        Assert.AreEqual(1, matchedFiles);
        Assert.AreEqual(1, appliedCharts);
    }

    /// <summary>エントリにMD5がある場合は一致するSHAがあってもMD5不一致を所属にしない。異なる譜面をSHA補完で受理する誤りを検出する。</summary>
    [TestMethod]
    public void ApplyReferenceMap_DoesNotFallbackToSha256WhenEntryMd5Exists()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string sha256 = new string('a', 64);
        BMSTable table = CreateTable(CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sha256));
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sha256);

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots([file]), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, lookupKeys.Md5Hashes.Count);
        Assert.AreEqual(0, lookupKeys.Sha256Hashes.Count);
        Assert.AreEqual(0, matchedFiles);
        Assert.AreEqual(0, appliedCharts);
    }

    /// <summary>同一譜面のMD5とSHAが別テーブルに一致しても譜面を一回だけ数える。複数ハッシュによる一致数の二重加算を検出する。</summary>
    [TestMethod]
    public void ApplyReferenceMap_CountsChartOnceWhenBothHashesMatch()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        BMSTable md5Table = CreateTable(CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        md5Table.name = "MD5";
        BMSTable shaTable = CreateTable(CreateEntry(null, new string('b', 64)));
        shaTable.name = "SHA";
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys([md5Table, shaTable]);
        int appliedCharts = service.ApplyReferenceMap(ToChartSnapshots([file]), lookupKeys, out int matchedFiles, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedFiles);
        Assert.AreEqual(1, appliedCharts);
    }

    /// <summary>ライブラリのbmson参照から所属MD5を照合して一致を返す。BMS専用の入力だけを受け付ける接続漏れを検出する。</summary>
    [TestMethod]
    public void ApplyReferenceMap_MatchesLibraryBmsonReference()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        var chart = LibraryChartRef.FromChartFile((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = md5,
            Sha256 = new string('a', 64)
        }));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromLibraryChartRef(chart)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
    }

    /// <summary>保留パッケージのbmsonを所属検索でき、検索後もBMSアダプターを作らない。検索のためだけにBMS構築へ依存する誤りを検出する。</summary>
    [TestMethod]
    public void ApplyReferenceMap_PackageEntryDoesNotMaterializeAdapterlessBmson()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        var entry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Pending\chart.bmson",
            Md5 = md5,
            Sha256 = new string('a', 64)
        }));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromPackageChartEntry(entry)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
        Assert.IsNull(entry.GetBmsChartForTest());
    }

    /// <summary>パッケージのBMS入力から所属MD5を照合し一致を返す。保留項目から共通検索入力への変換漏れを検出する。</summary>
    [TestMethod]
    public void ApplyReferenceMap_MatchesPackageBmsReference()
    {
        var service = new BmsLibraryPlaylistReferenceService(2);
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable table = CreateTable(CreateEntry(md5));
        ChartFile file = CreateFile(md5);
        var entry = PackageChartEntry.FromChart((file));

        PlaylistReferenceLookupKeys lookupKeys = BuildReferenceLookupKeys(table);
        int appliedCharts = service.ApplyReferenceMap([PlaylistReferenceChartSnapshot.FromPackageChartEntry(entry)], lookupKeys, out int matchedCharts, out PlaylistReferenceApplyStats _);

        Assert.AreEqual(1, matchedCharts);
        Assert.AreEqual(1, appliedCharts);
    }

    /// <summary>SHAだけのエントリを索引化して対応するテーブル記号・名前を返す。MD5以外の参照欠落や表示テーブルの取り違えを検出する。</summary>
    [TestMethod]
    public void PlaylistReferenceIndex_FindsSha256OnlyBmsonReference()
    {
        BMSTable table = CreateTable(CreateEntry(null, new string('c', 64)));
        table.symbol = "BMSN";
        table.name = "Bmson Table";

        var index = PlaylistReferenceIndex.FromSnapshots(
            [new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        PlaylistReferenceDisplay display = index.Find(null, new string('c', 64));

        Assert.AreEqual("BMSN", display.Symbols);
        Assert.AreEqual("Bmson Table", display.Names);
    }

    /// <summary>MD5とSHAを持つbmsonをChartFileとLibraryChartRefの両入口で検索する。MD5優先の索引化と譜面表現ごとの接続漏れを検出する。</summary>
    [TestMethod]
    public void PlaylistReferenceIndex_FindsReferencesFromChartIdentity()
    {
        string md5 = "dddddddddddddddddddddddddddddddd";
        string sha256 = new string('d', 64);
        BMSTable table = CreateTable(CreateEntry(md5, sha256));
        table.symbol = "ID";
        table.name = "Identity";
        var index = PlaylistReferenceIndex.FromSnapshots(
            [new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        ChartFile chart = (ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = md5,
            Sha256 = sha256
        });
        var chartRef = LibraryChartRef.FromChartFile(chart);

        Assert.AreEqual(1, index.Md5Count);
        Assert.AreEqual(0, index.Sha256Count);
        Assert.AreEqual("ID", index.Find(chart).Symbols);
        Assert.AreEqual("Identity", index.Find(chartRef).Names);
    }

    /// <summary>同一テーブルのエントリと表示名を差替えると旧ハッシュを消し、新ハッシュに新記号を返す。更新後も古い所属を残す誤りを検出する。</summary>
    [TestMethod]
    public void PlaylistReferenceIndex_ReplacesTableEntries()
    {
        BMSTable table = CreateTable(CreateEntry(null, new string('d', 64)));
        table.symbol = "OLD";
        table.name = "Old Table";
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));

        Assert.AreEqual("OLD", index.Find(null, new string('d', 64)).Symbols);

        table.entries =
        [
            CreateEntry(null, new string('e', 64))
        ];
        table.symbol = "NEW";
        table.name = "New Table";
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));

        Assert.AreEqual(string.Empty, index.Find(null, new string('d', 64)).Symbols);
        Assert.AreEqual("NEW", index.Find(null, new string('e', 64)).Symbols);
    }

    /// <summary>追加譜面を持つカタログ確定結果から所属一致と受理世代を返す。カタログ更新を所属へ反映しない接続漏れを検出する。</summary>
    [TestMethod]
    public void PlaylistReferenceOwner_AppliesCatalogReceiptToChartSnapshot()
    {
        string md5 = "11111111111111111111111111111111";
        BMSTable table = CreateTable(CreateEntry(md5));
        var owner = new BmsLibraryPlaylistReferenceOwner(2);
        var tableSnapshot = new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries);
        owner.AddReferenceBMSTable(
            tableSnapshot,
            owner.BuildReferenceLookupKeys(tableSnapshot),
            [],
            [],
            null);

        var receipt = new CatalogMutationReceipt(
            applied: true,
            new OwnedChartCollectionVersionSnapshot(0),
            folderDbMs: 0,
            bmsPathDbMs: 0,
            bmsonPathDbMs: 0,
            bmsRemovalDbMs: 0,
            bmsonRemovalDbMs: 0,
            liveApplyMs: 0,
            ownedCollectionApplied: true,
            catalogRevision: 17,
            addedCharts:
            [
                new CatalogChartMutationFact(
                    ChartFileKind.Bms,
                    @"C:\\Library\\chart.bms",
                    md5,
                    null)
            ],
            pathFacts: [],
            removalRequests: []);

        PlaylistReferenceCatalogApplyResult result = owner.ApplyCatalogMutationReceipt(
            receipt,
            [new PlaylistReferenceChartSnapshot(md5, null)]);

        Assert.AreEqual(17, result.CatalogVersion);
        Assert.AreEqual(1, result.AffectedChartCount);
        Assert.AreEqual(1, result.MatchedChartCount);
    }

    /// <summary>所有者経由のテーブル置換で旧所属を除き新所属を公開する。索引更新を所有者の公開結果へ渡し忘れる誤りを検出する。</summary>
    [TestMethod]
    public void PlaylistReferenceOwner_ReplaceReferenceBMSTablePublishesNewSnapshot()
    {
        string oldMd5 = "22222222222222222222222222222222";
        string newMd5 = "33333333333333333333333333333333";
        BMSTable oldTable = CreateTable(CreateEntry(oldMd5));
        BMSTable newTable = CreateTable(CreateEntry(newMd5));
        oldTable.symbol = "OLD";
        newTable.symbol = "NEW";
        var owner = new BmsLibraryPlaylistReferenceOwner(2);
        var oldSnapshot = new PlaylistReferenceTableSnapshot(oldTable, oldTable.symbol, oldTable.name, oldTable.entries);
        var newSnapshot = new PlaylistReferenceTableSnapshot(newTable, newTable.symbol, newTable.name, newTable.entries);

        owner.AddReferenceBMSTable(oldSnapshot, owner.BuildReferenceLookupKeys(oldSnapshot), [], [], null);
        owner.ReplaceReferenceBMSTable(oldSnapshot, newSnapshot, [], [], null);

        Assert.AreEqual(string.Empty, owner.Find(oldMd5, null).Symbols);
        Assert.AreEqual("NEW", owner.Find(newMd5, null).Symbols);
    }

    /// <summary>同一テーブル内の重複MD5は記号と名前を一回だけ表示する。譜面行ごとの重複所属表示を検出する。</summary>
    [TestMethod]
    public void PlaylistReferenceIndex_FromSnapshotsDeduplicatesDuplicateEntriesForSameTable()
    {
        string md5 = "ffffffffffffffffffffffffffffffff";
        BMSTable table = CreateTable(
            CreateEntry(md5),
            CreateEntry(md5));
        table.symbol = "DUP";
        table.name = "Duplicate";

        var index = PlaylistReferenceIndex.FromSnapshots(
            [new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)]);
        PlaylistReferenceDisplay display = index.Find(md5, null);

        Assert.AreEqual("DUP", display.Symbols);
        Assert.AreEqual("Duplicate", display.Names);
    }

    /// <summary>実BMSファイルの参照と同一フォルダの合成bmson参照からMD5を集め、別フォルダのBMSとMD5なしbmsonを除く。形式による参照の欠落や他フォルダの混入を検出する。</summary>
    [TestMethod]
    public void GetPlaylistOrgMd5sForChart_ReturnsBmsAndBmsonMd5sInSameDirectory()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        string libraryDirectoryPath = Path.Combine(tempRootPath, "Library");
        string otherDirectoryPath = Path.Combine(tempRootPath, "Other");
        Directory.CreateDirectory(libraryDirectoryPath);
        Directory.CreateDirectory(otherDirectoryPath);
        try
        {
            string songDbPath = Path.Combine(tempRootPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            string sourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string includedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string bmsonHash = "cccccccccccccccccccccccccccccccc";
            string otherHash = "dddddddddddddddddddddddddddddddd";
            ChartFile source = CreateLibraryFile(sourceHash, Path.Combine(libraryDirectoryPath, "source.bms"));
            ChartFile included = CreateLibraryFile(includedHash, Path.Combine(libraryDirectoryPath, "included.bms"));
            ChartFile otherDirectory = CreateLibraryFile(otherHash, Path.Combine(otherDirectoryPath, "other.bms"));
            ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(libraryDirectoryPath, "chart.bmson"),
                Md5 = bmsonHash,
                Sha256 = new string('c', 64)
            };
            ChartFile md5lessBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(libraryDirectoryPath, "md5less.bmson"),
                Md5 = null,
                Sha256 = new string('e', 64)
            };
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [source, included, otherDirectory],
                BmsonCharts = [bmson, md5lessBmson]
            };

            List<string> orgMd5s = library.GetPlaylistOrgMd5sForChart((source));

            CollectionAssert.AreEqual(new[] { sourceHash, includedHash, bmsonHash }, orgMd5s);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static BMSTable CreateTable(params TestablePlaylistEntry[] entries)
    {
        var table = new BMSTable
        {
            name = "Playlist",
            entries = [.. entries.Cast<BMSTableEntry>()]
        };
        return table;
    }

    private static PlaylistReferenceLookupKeys BuildReferenceLookupKeys(BMSTable table)
    {
        return new BmsLibraryPlaylistReferenceOwner(2).BuildReferenceLookupKeys(
            new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));
    }

    private static PlaylistReferenceLookupKeys BuildReferenceLookupKeys(IEnumerable<BMSTable> tables)
    {
        return new BmsLibraryPlaylistReferenceOwner(2).BuildReferenceLookupKeys(
            (tables ?? []).Where(table => table != null)
                .Select(table => new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries)));
    }

    private static TestablePlaylistEntry CreateEntry(string? md5, string? sha256 = null)
    {
        var entry = new TestablePlaylistEntry();
        if (md5 != null)
        {
            entry.SetMd5(md5);
        }
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        entry.folder = "Folder";
        return entry;
    }

    private static ChartFile CreateFile(string hash, string? sha256 = null)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = "C:\\Dummy\\" + Guid.NewGuid().ToString("N") + ".bms"
        };
        file = file with { Md5 = hash };
        if (sha256 != null)
        {
            file = file with { Sha256 = sha256 };
        }
        return file;
    }

    private static ChartFile CreateLibraryFile(string hash, string path, params string[] wavFiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path,
            Resources = TestChartResources.Create(wavFiles)
        };
        file = file with { Md5 = hash };
        return file;
    }

    private static List<PlaylistReferenceChartSnapshot> ToChartSnapshots(IEnumerable<ChartFile> files)
    {
        return [.. files.Select(chart => LibraryChartRef.FromChartFile((chart)))
            .Select(PlaylistReferenceChartSnapshot.FromLibraryChartRef)
            .Where(chart => chart != null)];
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }

}
