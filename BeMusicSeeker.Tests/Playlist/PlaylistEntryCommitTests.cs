using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Tests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>entry owner の draft・実 SQLite・不変差分と局所仕事量を検証します。表示と必要出力の終端は既存 workspace 系列が分担します。</summary>
[TestClass]
public sealed class PlaylistEntryCommitTests
{
    private const string LowerMd5 = "abcdef0123456789abcdef0123456789";
    private const string MixedMd5 = "ABCDef0123456789ABCDef0123456789";
    private const string LowerSha256 = LowerMd5 + LowerMd5;
    private const string MixedSha256 = MixedMd5 + MixedMd5;

    /// <summary>Level の既存抽出・非数値維持・空 null を DB と不変捕捉で確認し、入力文字列の正本化と非編集値の巻戻しを検出します。</summary>
    [DataTestMethod]
    [DataRow("★12.5", 12.5)]
    [DataRow("A", 2d)]
    [DataRow("", null)]
    public void CommitDraft_LevelUsesSavedNumberAndKeepsOtherValues(string text, double? expected)
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry entry = table.entries[0];
        PlaylistEntryReadSnapshot old = table.CaptureEntryReadSnapshot(entry);
        int lookupVersion = table.EntryLookupRevision;
        PlaylistEntryCommitReceipt receipt = fixture.Commit(entry, "Level", text);
        Assert.AreEqual(expected, entry.level);
        Assert.AreEqual("memo before", entry.memo);
        Assert.AreEqual("comment before", entry.comment);
        Assert.AreEqual(new Uri("https://example.test/original"), entry.Url);
        Assert.AreEqual(2d, old.Entries.Single().After.Level);
        Assert.AreEqual(lookupVersion, table.EntryLookupRevision);
        Assert.AreEqual(expected == 2 ? old.Revision : old.Revision + 1, receipt.Revision);
        Assert.AreEqual(table.PlaylistEntriesRevision, table.CaptureEntryReadSnapshot(entry).Revision);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(expected, db.ExecuteScalar<double?>("SELECT level FROM playlist_entry WHERE playlist_id = 9201 AND title = 'row-0';"));
    }

    /// <summary>DB 前の実 trigger 失敗で live/header/版/配送を保持し、同じ entry への次保存と別参照の拒否を確認します。</summary>
    [TestMethod]
    public void CommitDraft_DbFailureKeepsLiveAndRejectsAnotherEntryReference()
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry entry = table.entries[0];
        DateTime oldDate = table.last_update;
        int oldVersion = table.PlaylistEntriesRevision;
        fixture.Execute("CREATE TRIGGER fail_entry BEFORE INSERT ON playlist_entry BEGIN SELECT RAISE(ABORT, 'entry failure'); END;");
        Assert.ThrowsException<SQLite.SQLiteException>(() => fixture.Commit(entry, "memo", "draft"));
        Assert.AreEqual("memo before", entry.memo);
        Assert.AreEqual(oldDate, table.last_update);
        Assert.AreEqual(oldVersion, table.PlaylistEntriesRevision);
        Assert.AreEqual(0, fixture.Receipts.Count);
        fixture.Execute("DROP TRIGGER fail_entry;");
        BMSTableEntry stale = entry.CreatePlaylistReloadSnapshot();
        stale.parent = table;
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Commit(stale, "memo", "wrong"));
        PlaylistEntryCommitReceipt receipt = fixture.Commit(entry, "memo", "saved");
        Assert.AreSame(entry, receipt.Changes.Single().Entry);
        Assert.AreEqual("memo before", receipt.Changes.Single().Before.Memo);
        Assert.AreEqual("saved", receipt.Changes.Single().After.Memo);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual("saved", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id = 9201 AND title = 'row-0';"));
    }

    /// <summary>表 reader を別 worker が保持し、実 writer 待機を観測して未確定 draft が live/header/版へ現れないことを確認します。</summary>
    [TestMethod]
    public async Task CommitDraft_WaitingWriterDoesNotExposeDraft()
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry entry = table.entries[0];
        DateTime oldDate = table.last_update;
        int oldVersion = table.PlaylistEntriesRevision;
        var readerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                readerHeld.TrySetResult();
                SpinWait.SpinUntil(() => table.ReaderWriterLock.WaitingWriteCount > 0 || writerFinished.Task.IsCompleted);
                Assert.IsTrue(table.ReaderWriterLock.WaitingWriteCount > 0);
                Assert.AreEqual("memo before", entry.memo);
                Assert.AreEqual(oldDate, table.last_update);
                Assert.AreEqual(oldVersion, table.PlaylistEntriesRevision);
                Assert.AreEqual("memo before", table.CaptureEntryReadSnapshot(entry).Entries.Single().After.Memo);
            }
        });
        Task? writer = null;
        try
        {
            await readerHeld.Task;
            writer = Task.Run(() =>
            {
                try { fixture.Commit(entry, "memo", "committed"); }
                finally { writerFinished.TrySetResult(); }
            });
            await Task.WhenAll(reader, writer);
            Assert.AreEqual("committed", entry.memo);
        }
        finally
        {
            writerFinished.TrySetResult();
            await reader;
            if (writer != null) { await writer; }
        }
    }

    /// <summary>二表目の DB 失敗で一括 transaction 全体と同じ live 所属列・参照・日時・版を維持します。</summary>
    [TestMethod]
    public void CommitRemovalBatch_SecondTableFailureKeepsBothTables()
    {
        using var fixture = new Fixture();
        IReadOnlyList<BMSTableEntry>[] oldEntries = [.. fixture.Tables.Select(table => table.entries)];
        DateTime[] dates = [.. fixture.Tables.Select(table => table.last_update)];
        int[] versions = [.. fixture.Tables.Select(table => table.PlaylistEntriesRevision)];
        long generation = fixture.Owner.PersistenceGeneration;
        fixture.Execute("CREATE TRIGGER fail_second BEFORE INSERT ON playlist WHEN NEW.playlist_id = 9202 BEGIN SELECT RAISE(ABORT, 'second failure'); END;");
        Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>> requests = fixture.Tables.ToDictionary(table => table,
            table => new Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>(observer => table.PrepareEntryMutation([table.entries[0]], [], observer)));
        Assert.ThrowsException<SQLite.SQLiteException>(() => fixture.Owner.CommitEntryMutations(requests, fixture.Receipts.Add));
        for (int index = 0; index < fixture.Tables.Length; index++)
        {
            BMSTable table = fixture.Tables[index];
            Assert.AreSame(oldEntries[index], table.entries);
            Assert.AreEqual(dates[index], table.last_update);
            Assert.AreEqual(versions[index], table.PlaylistEntriesRevision);
            Assert.AreSame(table, table.entries[0].parent);
        }
        Assert.AreEqual(generation, fixture.Owner.PersistenceGeneration);
        Assert.AreEqual(0, fixture.Receipts.Count);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(4, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry;"));
    }

    /// <summary>同じ一件削除・追加で背景を増やし、実捕捉・参照取得・所属列変更と SQL 対象が背景件数へ比例しないことを測ります。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void CommitLocalChanges_WorkIsLimitedToAffectedEntries(int background)
    {
        using var fixture = new Fixture(background);
        BMSTable table = fixture.Tables[0];
        IReadOnlyList<BMSTableEntry> oldMembership = table.entries;
        BMSTableEntry removed = table.entries[0];
        BMSTableEntry retained = table.entries[1];
        fixture.Execute("CREATE TABLE entry_work (kind TEXT);",
            "CREATE TRIGGER count_delete AFTER DELETE ON playlist_entry BEGIN INSERT INTO entry_work VALUES ('delete'); END;",
            "CREATE TRIGGER count_insert AFTER INSERT ON playlist_entry BEGIN INSERT INTO entry_work VALUES ('insert'); END;");
        var added = new BMSTableEntry { md5 = new string('e', 32), title = "added" };
        IReadOnlyList<PlaylistEntryCommitReceipt> receipts = fixture.Owner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareEntryMutation([removed], [(added, "folder")], observer) }, fixture.Receipts.Add);
        Assert.AreEqual(1, fixture.Work.Candidates);
        Assert.AreEqual(2, fixture.Work.MembershipChanges);
        Assert.IsTrue(fixture.Work.ReferenceReads <= 16, "背景全件の参照取得をしません。");
        Assert.AreEqual(2, receipts.Single().Changes.Length);
        Assert.AreEqual(background, oldMembership.Count);
        Assert.AreSame(removed, oldMembership[0]);
        Assert.AreSame(retained, table.entries[0]);
        Assert.AreSame(added, table.entries[^1]);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM entry_work WHERE kind = 'delete';"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM entry_work WHERE kind = 'insert';"));
        Assert.AreEqual(background, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201;"));
    }

    /// <summary>NULL folder・NULL hash・重複候補・削除済みを実 SQL から読み、正規化同値候補だけを編集・削除します。</summary>
    [TestMethod]
    public void CommitLocalChanges_NullAndDuplicateKeysKeepUnrelatedRemovedRows()
    {
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, NULL, 'null-key', NULL, 0, 'before');",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, NULL, 'null-key', '', 0, 'duplicate');",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, NULL, 'null-key', NULL, 1, 'removed');");
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: false);
        BMSTableEntry entry = table.entries.First(row => !row.is_removed);
        fixture.Commit(entry, "memo", "after");
        using (LR2SongDBExtended db = fixture.OpenRead())
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0;"));
            Assert.AreEqual("after", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0;"));
            Assert.AreEqual("removed", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 1;"));
        }
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareEntryMutation([entry], [], observer) }, fixture.Receipts.Add);
        using LR2SongDBExtended after = fixture.OpenRead();
        Assert.AreEqual(0, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0;"));
        Assert.AreEqual(1, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 1;"));
    }

    /// <summary>所属済みの同じ参照を別フォルダへ追加しても二重所属を作らず、元の明示順序と対象外参照を保持します。</summary>
    [TestMethod]
    public void CommitLocalChanges_ExistingEntryMoveKeepsReferenceAndOrder()
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry moved = table.entries[0];
        BMSTableEntry retained = table.entries[1];
        PlaylistEntryReadSnapshot before = table.CaptureEntryReadSnapshot(moved);
        PlaylistEntryCommitReceipt receipt = fixture.Owner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareEntryMutation([], [(moved, "destination")], observer) }, fixture.Receipts.Add).Single();
        Assert.AreEqual(2, table.entries.Count);
        Assert.AreSame(moved, table.entries[0]);
        Assert.AreSame(retained, table.entries[1]);
        Assert.AreEqual("destination", moved.folder);
        Assert.AreEqual("folder", retained.folder);
        Assert.AreEqual(before.Entries.Single().AfterOrder, receipt.Changes.Single().AfterOrder);
        Assert.AreEqual("folder", before.Entries.Single().After.Folder);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND folder = 'destination';"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND folder = 'folder';"));
    }

    /// <summary>少数 folder の名称変更・ルート統合は対象候補だけを読み書きし、背景 entry と順序を保持します。</summary>
    [DataTestMethod]
    [DataRow(16, "renamed")]
    [DataRow(128, "renamed")]
    [DataRow(128, "")]
    public void CommitFolderRename_VisitsOnlyAffectedFolder(int background, string destination)
    {
        using var fixture = new Fixture(background);
        BMSTable table = fixture.Tables[0];
        BMSTableEntry changed = table.entries[0];
        BMSTableEntry retained = table.entries[1];
        changed.folder = "source";
        table.entries = table.entries;
        table.Folder_order = ["source", "folder"];
        fixture.Owner.ReplaceTablesWithEntries([table]);
        fixture.Execute("CREATE TABLE entry_work (kind TEXT);",
            "CREATE TRIGGER count_delete AFTER DELETE ON playlist_entry BEGIN INSERT INTO entry_work VALUES ('delete'); END;",
            "CREATE TRIGGER count_insert AFTER INSERT ON playlist_entry BEGIN INSERT INTO entry_work VALUES ('insert'); END;");
        PlaylistEntryCommitReceipt receipt = fixture.Owner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareFolderRename("source", destination, observer) }, fixture.Receipts.Add).Single();
        Assert.AreEqual(1, fixture.Work.Candidates);
        Assert.AreEqual(0, fixture.Work.MembershipChanges);
        Assert.IsTrue(fixture.Work.ReferenceReads <= 4);
        Assert.AreEqual(1, receipt.Changes.Length);
        Assert.AreSame(changed, table.entries[0]);
        Assert.AreSame(retained, table.entries[1]);
        Assert.AreEqual(destination, changed.folder);
        Assert.AreEqual("folder", retained.folder);
        CollectionAssert.AreEqual(new[] { destination, "folder" }, table.Folder_order);
        CollectionAssert.AreEqual(new[] { destination, "folder" }, receipt.FolderOrder.ToArray());
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM entry_work WHERE kind = 'delete';"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM entry_work WHERE kind = 'insert';"));
        Assert.AreEqual(background, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201;"));
        CollectionAssert.AreEqual(new[] { destination, "folder" }, db.Table<BMSTable>().Single(row => row.playlist_id == 9201).Folder_order);
    }

    /// <summary>memo 確定と URL 補完は所持数の依存を変更せず、既に温めた表の全 entry 再走査を要求しません。</summary>
    [TestMethod]
    public void CommitMemoAndRuntimeUrl_KeepSummaryCountCache()
    {
        using var fixture = new Fixture(128);
        BMSTable table = fixture.Tables[0];
        var hashes = new OwnedChartHashIndexSnapshot();
        hashes.AddMd5(table.entries[0].md5);
        var owned = new OwnedChartHashIndexVersionedSnapshot(hashes, 1, 0, 1, 1);
        var summary = new PlaylistCatalogSummaryOwner();
        PlaylistSummaryCountResult first = summary.GetOrBuildTableCount(table, owned, CancellationToken.None, out bool initialHit);
        Assert.IsFalse(initialHit);
        Assert.AreEqual(128, first.TotalCharts);
        Assert.AreEqual(1, first.OwnedCharts);
        fixture.Commit(table.entries[0], "memo", "memo after");
        summary.GetOrBuildTableCount(table, owned, CancellationToken.None, out bool memoHit);
        Assert.IsTrue(memoHit);
        using (table.ReaderWriterLock.GetWriterGuard())
        {
            BMSTableEntry entry = table.entries[0];
            PlaylistEntryValue before = entry.CaptureValue();
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.test/completed"), null, overwriteExisting: true);
            table.CommitEntryValues([table.CaptureEntryChange(entry, before)]);
        }
        PlaylistSummaryCountResult last = summary.GetOrBuildTableCount(table, owned, CancellationToken.None, out bool urlHit);
        Assert.IsTrue(urlHit);
        Assert.AreEqual(128, last.TotalCharts);
        Assert.AreEqual(1, last.OwnedCharts);
    }

    /// <summary>同表・同folder・NULL titleの背景だけを増やし、正規memo保存の候補SELECTの実VM_STEPが全件走査へ増えないことを確認します。</summary>
    /// <remarks>SEARCHによる広い索引範囲走査はFULLSCAN_STEPや変更行triggerだけでは検出できないため、PROFILEの実VM_STEPを比較します。</remarks>
    [DataTestMethod]
    [DataRow("md5")]
    [DataRow("sha256")]
    [DataRow("none")]
    public void CommitDraft_CandidateLookupWorkDoesNotGrowWithBackground(string hashKind)
    {
        CandidateLookupWork small = MeasureCandidateLookup(16, hashKind);
        CandidateLookupWork large = MeasureCandidateLookup(128, hashKind);
        Assert.AreEqual(2, small.StatementCount);
        Assert.AreEqual(2, large.StatementCount);
        Assert.AreEqual(1, small.ReturnedRows);
        Assert.AreEqual(1, large.ReturnedRows);
        Assert.IsTrue(small.VmSteps > 0 && large.VmSteps > 0, "実SQLiteのPROFILE計測が必要です。");
        Assert.AreEqual(small.VmSteps, large.VmSteps,
            "影響候補は同じ一件です。背景の増加で候補SQLが広い索引範囲を走査しています。"
            + " 16=" + small.VmSteps + ", 128=" + large.VmSteps
            + Environment.NewLine + string.Join(Environment.NewLine, large.Plans));
        Assert.IsTrue(large.Plans.All(plan => !plan.Contains("SCAN playlist_entry", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, large.Plans));
    }

    private static CandidateLookupWork MeasureCandidateLookup(int background, string hashKind)
    {
        using var observation = new SqliteStatementObservation();
        using var fixture = new Fixture(background, observation.OpenSongDb);
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        for (int index = 0; index < background; index++)
        {
            string? md5 = hashKind == "sha256" || (hashKind == "none" && index == 0)
                ? null : index == 0 ? LowerMd5 : index.ToString("x32");
            string? sha256 = hashKind == "sha256" ? index == 0 ? LowerSha256 : index.ToString("x64") : null;
            fixture.InsertRawEntry(md5, sha256);
        }
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single(entry => hashKind switch
        {
            "md5" => entry.md5 == LowerMd5,
            "sha256" => entry.sha256 == LowerSha256,
            _ => entry.md5 == null && entry.sha256 == null
        });
        BMSTableEntry retained = table.entries.First(entry => !ReferenceEquals(entry, target));
        fixture.Commit(target, "memo", "after");
        SqliteStatementObservation.SqliteObservedStatement[] statements = [.. observation.Statements.Where(statement =>
            statement.Sql.StartsWith("SELECT rowid AS RowId FROM playlist_entry WHERE ", StringComparison.Ordinal))];
        observation.ThrowIfCallbackFailed();
        Assert.AreEqual(background, table.entries.Count);
        Assert.AreEqual("before", retained.memo);
        Assert.AreEqual("after", target.memo);
        using (LR2SongDBExtended db = fixture.OpenRead())
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'after';"));
            Assert.AreEqual(background - 1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'before';"));
        }
        return new(statements.Length, statements.Sum(statement => statement.RowCount), statements.Sum(statement => statement.VmSteps),
            [.. statements.SelectMany(statement => fixture.Explain(statement.Sql))]);
    }

    /// <summary>実DBのNULL/空/Unicode blank/大小文字と従来受理する最終LFを、setterと同じ意味の保存キーで扱います。</summary>
    /// <remarks>非blankをTrimしてLF付きhashを通常hashへ統合する誤りと、blankの旧行が残る誤りを検出します。</remarks>
    [DataTestMethod]
    [DataRow(null, null, null, null)]
    [DataRow("", "", null, null)]
    [DataRow("unicode-blank", "unicode-blank", null, null)]
    [DataRow(MixedMd5, MixedSha256, LowerMd5, LowerSha256)]
    [DataRow(MixedMd5 + "\n", null, LowerMd5 + "\n", null)]
    [DataRow(null, MixedSha256 + "\n", null, LowerSha256 + "\n")]
    public void CommitDraft_HashLookupPreservesExistingNormalization(string? rawMd5, string? rawSha256,
        string? expectedMd5, string? expectedSha256)
    {
        if (rawMd5 == "unicode-blank")
        {
            // 制御文字は判定記録のケース名へ混ぜず、実入力には25種すべてを渡します。
            rawMd5 = " \t\r\n\v\f\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
            rawSha256 = "\t\u3000";
        }
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        fixture.InsertRawEntry(rawMd5, rawSha256);
        fixture.InsertRawEntry(rawMd5, rawSha256, memo: "duplicate");
        fixture.InsertRawEntry(rawMd5, rawSha256, removed: true, memo: "removed");
        // 非blank最終LFは通常hashと別キーとして保存し、対象のmemo変更で削除・書換えしません。
        if (expectedMd5?.EndsWith('\n') == true || expectedSha256?.EndsWith('\n') == true)
        {
            fixture.InsertRawEntry(expectedMd5?.TrimEnd('\n'), expectedSha256?.TrimEnd('\n'), memo: "without-lf");
        }
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single(entry => entry.md5 == expectedMd5 && entry.sha256 == expectedSha256);
        Assert.AreEqual(expectedMd5, target.md5);
        Assert.AreEqual(expectedSha256, target.sha256);
        fixture.Commit(target, "memo", "after");
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'after';"));
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo IN ('before', 'duplicate');"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'removed' AND is_removed = 1;"));
        Assert.AreEqual(expectedMd5, db.ExecuteScalar<string>("SELECT md5 FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'after';"));
        Assert.AreEqual(expectedSha256, db.ExecuteScalar<string>("SELECT sha256 FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'after';"));
        if (expectedMd5?.EndsWith('\n') == true || expectedSha256?.EndsWith('\n') == true)
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'without-lf';"));
        }
    }

    /// <summary>索引追加は生の非blank値を変えず、従来拒否される前後空白・不正hashの読込み判定も変えません。</summary>
    [DataTestMethod]
    [DataRow(" " + LowerMd5)]
    [DataRow(LowerMd5 + " ")]
    [DataRow("not-a-hash")]
    public void EnsureSchema_DoesNotRewriteOrNewlyAcceptMalformedHash(string rawMd5)
    {
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        fixture.Execute("DROP INDEX playlist_entry_idx_candidate;");
        fixture.InsertRawEntry(rawMd5, null);
        fixture.EnsureSchema();
        using (LR2SongDBExtended db = fixture.OpenRead())
        {
            Assert.AreEqual(rawMd5, db.ExecuteScalar<string>("SELECT md5 FROM playlist_entry WHERE playlist_id = 9201;"));
        }
        Assert.ThrowsException<FormatException>(() => fixture.Repository.LoadStartupPlaylistEntries());
    }

    /// <summary>通常readerのNULL title→空とraw startupのNULL保持の両方から、同値候補だけを保存・削除します。</summary>
    /// <remarks>NULL/空titleは同じentry identityです。最後の実entry削除はfolder用dummyを残し、削除済み・別表は保持します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CommitDraft_NullTitleReaderRoutesConvergeForSaveAndRemoval(bool rawStartup)
    {
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        fixture.InsertRawEntry(LowerMd5, null);
        fixture.InsertRawEntry(LowerMd5, null, memo: "duplicate", title: string.Empty);
        fixture.InsertRawEntry(LowerMd5, null, removed: true, memo: "removed");
        BMSTable table = fixture.Tables[0];
        table.entries = rawStartup
            // startup の NULL 保持を確実に通すため、正規化時に先勝ちする NULL title を先頭にします。
            ? [.. fixture.Repository.LoadStartupPlaylistEntries().Entries
                .Where(entry => entry.playlist_id == table.playlist_id && !entry.is_removed)
                .OrderBy(entry => entry.title != null)]
            : fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single();
        Assert.AreEqual(rawStartup ? null : string.Empty, target.title);
        fixture.Commit(target, "memo", "after");
        using (LR2SongDBExtended db = fixture.OpenRead())
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0;"));
            Assert.AreEqual("after", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0;"));
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'removed' AND is_removed = 1;"));
        }
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareEntryMutation([target], [], observer) }, fixture.Receipts.Add);
        using LR2SongDBExtended after = fixture.OpenRead();
        Assert.AreEqual(0, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0 AND md5 = ?;", LowerMd5));
        Assert.AreEqual(1, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0;"));
        Assert.AreEqual(1, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed = 0 AND md5 = '00000000000000000000000000000000' AND folder = 'folder';"));
        Assert.AreEqual("00000000000000000000000000000000", table.entries.Single().md5);
        Assert.AreEqual("folder", table.entries.Single().folder);
        Assert.IsFalse(table.entries.Contains(target));
        Assert.AreEqual(1, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'removed' AND is_removed = 1;"));
        Assert.AreEqual(2, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9202;"));
    }

    private sealed record CandidateLookupWork(int StatementCount, int ReturnedRows, int VmSteps, IReadOnlyList<string> Plans);

    private sealed class QueryPlanRow
    {
        public string detail { get; set; } = string.Empty;
    }

    private sealed class WorkObserver : IPlaylistEntryMutationWorkObserver
    {
        internal int ReferenceReads;
        internal int Candidates;
        internal int MembershipChanges;
        public void OnEntryReferenceRead() => ReferenceReads++;
        public void OnCandidateCaptured() => Candidates++;
        public void OnMembershipChanged() => MembershipChanges++;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistEntryCommitTests), Guid.NewGuid().ToString("N"));
        private readonly string dbPath;
        private readonly ReaderWriterLockSlimWrapper collectionLock = new();
        internal WorkObserver Work { get; } = new();
        internal PlaylistAggregatePersistenceOwner Owner { get; }
        internal PlaylistPersistenceRepository Repository { get; }
        internal BMSTable[] Tables { get; }
        internal List<PlaylistEntryCommitReceipt> Receipts { get; } = [];

        internal Fixture(int background = 2, Func<string, LR2SongDBExtended>? openEntryWriteConnection = null)
        {
            Directory.CreateDirectory(directory);
            dbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(dbPath);
            Repository = new(dbPath, openEntryWriteConnection);
            Owner = new(Repository, collectionLock, new TestUiScheduler(() => null), Work);
            Tables = [CreateTable(9201, background), CreateTable(9202, 2)];
            Owner.MarkActiveTables(Tables);
            Owner.ReplaceTablesWithEntries(Tables);
        }

        private static BMSTable CreateTable(int id, int count) => new()
        {
            playlist_id = id,
            name = "table-" + id,
            last_update = new DateTime(2026, 1, 1),
            entries = [.. Enumerable.Range(0, count).Select(index => new BMSTableEntry
            {
                md5 = (index + 1).ToString("x32"), title = "row-" + index, folder = "folder", level = 2,
                memo = "memo before", comment = "comment before", Url = new Uri("https://example.test/original")
            })]
        };

        internal PlaylistEntryCommitReceipt Commit(BMSTableEntry entry, string property, string text)
            => Owner.CommitEntry(entry, property, text, null, Receipts.Add, () => collectionLock.GetReaderGuard());
        internal LR2SongDBExtended OpenRead() => new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
        internal void EnsureSchema() => PlaylistPersistenceRepository.EnsureSchema(dbPath);
        internal IReadOnlyList<string> Explain(string sql)
        {
            using LR2SongDBExtended db = OpenRead();
            return [.. db.Query<QueryPlanRow>("EXPLAIN QUERY PLAN " + sql).Select(row => row.detail)];
        }
        internal void InsertRawEntry(string? md5, string? sha256, bool removed = false, string memo = "before", string? title = null)
        {
            using var db = new LR2SongDBExtended(dbPath);
            db.Execute("INSERT INTO playlist_entry (playlist_id, md5, sha256, folder, title, lr2_bmsid, is_removed, memo) VALUES (9201, ?, ?, 'folder', ?, NULL, ?, ?);",
                md5, sha256, title, removed ? 1 : 0, memo);
        }
        internal void Execute(params string[] statements) { using var db = new LR2SongDBExtended(dbPath); foreach (string sql in statements) { db.Execute(sql); } }
        public void Dispose()
        {
            foreach (BMSTable table in Tables) { table.ReaderWriterLock.Dispose(); }
            collectionLock.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
