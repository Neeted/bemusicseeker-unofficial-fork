using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>参照索引のkey存在・多重度・不変header順・同時捕捉・単一配送と実処理量を検証します。画面への反映はworkspace/normal系列へ分担します。</summary>
[TestClass]
public sealed class BmsLibraryPlaylistReferenceServiceTests
{
    /// <summary>同key多重度0→1→2→1→0で無/有/有/有/無となり、表示同値の区間も版位置を配送する。URLだけのentry確定はno-op。</summary>
    [TestMethod]
    public void ReferenceCommits_TrackMultiplicityAndRetainEveryCommitBoundary()
    {
        var publisher = new NormalLibraryRefreshPublisher();
        int wakes = 0;
        var owner = new BmsLibraryPlaylistReferenceOwner(publisher, () => wakes++);
        var table = new BMSTable { name = "B name", symbol = "B" };
        owner.ReplaceTables([Snapshot(table)]);
        int handled = publisher.Version;
        TestablePlaylistEntry first = CreateEntry(new string('a', 32));
        TestablePlaylistEntry second = CreateEntry(first.md5);
        var key = PlaylistEntryLookupKey.FromEntry(first);
        Assert.IsFalse(owner.Capture([key]).Values.ContainsKey(key));
        PlaylistReferenceCommitReceipt one = owner.Apply([Delta(table, first, null, first.CaptureValue())]);
        Assert.AreEqual("B", ChangedDisplay(one, key).Symbols);
        PlaylistReferenceCommitReceipt two = owner.Apply([Delta(table, second, null, second.CaptureValue())]);
        Assert.AreEqual(0, two.Changes.Count);
        Assert.AreEqual("B", owner.Find(first.md5, null).Symbols);
        PlaylistReferenceCommitReceipt three = owner.Apply([Delta(table, first, first.CaptureValue(), null)]);
        Assert.AreEqual(0, three.Changes.Count);
        Assert.AreEqual("B", owner.Find(first.md5, null).Symbols);
        PlaylistReferenceCommitReceipt four = owner.Apply([Delta(table, second, second.CaptureValue(), null)]);
        Assert.IsNull(four.Changes[key]);
        Assert.IsFalse(owner.Capture([key]).Values.ContainsKey(key));
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(handled);
        CollectionAssert.AreEqual(new[] { one, two, three, four }, batch.ReferenceCommits.ToArray());
        CollectionAssert.AreEqual(new[] { one.BaseRevision, one.Revision, two.Revision, three.Revision }, batch.ReferenceCommits.Select(receipt => receipt.BaseRevision).ToArray());
        int version = publisher.Version, wakeCount = wakes;
        PlaylistEntryValue before = second.CaptureValue();
        Assert.IsNull(owner.Apply([Delta(table, second, before, before with { EffectiveUrl = new Uri("https://example.invalid/url") })]));
        Assert.AreEqual(version, publisher.Version);
        Assert.AreEqual(wakeCount, wakes);
    }

    /// <summary>MD5 present-emptyはSHAへ進まず、消失後だけSHAを使う。捕捉済みpresenceと表示は後続更新で変わらない。</summary>
    [TestMethod]
    public void ReferenceCapture_PreservesEmptyBucketPresenceAndShaFallbackInput()
    {
        string md5 = new('a', 32), sha = new('b', 64);
        BMSTable empty = new() { symbol = "", name = "" };
        BMSTable fallback = new() { symbol = "SHA", name = "SHA name" };
        var owner = new BmsLibraryPlaylistReferenceOwner();
        owner.ReplaceTables([Snapshot(empty, CreateEntry(md5)), Snapshot(fallback, CreateEntry(null, sha))]);
        var md5Key = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, md5.ToUpperInvariant());
        var shaKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Sha256, sha);
        PlaylistReferenceReadSnapshot old = owner.Capture([md5Key, shaKey]);
        Assert.AreEqual(2, old.Values.Count);
        Assert.AreEqual(string.Empty, old.Values[md5Key].Symbols);
        Assert.AreEqual("SHA", old.Values[shaKey].Symbols);
        Assert.AreEqual(string.Empty, owner.Find(md5, sha).Symbols);
        PlaylistReferenceCommitReceipt removal = owner.RemoveTables([empty]);
        Assert.IsNull(removal.Changes[md5Key]);
        Assert.IsTrue(removal.Revision > old.Revision);
        Assert.AreEqual("SHA", owner.Find(md5, sha).Symbols);
        Assert.AreEqual(string.Empty, old.Values[md5Key].Symbols);
        Assert.IsTrue(old.Values.ContainsKey(md5Key));
    }

    /// <summary>正本A/B/C・BMT SORT 2/1/2はB/A/C。同順位は正本順、負値・0は数値順、欠損は末尾とし、header更新後も旧捕捉値を保持する。</summary>
    [TestMethod]
    public void ReferenceHeaders_UseBmtSortThenCanonicalTableOrder()
    {
        string md5 = new('c', 32);
        BMSTable a = new() { symbol = "A", name = "A name", bmt_sort = 2 };
        BMSTable b = new() { symbol = "B", name = "B name", bmt_sort = 1 };
        BMSTable c = new() { symbol = "C", name = "C name", bmt_sort = 2 };
        var owner = new BmsLibraryPlaylistReferenceOwner();
        owner.ReplaceTables([Snapshot(a, 0, CreateEntry(md5)), Snapshot(b, 1, CreateEntry(md5)), Snapshot(c, 2, CreateEntry(md5))]);
        var key = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, md5);
        PlaylistReferenceReadSnapshot old = owner.Capture([key]);
        Assert.AreEqual("B A C", old.Values[key].Symbols);
        Assert.AreEqual(string.Join(Environment.NewLine, "B name", "A name", "C name"), old.Values[key].Names);
        PlaylistReferenceCommitReceipt changed = owner.Apply([], [new(a, "AX", "A new", 2, 0)]);
        Assert.AreEqual("B AX C", ChangedDisplay(changed, key).Symbols);
        Assert.AreEqual("B A C", old.Values[key].Symbols);
        Assert.AreEqual("B AX C", owner.Find(md5, null).Symbols);

        PlaylistReferenceCommitReceipt numeric = owner.Apply([], [new(a, "AX", "A new", null, 0),
            new(b, "B", "B name", 0, 1), new(c, "C", "C name", -1, 2)]);
        Assert.AreEqual("C B AX", ChangedDisplay(numeric, key).Symbols);
        Assert.AreEqual(string.Join(Environment.NewLine, "C name", "B name", "A new"), ChangedDisplay(numeric, key).Names);
        Assert.AreEqual("C B AX", owner.Find(md5, null).Symbols);
        Assert.AreEqual("B A C", old.Values[key].Symbols);
    }

    /// <summary>同groupの二表entry差分は参照版を一回進める。catalog resetと旧補助通知clearは独立参照範囲を削除しない。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReferenceQueue_PreservesGroupedCommitAcrossCatalogResetAndLegacyClear(bool resetFirst)
    {
        var publisher = new NormalLibraryRefreshPublisher();
        var owner = new BmsLibraryPlaylistReferenceOwner(publisher);
        BMSTable a = new() { symbol = "A" }, b = new() { symbol = "B" };
        owner.ReplaceTables([Snapshot(a), Snapshot(b)]);
        int handled = publisher.Version;
        TestablePlaylistEntry first = CreateEntry(new string('a', 32));
        TestablePlaylistEntry second = CreateEntry(new string('b', 32));
        void Reset() => publisher.Publish(new NormalLibraryRefreshPublishRequest { ResetsPriorNotifications = true });
        if (resetFirst) { Reset(); }
        long before = owner.Capture([]).Revision;
        PlaylistReferenceCommitReceipt receipt = owner.Apply([Delta(a, first, null, first.CaptureValue()), Delta(b, second, null, second.CaptureValue())]);
        Assert.AreEqual(before + 1, receipt.Revision);
        Assert.AreEqual(2, receipt.Changes.Count);
        publisher.ClearLegacyNotification(publisher.Version);
        if (!resetFirst) { Reset(); }
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(handled);
        Assert.IsTrue(batch.HasRefreshNotification);
        Assert.AreSame(receipt, batch.ReferenceCommits.Single());
        Assert.AreEqual("A", ChangedDisplay(receipt, PlaylistEntryLookupKey.FromEntry(first)).Symbols);
    }

    /// <summary>背景entry/keyを16/128へ増やしても一件entry差分は一bucketだけを更新・表示生成し、全表entryを捕捉しない。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void ReferenceDelta_VisitsOnlyChangedEntryAndBucket(int count)
    {
        var owner = new BmsLibraryPlaylistReferenceOwner();
        BMSTable a = new() { symbol = "A" }, b = new() { symbol = "B" };
        owner.ReplaceTables([Snapshot(a, Enumerable.Range(1, count).Select(value => CreateEntry(value.ToString("x32"))).ToArray()),
            Snapshot(b, Enumerable.Range(1000, count).Select(value => CreateEntry(value.ToString("x32"))).ToArray())]);
        var work = new List<string>();
        owner.WorkObserver = work.Add;
        TestablePlaylistEntry added = CreateEntry(new string('f', 32));
        owner.Apply([Delta(a, added, null, added.CaptureValue())]);
        Assert.AreEqual(1, work.Count(value => value == "reference_entry_delta"));
        Assert.AreEqual(1, work.Count(value => value == "reference_bucket_update"));
        Assert.AreEqual(1, work.Count(value => value == "reference_display_generated"));
        Assert.AreEqual(0, work.Count(value => value == "reference_entry_captured"));
        Assert.AreEqual("A", owner.Capture([PlaylistEntryLookupKey.FromEntry(added)]).Values[PlaylistEntryLookupKey.FromEntry(added)].Symbols);
        Assert.AreEqual(1, work.Count(value => value == "reference_lookup"));
    }

    /// <summary>entry自身にMD5があればSHAは登録しない。MD5-onlyとSHA-onlyの登録規則と削除済み除外を維持する。</summary>
    [TestMethod]
    public void ReferenceIndex_RegistersEntryMd5BeforeShaAndIgnoresRemovedEntries()
    {
        string sha = new('e', 64);
        TestablePlaylistEntry entry = CreateEntry(new string('d', 32), sha);
        TestablePlaylistEntry removed = CreateEntry(new string('f', 32));
        removed.is_removed = true;
        BMSTable table = new() { name = "Table", symbol = "T" };
        var index = PlaylistReferenceIndex.FromSnapshots([Snapshot(table, entry, removed)]);
        Assert.AreEqual("T", index.Find(entry.md5, null).Symbols);
        Assert.AreEqual(string.Empty, index.Find(new string('a', 32), sha).Symbols);
        Assert.AreEqual(string.Empty, index.Find(removed.md5, null).Symbols);
    }

    private static PlaylistReferenceDisplay ChangedDisplay(PlaylistReferenceCommitReceipt receipt, PlaylistEntryLookupKey key)
    {
        Assert.IsTrue(receipt.Changes.TryGetValue(key, out PlaylistReferenceDisplay? display));
        Assert.IsNotNull(display);
        return display;
    }

    private static PlaylistReferenceTableSnapshot Snapshot(BMSTable table, params TestablePlaylistEntry[] entries) => Snapshot(table, 0, entries);
    private static PlaylistReferenceTableSnapshot Snapshot(BMSTable table, int position, params TestablePlaylistEntry[] entries)
        => new(table, table.symbol, table.name, entries, position);
    private static PlaylistEntryCommitReceipt Delta(BMSTable table, BMSTableEntry entry, PlaylistEntryValue? before, PlaylistEntryValue? after)
        => new(table, 0, 1, [new(entry, before, after, before == null ? null : 0, after == null ? null : 0)]);

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
