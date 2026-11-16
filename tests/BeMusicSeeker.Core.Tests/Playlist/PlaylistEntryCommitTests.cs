using System.Globalization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


/// <summary>entry owner の draft・実 SQLite・不変差分と局所仕事量を検証します。表示と必要出力の終端は既存 workspace 系列が分担します。</summary>
[TestClass]
public sealed class PlaylistEntryCommitTests
{
    private const string LowerMd5 = "abcdef0123456789abcdef0123456789";
    private const string MixedMd5 = "ABCDef0123456789ABCDef0123456789";
    private const string LowerSha256 = LowerMd5 + LowerMd5;
    private const string MixedSha256 = MixedMd5 + MixedMd5;

    /// <summary>header単独の実DB確定はentry版と日時を保ち、後続保存後も先の捕捉URI・名前を不変に保ちます。無変更要求はreceiptを増やしません。</summary>
    [TestMethod]
    public void CommitHeader_CapturesImmutableSavedHeaderWithoutEntryRevision()
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        int revision = table.PlaylistEntriesRevision;
        DateTime date = table.last_update;
        PlaylistEntryCommitReceipt Commit(PlaylistHeaderEdit edit) => fixture.Owner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareHeaderMutation(edit, observer) }, fixture.Receipts.Add).Single();
        PlaylistEntryCommitReceipt first = Commit(new PlaylistHeaderEdit
        {
            Fields = PlaylistHeaderFields.PageUrl,
            PageUrl = new Uri("https://example.test/first")
        });
        Commit(new PlaylistHeaderEdit
        {
            Fields = PlaylistHeaderFields.PageUrl | PlaylistHeaderFields.Name,
            PageUrl = new Uri("https://example.test/second"),
            Name = "second"
        });
        Assert.AreSame(table, first.Table);
        Assert.IsNotNull(first.Header);
        Assert.AreEqual(table.playlist_id, first.Header.PlaylistId);
        Assert.AreEqual(new Uri("https://example.test/first"), first.Header.PageUrl);
        Assert.AreEqual("table-9201", first.Header.Name);
        Assert.AreEqual(date, first.Header.LastUpdate);
        Assert.AreEqual(revision, first.BaseRevision);
        Assert.AreEqual(revision, first.Revision);
        Assert.IsTrue(first.Changes.IsEmpty);
        Assert.AreEqual(revision, table.PlaylistEntriesRevision);
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareHeaderMutation(new PlaylistHeaderEdit(), observer) }, fixture.Receipts.Add);
        Assert.AreEqual(2, fixture.Receipts.Count);
    }

    /// <summary>画面を経由しない保存入口でもURL1/2の非空無効URIは拒否し、正本・補完・DBと版を保持します。</summary>
    [TestMethod]
    [DataRow(nameof(BMSTableEntry.Url))]
    [DataRow(nameof(BMSTableEntry.Url_diff))]
    public void CommitDraft_InvalidUrlPreservesCurrentValues(string propertyName)
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry entry = table.entries[0];
        fixture.Commit(entry, nameof(BMSTableEntry.Url), "https://example.com/saved-main");
        fixture.Commit(entry, nameof(BMSTableEntry.Url_diff), "https://example.com/saved-diff");
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime-main"),
            new Uri("https://example.com/runtime-diff"), overwriteExisting: true);
        int revision = table.PlaylistEntriesRevision;
        int receipts = fixture.Receipts.Count;
        Assert.ThrowsExactly<UriFormatException>(() => fixture.Commit(entry, propertyName, "not a URI"));
        Assert.AreEqual(revision, table.PlaylistEntriesRevision);
        Assert.AreEqual(receipts, fixture.Receipts.Count);
        Assert.AreEqual(new Uri("https://example.com/saved-main"), entry.Url);
        Assert.AreEqual(new Uri("https://example.com/saved-diff"), entry.Url_diff);
        Assert.AreEqual(new Uri("https://example.com/runtime-main"), entry.EffectiveUrl);
        Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.EffectiveUrlDiff);
        BMSTableEntry saved = fixture.FreshStartupEntries().Single(row => row.playlist_id == entry.playlist_id && row.md5 == entry.md5);
        Assert.AreEqual(new Uri("https://example.com/saved-main"), saved.Url);
        Assert.AreEqual(new Uri("https://example.com/saved-diff"), saved.Url_diff);
    }

    /// <summary>raw NULL生存行をstartup readerから正規ownerへ渡し、編集とfreshな削除を別fixtureで読戻します。</summary>
    /// <remarks>NULLと0だけを同じ生存単位へ揃え、removed・同hash別folder/title・別表の行を保持します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CommitEntry_NullRemovedStateRoundTripsThroughStartupReader(bool remove)
    {
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, '" + LowerMd5 + "', 'target', '', NULL, 'before');",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, '" + LowerMd5 + "', 'target', '', 1, 'removed');",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, '" + LowerMd5 + "', 'target', 'other', NULL, 'other-folder');",
            "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed, memo) VALUES (9201, '" + LowerMd5 + "', 'other-title', '', NULL, 'other-title');");
        fixture.Execute("CREATE TABLE null_state_work (kind TEXT);",
            "CREATE TRIGGER count_null_delete AFTER DELETE ON playlist_entry BEGIN INSERT INTO null_state_work VALUES ('delete'); END;",
            "CREATE TRIGGER count_null_insert AFTER INSERT ON playlist_entry BEGIN INSERT INTO null_state_work VALUES ('insert'); END;");
        BMSTable table = fixture.Tables[0];
        table.entries = [.. fixture.FreshStartupEntries().Where(entry => entry.playlist_id == 9201 && !entry.is_removed)];
        BMSTableEntry target = table.entries.First(entry => entry.folder == string.Empty);
        Assert.AreEqual("target", target.title);
        Assert.IsTrue(fixture.Repository.LoadPersistedPlaylistEntries(9201, activeOnly: true).Any(entry => entry.memo == "before"));
        if (remove)
        {
            fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareEntryMutation([target], [], observer) }, fixture.Receipts.Add);
        }
        else { fixture.Commit(target, "memo", "after"); }
        BMSTableEntry[] fresh = [.. fixture.FreshStartupEntries().Where(entry => entry.playlist_id == 9201)];
        BMSTableEntry[] activeTarget = [.. fresh.Where(entry => !entry.is_removed && entry.folder == string.Empty && entry.title == "target")];
        Assert.AreEqual(remove ? 0 : 1, activeTarget.Length);
        if (!remove) { Assert.AreEqual("after", activeTarget.Single().memo); }
        Assert.AreEqual(1, fresh.Count(entry => entry.is_removed && entry.memo == "removed"));
        Assert.AreEqual(1, fresh.Count(entry => entry.folder == "other" && entry.memo == "other-folder"));
        Assert.AreEqual(1, fresh.Count(entry => entry.title == "other-title" && entry.memo == "other-title"));
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9202;"));
        Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed IS NULL;"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM null_state_work WHERE kind = 'delete';"));
        Assert.AreEqual(remove ? 0 : 1, db.ExecuteScalar<int>("SELECT count(*) FROM null_state_work WHERE kind = 'insert';"));
    }

    /// <summary>prefixとheaderを同じwriterで候補化し、少数folderだけの捕捉・SQL・不変差分と順序・日時保持を確認します。</summary>
    [TestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void CommitHeader_PrefixTouchesOnlyAffectedFolderAndPreservesCurrentEntryValues(int background)
    {
        using var fixture = new Fixture(background);
        BMSTable table = fixture.Tables[0];
        table.compat_prefix = "P";
        table.entries = [.. table.entries.Select((entry, index) => new BMSTableEntry
        { md5 = entry.md5, title = entry.title, folder = index == 0 ? "P1" : "X", memo = "current memo" })];
        table.Folder_order = ["X", "P1"];
        fixture.Owner.ReplaceTablesWithEntries([table]);
        BMSTableEntry target = table.entries[0], retained = table.entries[1];
        IReadOnlyList<PlaylistFolderNode> oldNodes = table.FolderNodes;
        IReadOnlyList<string> oldOrder = table.Folder_order;
        PlaylistEntryReadSnapshot old = table.CaptureEntryReadSnapshot(target);
        DateTime lastUpdate = table.last_update;
        int version = table.PlaylistEntriesRevision;
        fixture.Execute("CREATE TABLE prefix_work (kind TEXT);",
            "CREATE TRIGGER count_prefix_delete AFTER DELETE ON playlist_entry BEGIN INSERT INTO prefix_work VALUES ('delete'); END;",
            "CREATE TRIGGER count_prefix_insert AFTER INSERT ON playlist_entry BEGIN INSERT INTO prefix_work VALUES ('insert'); END;");
        var edit = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = string.Empty };
        PlaylistEntryCommitReceipt receipt = fixture.Owner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareHeaderMutation(edit, observer) }, fixture.Receipts.Add).Single();
        Assert.AreEqual(1, fixture.Work.ReferenceReads);
        Assert.AreEqual(1, fixture.Work.Candidates);
        Assert.AreEqual(0, fixture.Work.MembershipChanges);
        Assert.AreSame(target, table.entries[0]); Assert.AreSame(retained, table.entries[1]);
        Assert.AreEqual("1", target.folder); Assert.AreEqual("current memo", target.memo);
        CollectionAssert.AreEqual(new[] { "X", "P1" }, oldOrder.ToArray());
        CollectionAssert.AreEqual(new[] { "X", "P1" }, oldNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToArray());
        CollectionAssert.AreEqual(new[] { "X", "1" }, table.FolderNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToArray());
        Assert.AreEqual("P1", old.Entries.Single().After.Folder);
        Assert.AreEqual(lastUpdate, table.last_update); Assert.AreEqual(version + 1, table.PlaylistEntriesRevision);
        CollectionAssert.AreEqual(new[] { "X", "1" }, receipt.FolderOrder.ToArray());
        Assert.AreEqual("P1", receipt.Changes.Single().Before.Folder); Assert.AreEqual("1", receipt.Changes.Single().After.Folder);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM prefix_work WHERE kind='delete';"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM prefix_work WHERE kind='insert';"));
        Assert.AreEqual(background, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id=9201;"));
    }

    /// <summary>raw NULLを含む二表のheader/prefixを一括保存し、二表目の実DB拒否では全表の旧値・所属・版を保ちます。</summary>
    [TestMethod]
    public void CommitHeader_SecondTableFailureLeavesNullRowsAndAllCurrentFactsUnchanged()
    {
        using var fixture = new Fixture();
        fixture.Execute("UPDATE playlist_entry SET is_removed = NULL;",
            "CREATE TRIGGER fail_second_header BEFORE INSERT ON playlist WHEN NEW.playlist_id = 9202 BEGIN SELECT RAISE(ABORT, 'second header failed'); END;");
        IReadOnlyList<BMSTableEntry>[] memberships = [.. fixture.Tables.Select(table => table.entries)];
        int[] versions = [.. fixture.Tables.Select(table => table.PlaylistEntriesRevision)];
        DateTime[] dates = [.. fixture.Tables.Select(table => table.last_update)];
        IReadOnlyList<PlaylistFolderNode>[] oldNodes = fixture.Tables.Select(table => table.FolderNodes).ToArray();
        IReadOnlyList<string>[] oldOrders = fixture.Tables.Select(table => table.Folder_order).ToArray();
        var edit = new PlaylistHeaderEdit
        {
            Fields = PlaylistHeaderFields.Name | PlaylistHeaderFields.CompatPrefix,
            Name = "new header",
            CompatPrefix = "Q"
        };
        Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>> requests = fixture.Tables.ToDictionary(table => table,
            table => new Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>(observer => table.PrepareHeaderMutation(edit, observer)));
        Assert.ThrowsExactly<SQLite.SQLiteException>(() => fixture.Owner.CommitEntryMutations(requests, fixture.Receipts.Add));
        for (int index = 0; index < fixture.Tables.Length; index++)
        {
            BMSTable table = fixture.Tables[index];
            Assert.AreSame(memberships[index], table.entries);
            Assert.AreSame(oldNodes[index], table.FolderNodes); Assert.AreSame(oldOrders[index], table.Folder_order);
            Assert.AreEqual("table-" + table.playlist_id, table.name);
            Assert.AreEqual(string.Empty, table.compat_prefix);
            Assert.AreEqual(versions[index], table.PlaylistEntriesRevision);
            Assert.AreEqual(dates[index], table.last_update);
            Assert.IsTrue(table.entries.All(entry => entry.folder == "folder"));
        }
        Assert.AreEqual(0, fixture.Receipts.Count);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(4, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE folder='folder' AND is_removed IS NULL;"));
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM playlist WHERE name='new header';"));
    }

    /// <summary>header要求後に別属性を正規確定しても、遅れて保存するprefix候補はwriter内currentから作り、古いmemo/URLを戻しません。</summary>
    [TestMethod]
    public void CommitHeader_DelayedPrefixRequestPreservesLaterMemoAndUrlCommits()
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry target = table.entries[0];
        PlaylistEntryReadSnapshot old = table.CaptureEntryReadSnapshot(target);
        var edit = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = "Q" };
        fixture.Commit(target, "memo", "newer memo");
        fixture.Commit(target, "Url", "https://example.test/newer");
        DateTime date = table.last_update;
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareHeaderMutation(edit, observer) }, fixture.Receipts.Add);
        Assert.AreEqual("Qfolder", target.folder);
        Assert.AreEqual("newer memo", target.memo);
        Assert.AreEqual(new Uri("https://example.test/newer"), target.Url);
        Assert.AreEqual("memo before", old.Entries.Single().After.Memo);
        Assert.AreEqual(new Uri("https://example.test/original"), old.Entries.Single().After.Url);
        Assert.AreEqual(date, table.last_update);
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual("newer memo", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=9201 AND title='row-0';"));
        Assert.AreEqual("https://example.test/newer", db.ExecuteScalar<string>("SELECT url FROM playlist_entry WHERE playlist_id=9201 AND title='row-0';"));
    }

    /// <summary>
    /// 3.2.0.0 開発版が作った候補索引の定義文そのものです（基準版の <c>RebuildPlaylistEntryIndexes</c> と <c>HashComparisonExpression</c> から組み立てた固定入力）。
    /// 式索引と 3.7.16 の <c>char()</c> を含み、SQLite 3.6.7 は解析できません。
    /// </summary>
    internal const string DevelopmentExpressionCandidateIndexSql =
        "CREATE INDEX playlist_entry_idx_candidate ON playlist_entry (playlist_id, ifnull(is_removed, 0), "
        + "(CASE WHEN md5 IS NULL OR trim(md5, char(9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)) = '' THEN '' ELSE md5 END) COLLATE NOCASE, "
        + "(CASE WHEN sha256 IS NULL OR trim(sha256, char(9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)) = '' THEN '' ELSE sha256 END) COLLATE NOCASE, "
        + "ifnull(folder, ''), lr2_bmsid, ifnull(title, ''))";

    /// <summary>
    /// 条件: is_removed を NULL にした行を含む DB に、開発版の式の候補索引を置き、playlist・app-owned・repair の各準備入口を通す。
    /// 期待結果: 準備前は検査器が式索引を報告し、準備後は sqlite_master 全体が SQLite 3.6.7 の互換範囲で、同名の候補索引が存在し、
    /// 全行の rowid と各列の quote() 値と UNIQUE 索引の定義が準備前と一致する。同じ入口の二回目では候補索引の定義行（rowid・rootpage・sql）が変わらず、
    /// playlist・app-owned では <c>PRAGMA schema_version</c> も変わらない。
    /// 検出する誤り: 式索引が残る退行、置換のための行の書換え・UNIQUE 変更、一致した定義を毎回作り直す誤り。
    /// </summary>
    [TestMethod]
    [DataRow("playlist")]
    [DataRow("app-owned")]
    [DataRow("repair")]
    public void EnsureSchema_ReplacesDevelopmentExpressionCandidateIndexWithoutRewritingRows(string entry)
    {
        using var fixture = new Fixture();
        fixture.Execute("UPDATE playlist_entry SET is_removed = NULL WHERE playlist_id = 9201;",
            "DROP INDEX playlist_entry_idx_candidate;",
            DevelopmentExpressionCandidateIndexSql + ";");
        string[] rowsBefore;
        string uniqueBefore;
        using (LR2SongDBExtended before = fixture.OpenRead())
        {
            CollectionAssert.Contains(Sqlite367SchemaCompatibility.FindViolations(before).Select(violation => violation.Category).ToList(),
                Sqlite367ViolationCategory.ExpressionIndex);
            rowsBefore = Fixture.ReadRawEntryRows(before);
            uniqueBefore = before.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            Assert.AreEqual(2, before.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND is_removed IS NULL;"));
        }
        Action prepare = entry switch
        {
            "playlist" => fixture.EnsureSchema,
            "app-owned" => fixture.EnsureAppOwnedSchema,
            _ => fixture.RepairAppOwnedSchema,
        };
        prepare();
        string candidateDefinition;
        long schemaVersion;
        using (LR2SongDBExtended after = fixture.OpenRead())
        {
            Sqlite367SchemaCompatibility.AssertCompatible(after);
            candidateDefinition = Fixture.ReadCandidateIndexDefinition(after);
            CollectionAssert.AreEqual(rowsBefore, Fixture.ReadRawEntryRows(after));
            Assert.AreEqual(uniqueBefore, after.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';"));
            schemaVersion = after.ExecuteScalar<long>("PRAGMA schema_version;");
        }
        prepare();
        using LR2SongDBExtended again = fixture.OpenRead();
        Assert.AreEqual(candidateDefinition, Fixture.ReadCandidateIndexDefinition(again));
        // 修復は既存仕様で chart_digest_map を毎回 DROP・CREATE し schema_version を進めるため、修復では候補索引の定義行の不変だけを判定します。
        if (entry != "repair")
        {
            Assert.AreEqual(schemaVersion, again.ExecuteScalar<long>("PRAGMA schema_version;"));
        }
    }

    /// <summary>
    /// 条件: 両 hash が NULL の対象を読み込んだ後、md5 側・sha256 側のそれぞれで、同じ所属の生存行として次の二行を生値で置き、対象の MEMO 保存と削除を行う。
    /// 一つは指定の空白文字で始まり空白だけからなる hash の行、もう一つは同じ空白文字で始まり後ろに非空白が続く hash の行。
    /// 期待結果: 空白だけの行は対象と同じ空キーとして保存・削除のどちらでも消え、非空白が続く行は rowid と全列の値ごと残る。
    /// 検出する誤り: 空白文字の先頭区間の境界の誤り（区間から外れた空白文字の行が残る）と、
    /// 区間の探索後の残余の <c>trim</c> 検査を落として非空白が続く別の hash を同値として消す誤り。
    /// </summary>
    /// <remarks>
    /// .NET の <see cref="char.IsWhiteSpace(char)"/> が空白とする25文字すべてを、符号位置で指定します。
    /// 非空白が続く hash は読込みで拒否されるため、対象の読込み後に置きます。
    /// </remarks>
    [TestMethod]
    [DataRow(0x09)]
    [DataRow(0x0A)]
    [DataRow(0x0B)]
    [DataRow(0x0C)]
    [DataRow(0x0D)]
    [DataRow(0x20)]
    [DataRow(0x85)]
    [DataRow(0xA0)]
    [DataRow(0x1680)]
    [DataRow(0x2000)]
    [DataRow(0x2001)]
    [DataRow(0x2002)]
    [DataRow(0x2003)]
    [DataRow(0x2004)]
    [DataRow(0x2005)]
    [DataRow(0x2006)]
    [DataRow(0x2007)]
    [DataRow(0x2008)]
    [DataRow(0x2009)]
    [DataRow(0x200A)]
    [DataRow(0x2028)]
    [DataRow(0x2029)]
    [DataRow(0x202F)]
    [DataRow(0x205F)]
    [DataRow(0x3000)]
    public void CommitDraft_BlankHashLeadingWhitespaceRangesMatchOnlyWhitespaceOnlyValues(int codePoint)
    {
        string leading = ((char)codePoint).ToString();
        Assert.IsTrue(char.IsWhiteSpace(leading[0]));
        foreach (string column in new[] { "md5", "sha256" })
        {
            using var fixture = new Fixture();
            fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
            fixture.InsertRawEntry(null, null, memo: "target");
            BMSTable table = fixture.Tables[0];
            table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
            BMSTableEntry target = table.entries.Single(entry => entry.memo == "target");
            string blank = leading + leading + " ";
            string nonBlank = leading + LowerMd5;
            void InsertVariant(string value, string memo)
                => fixture.InsertRawEntry(column == "md5" ? value : null, column == "sha256" ? value : null, memo: memo);

            InsertVariant(blank, "blank-save");
            InsertVariant(nonBlank, "non-blank");
            long nonBlankRowId = fixture.ReadRowIdByMemo("non-blank");
            string nonBlankBefore = fixture.ReadRawEntryRow(nonBlankRowId);
            fixture.Commit(target, "memo", "after");
            Assert.AreEqual(0, fixture.CountByMemo("blank-save"), column + " U+" + codePoint.ToString("X4") + " の空白だけの行が保存で同値として扱われていません。");
            Assert.AreEqual(1, fixture.CountByMemo("after"));
            Assert.AreEqual(nonBlankBefore, fixture.ReadRawEntryRow(nonBlankRowId), column + " U+" + codePoint.ToString("X4") + " の非空白が続く行が保存で変わりました。");

            InsertVariant(blank, "blank-remove");
            fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareEntryMutation([target], [], observer) }, fixture.Receipts.Add);
            Assert.AreEqual(0, fixture.CountByMemo("blank-remove"), column + " U+" + codePoint.ToString("X4") + " の空白だけの行が削除で同値として扱われていません。");
            Assert.AreEqual(0, fixture.CountByMemo("after"));
            Assert.AreEqual(nonBlankBefore, fixture.ReadRawEntryRow(nonBlankRowId), column + " U+" + codePoint.ToString("X4") + " の非空白が続く行が削除で変わりました。");
        }
    }

    /// <summary>
    /// 条件: 同じ表・同じハッシュ・同じ生存状態で、所属キーの一列だけが違う対象と変種の生存行を置き、対象の MEMO 保存と削除を行う。
    /// lr2_bmsid は NULL と空、'10' と '010'、folder・title は大小文字の違いと前後空白の違いを使う。
    /// 期待結果: どちらの操作でも変種の行は残り、rowid と全列の値が変わらない。
    /// 検出する誤り: 問い合わせ側の正規化を hash 以外の列へ広げ、大小文字・前後空白・NULL と空の違う別の所属を同値候補として消す誤り。
    /// </summary>
    /// <remarks>folder・title の NULL と空は既存の識別で同値のため、ここでは扱いません。</remarks>
    [TestMethod]
    [DataRow("lr2_bmsid", null, "")]
    [DataRow("lr2_bmsid", "", null)]
    [DataRow("lr2_bmsid", "10", "010")]
    [DataRow("folder", "Folder", "folder")]
    [DataRow("folder", " folder", "folder")]
    [DataRow("title", "Title", "title")]
    [DataRow("title", "title ", "title")]
    public void CommitDraft_ExactKeyColumnsKeepCaseAndBlankDistinctions(string column, string? targetValue, string? variantValue)
    {
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        fixture.InsertKeyVariantEntry(column, targetValue, "target");
        long variantRowId = fixture.InsertKeyVariantEntry(column, variantValue, "variant");
        string variantBefore = fixture.ReadRawEntryRow(variantRowId);
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single(entry => entry.memo == "target");
        Assert.AreEqual(targetValue, column switch { "lr2_bmsid" => target.lr2_bmsid, "folder" => target.folder, _ => target.title });
        fixture.Commit(target, "memo", "after");
        Assert.AreEqual(variantBefore, fixture.ReadRawEntryRow(variantRowId));
        using (LR2SongDBExtended db = fixture.OpenRead())
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'after';"));
            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'target';"));
        }
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareEntryMutation([target], [], observer) }, fixture.Receipts.Add);
        Assert.AreEqual(variantBefore, fixture.ReadRawEntryRow(variantRowId));
        using LR2SongDBExtended after = fixture.OpenRead();
        Assert.AreEqual(0, after.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = 'after';"));
    }

    /// <summary>
    /// 条件: 両 hash・folder・title・is_removed が NULL の対象を読み込んだ後、同じ表に両 hash が NULL の生存行として、
    /// folder（NULL・空）、title（NULL・空）、is_removed（NULL・0）の8通りの組合せと、is_removed が1の削除履歴の行を生値で置き、対象の MEMO 保存を行う。
    /// 続けて同じ8通りの行を置き直し、対象を削除する。
    /// 期待結果: 保存と削除のどちらでも8通りの行はすべて対象と同値として消え、保存後の対象は一行だけ残り、削除後は残らない。削除履歴の行は残る。
    /// 検出する誤り: 両 hash が空キーのときに所属キー列を索引で探索する形から、folder・title の <c>IS NULL</c> または <c>= ''</c>、
    /// is_removed の <c>IS NULL</c> または <c>= 0</c> の形を落とし、NULL と空、NULL と0の片方の表現の行を同値候補から漏らす誤り。
    /// </summary>
    /// <remarks>
    /// 表現の違う行が別の行として残ることを確かめる <see cref="CommitDraft_ExactKeyColumnsKeepCaseAndBlankDistinctions"/> とは逆に、同値の表現がすべて同じ候補になることを確かめます。
    /// 読込みの重複の統合に左右されないよう、変種は対象の読込み後に置きます。
    /// </remarks>
    [TestMethod]
    public void CommitDraft_NullHashEmptyEquivalentKeyFormsAllMatchForSaveAndRemoval()
    {
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        fixture.InsertBlankHashEntry(null, null, null, "target");
        fixture.InsertBlankHashEntry(null, null, 1, "removed");
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single(entry => entry.memo == "target");
        string?[] blanks = [null, string.Empty];
        int?[] liveStates = [null, 0];
        IReadOnlyList<string> InsertVariants(string prefix)
        {
            var memos = new List<string>();
            foreach (string? folder in blanks)
            {
                foreach (string? title in blanks)
                {
                    foreach (int? state in liveStates)
                    {
                        string memo = prefix + " folder=" + (folder ?? "NULL") + " title=" + (title ?? "NULL") + " is_removed=" + (state?.ToString() ?? "NULL");
                        fixture.InsertBlankHashEntry(folder, title, state, memo);
                        memos.Add(memo);
                    }
                }
            }
            return memos;
        }

        IReadOnlyList<string> saveVariants = InsertVariants("save");
        fixture.Commit(target, "memo", "after");
        foreach (string memo in saveVariants)
        {
            Assert.AreEqual(0, fixture.CountByMemo(memo), memo + " の行が保存で同値として扱われていません。");
        }
        Assert.AreEqual(1, fixture.CountByMemo("after"));
        Assert.AreEqual(1, fixture.CountByMemo("removed"));

        IReadOnlyList<string> removeVariants = InsertVariants("remove");
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareEntryMutation([target], [], observer) }, fixture.Receipts.Add);
        foreach (string memo in removeVariants)
        {
            Assert.AreEqual(0, fixture.CountByMemo(memo), memo + " の行が削除で同値として扱われていません。");
        }
        Assert.AreEqual(0, fixture.CountByMemo("after"));
        Assert.AreEqual(1, fixture.CountByMemo("removed"));
    }

    /// <summary>
    /// 条件: 片方の hash だけが非空で他方が NULL の対象を読み込んだ後、同じ所属の生存行として、非空の hash は対象と同じで、
    /// 他方の hash が空・空白一文字・空白だけ（全角空白とタブ）の行と、他方の hash が非空の別値の行・空白の後に非空白が続く値の行を生値で置き、
    /// 対象の MEMO 保存を行う。続けて空と空白の行を置き直し、対象を削除する。
    /// md5-target は対象の MD5 が非空で SHA-256 が NULL、sha256-target は対象の SHA-256 が非空で MD5 が NULL。
    /// 期待結果: 空と空白の行は保存と削除のどちらでも対象と同値として消え、非空の別値の行と空白の後に非空白が続く行は rowid と全列の値ごと残る。
    /// 検出する誤り: md5-target では、非空の MD5 で絞った後の SHA-256 の空キーの残余照合を <c>IS NULL</c> だけにし、空・空白だけの行を漏らす誤り。
    /// sha256-target では、MD5 の空キーの探索形から空の一点の形や空白文字の範囲の形を外し、その表現の行を漏らす誤り。
    /// どちらも、空白の後に非空白が続く値を空キーとして消す誤りも検出します。
    /// </summary>
    /// <remarks>
    /// 両 hash が空キーの組合せは <see cref="CommitDraft_NullHashEmptyEquivalentKeyFormsAllMatchForSaveAndRemoval"/> と
    /// <see cref="CommitDraft_BlankHashLeadingWhitespaceRangesMatchOnlyWhitespaceOnlyValues"/> が担い、ここでは一方の hash が非空の分岐を扱います。
    /// 読込みで拒否される空白付きの値を含むため、変種は対象の読込み後に置きます。
    /// </remarks>
    [TestMethod]
    [DataRow("md5-target")]
    [DataRow("sha256-target")]
    public void CommitDraft_OtherHashBlankFormsMatchWhenOneHashIsNonBlank(string targetKind)
    {
        bool md5Target = targetKind == "md5-target";
        string? Md5Of(string? other) => md5Target ? LowerMd5 : other;
        string? Sha256Of(string? other) => md5Target ? other : LowerSha256;
        using var fixture = new Fixture();
        fixture.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
        fixture.InsertRawEntry(Md5Of(null), Sha256Of(null), memo: "target");
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single(entry => entry.memo == "target");
        Assert.AreEqual(Md5Of(null), target.md5);
        Assert.AreEqual(Sha256Of(null), target.sha256);

        string otherNonBlank = md5Target ? LowerSha256 : 2.ToString("x32");
        string otherLeadingBlank = " " + (md5Target ? LowerSha256 : LowerMd5);
        (string Value, string Name)[] blanks = [(string.Empty, "empty"), (" ", "space"), ("　\t", "ideographic-space-tab")];
        IReadOnlyList<string> InsertBlankVariants(string prefix)
        {
            var memos = new List<string>();
            foreach ((string value, string name) in blanks)
            {
                string memo = prefix + " " + targetKind + " other=" + name;
                fixture.InsertRawEntry(Md5Of(value), Sha256Of(value), memo: memo);
                memos.Add(memo);
            }
            return memos;
        }

        fixture.InsertRawEntry(Md5Of(otherNonBlank), Sha256Of(otherNonBlank), memo: "other-non-blank");
        fixture.InsertRawEntry(Md5Of(otherLeadingBlank), Sha256Of(otherLeadingBlank), memo: "other-leading-blank");
        long[] retainedRowIds = [fixture.ReadRowIdByMemo("other-non-blank"), fixture.ReadRowIdByMemo("other-leading-blank")];
        string[] retainedBefore = [.. retainedRowIds.Select(fixture.ReadRawEntryRow)];
        void AssertRetainedUnchanged(string operation)
        {
            for (int index = 0; index < retainedRowIds.Length; index++)
            {
                Assert.AreEqual(retainedBefore[index], fixture.ReadRawEntryRow(retainedRowIds[index]),
                    targetKind + " の別キーの行が" + operation + "で変わりました。");
            }
        }

        IReadOnlyList<string> saveVariants = InsertBlankVariants("save");
        fixture.Commit(target, "memo", "after");
        foreach (string memo in saveVariants)
        {
            Assert.AreEqual(0, fixture.CountByMemo(memo), memo + " の行が保存で同値として扱われていません。");
        }
        Assert.AreEqual(1, fixture.CountByMemo("after"));
        AssertRetainedUnchanged("保存");

        IReadOnlyList<string> removeVariants = InsertBlankVariants("remove");
        fixture.Owner.CommitEntryMutations(new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
        { [table] = observer => table.PrepareEntryMutation([target], [], observer) }, fixture.Receipts.Add);
        foreach (string memo in removeVariants)
        {
            Assert.AreEqual(0, fixture.CountByMemo(memo), memo + " の行が削除で同値として扱われていません。");
        }
        Assert.AreEqual(0, fixture.CountByMemo("after"));
        AssertRetainedUnchanged("削除");
    }

    /// <summary>固定dot表示の往復・Float全体・空nullを実owner/DBで確認し、装飾・カンマ・部分数値・非有限値は保存前に拒否します。非編集値・日時・版・配送を保持します。</summary>
    [TestMethod]
    [DataRow("fr-FR", 1.5, null, 1.5, "1.5", false)]
    [DataRow("de-DE", -1.5, null, -1.5, "-1.5", false)]
    [DataRow("th-TH", 1.5000000000000002, null, 1.5000000000000002, "1.5000000000000002", false)]
    [DataRow("en-US", 12d, null, 12d, "12", false)]
    [DataRow("fr-FR", null, null, null, "", false)]
    [DataRow("fr-FR", 1.5, "  -2.5e1  ", -25d, null, false)]
    [DataRow("de-DE", 2d, ".5", .5, null, false)]
    [DataRow("en-US", 2d, "+12.5", 12.5, null, false)]
    [DataRow("fr-FR", 1.5, "", null, null, false)]
    [DataRow("fr-FR", 1.5, "  ", null, null, false)]
    [DataRow("fr-FR", 1.5, "2,5", 1.5, null, true)]
    [DataRow("en-US", 1.5, "1,000", 1.5, null, true)]
    [DataRow("en-US", 1.5, "★12.5", 1.5, null, true)]
    [DataRow("fr-FR", 1.5, "12.5x", 1.5, null, true)]
    [DataRow("en-US", 1.5, "1 2", 1.5, null, true)]
    [DataRow("de-DE", 1.5, "A", 1.5, null, true)]
    [DataRow("fr-FR", null, "A", null, null, true)]
    [DataRow("en-US", 1.5, "NaN", 1.5, null, true)]
    [DataRow("fr-FR", 1.5, "Infinity", 1.5, null, true)]
    [DataRow("en-US", 1.5, "-Infinity", 1.5, null, true)]
    [DataRow("en-US", 1.5, "1e9999", 1.5, null, true)]
    public void CommitDraft_LevelUsesSavedNumberAndKeepsOtherValues(string culture, double? initialLevel, string? text, double? expected, string? displayed, bool rejected)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            using var fixture = new Fixture(initialLevel: initialLevel);
            BMSTable table = fixture.Tables[0];
            BMSTableEntry entry = table.entries[0];
            if (text == null)
            {
                text = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow().Level;
                Assert.AreEqual(displayed, text);
            }
            PlaylistEntryReadSnapshot old = table.CaptureEntryReadSnapshot(entry);
            int lookupVersion = table.EntryLookupRevision;
            DateTime oldDate = table.last_update;
            DateTime entryDate = entry.adddate;
            int receiptsBefore = fixture.Receipts.Count;
            if (rejected)
            {
                Assert.ThrowsExactly<FormatException>(() => fixture.Commit(entry, "Level", text));
                Assert.AreEqual(initialLevel, entry.level);
                Assert.AreEqual(old.Revision, table.PlaylistEntriesRevision);
                Assert.AreEqual(lookupVersion, table.EntryLookupRevision);
                Assert.AreEqual(oldDate, table.last_update);
                Assert.AreEqual(entryDate, entry.adddate);
                Assert.AreEqual(receiptsBefore, fixture.Receipts.Count);
                Assert.AreEqual("memo before", entry.memo);
                Assert.AreEqual("comment before", entry.comment);
                Assert.AreEqual(new Uri("https://example.test/original"), entry.Url);
                using LR2SongDBExtended unchangedDb = fixture.OpenRead();
                Assert.AreEqual(initialLevel, unchangedDb.ExecuteScalar<double?>("SELECT level FROM playlist_entry WHERE playlist_id = 9201 AND title = 'row-0';"));
                return;
            }
            PlaylistEntryCommitReceipt receipt = fixture.Commit(entry, "Level", text);
            Assert.AreEqual(entryDate, entry.adddate);
            Assert.AreEqual(expected, entry.level);
            Assert.AreEqual("memo before", entry.memo);
            Assert.AreEqual("comment before", entry.comment);
            Assert.AreEqual(new Uri("https://example.test/original"), entry.Url);
            Assert.AreEqual(initialLevel, old.Entries.Single().After.Level);
            Assert.AreEqual(lookupVersion, table.EntryLookupRevision);
            Assert.AreEqual(expected == initialLevel ? old.Revision : old.Revision + 1, receipt.Revision);
            if (expected != initialLevel) { Assert.AreEqual(expected, receipt.Changes.Single().After.Level); }
            else { Assert.IsTrue(receipt.Changes.IsEmpty); }
            Assert.AreEqual(table.PlaylistEntriesRevision, table.CaptureEntryReadSnapshot(entry).Revision);
            using LR2SongDBExtended db = fixture.OpenRead();
            Assert.AreEqual(expected, db.ExecuteScalar<double?>("SELECT level FROM playlist_entry WHERE playlist_id = 9201 AND title = 'row-0';"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
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
        Assert.ThrowsExactly<SQLite.SQLiteException>(() => fixture.Commit(entry, "memo", "draft"));
        Assert.AreEqual("memo before", entry.memo);
        Assert.AreEqual(oldDate, table.last_update);
        Assert.AreEqual(oldVersion, table.PlaylistEntriesRevision);
        Assert.AreEqual(0, fixture.Receipts.Count);
        fixture.Execute("DROP TRIGGER fail_entry;");
        BMSTableEntry stale = entry.CreatePlaylistReloadSnapshot();
        stale.parent = table;
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Commit(stale, "memo", "wrong"));
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
        Assert.ThrowsExactly<SQLite.SQLiteException>(() => fixture.Owner.CommitEntryMutations(requests, fixture.Receipts.Add));
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
    [TestMethod]
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

    /// <summary>明示追加は同じ所属キーの削除履歴を入力値へ置換し、別hash・別folderの履歴を保持します。DB前失敗では元所属と全履歴を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CommitLocalChanges_AddToRemovedDestinationReplacesHistoryAtomically(bool failInsert)
    {
        using var fixture = new Fixture();
        BMSTable table = fixture.Tables[0];
        BMSTableEntry source = table.entries[0];
        source.folder = "source";
        source.title = "current title";
        source.sha256 = new string('a', 64);
        source.memo = "current memo";
        BMSTableEntry retained = table.entries[1];
        retained.folder = "source";
        BMSTableEntry history = new() { md5 = source.md5, sha256 = new string('b', 64), title = "old title", folder = "destination", memo = "old memo", is_removed = true };
        BMSTableEntry otherHash = new() { md5 = new string('c', 32), folder = "destination", memo = "other hash", is_removed = true };
        BMSTableEntry otherFolder = new() { md5 = source.md5, folder = "other", memo = "other folder", is_removed = true };
        table.entries = [source, history, otherHash, otherFolder, retained];
        fixture.Owner.ReplaceTablesWithEntries([table]);
        PlaylistEntryValue[] before = [.. table.entries.Select(entry => entry.CaptureValue())];
        DateTime updated = table.last_update;
        int revision = table.PlaylistEntriesRevision;
        int lookupRevision = table.EntryLookupRevision;
        BMSTableEntry input = source.CreatePlaylistReloadSnapshot();
        if (failInsert)
        {
            fixture.Execute("CREATE TRIGGER reject_destination BEFORE INSERT ON playlist_entry WHEN NEW.folder = 'destination' AND NEW.is_removed = 0 BEGIN SELECT RAISE(ABORT, 'reject destination'); END;");
        }
        void Commit() => fixture.Owner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>>
            { [table] = observer => table.PrepareEntryMutation([source], [(input, "destination")], observer) }, fixture.Receipts.Add);
        if (failInsert)
        {
            Assert.ThrowsExactly<SQLite.SQLiteException>(Commit);
            CollectionAssert.AreEqual(before, table.entries.Select(entry => entry.CaptureValue()).ToArray());
            CollectionAssert.AreEquivalent(before, fixture.FreshStartupEntries().Where(entry => entry.playlist_id == table.playlist_id).Select(entry => entry.CaptureValue()).ToArray());
            Assert.AreEqual(updated, table.last_update);
            Assert.AreEqual(revision, table.PlaylistEntriesRevision);
            Assert.AreEqual(lookupRevision, table.EntryLookupRevision);
            Assert.AreEqual(0, fixture.Receipts.Count);
            Assert.AreSame(source, table.entries[0]);
            Assert.AreSame(history, table.entries[1]);
            return;
        }
        Commit();
        BMSTableEntry active = table.entries.Single(entry => entry.folder == "destination" && entry.md5 == source.md5);
        Assert.IsFalse(active.is_removed);
        Assert.AreEqual("current memo", active.memo);
        Assert.AreEqual("current title", active.title);
        Assert.AreEqual(new string('a', 64), active.sha256);
        Assert.IsFalse(table.entries.Contains(source));
        Assert.IsFalse(table.entries.Contains(history));
        CollectionAssert.AreEquivalent(new[] { otherHash, otherFolder, retained, input }, table.entries.ToArray());
        CollectionAssert.AreEquivalent(table.entries.Select(entry => entry.CaptureValue()).ToArray(),
            fixture.FreshStartupEntries().Where(entry => entry.playlist_id == table.playlist_id).Select(entry => entry.CaptureValue()).ToArray());
        Assert.AreEqual(1, fixture.Receipts.Count);
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
    [TestMethod]
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
        IReadOnlyList<PlaylistFolderNode> oldNodes = table.FolderNodes;
        IReadOnlyList<string> oldOrder = table.Folder_order;
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
        CollectionAssert.AreEqual(new[] { "source", "folder" }, oldOrder.ToArray());
        CollectionAssert.AreEqual(new[] { "source", "folder" }, oldNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToArray());
        CollectionAssert.AreEqual(new[] { destination, "folder" }, table.FolderNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToArray());
        Assert.AreEqual("folder", retained.folder);
        CollectionAssert.AreEqual(new[] { destination, "folder" }, table.Folder_order.ToArray());
        CollectionAssert.AreEqual(new[] { destination, "folder" }, receipt.FolderOrder.ToArray());
        using LR2SongDBExtended db = fixture.OpenRead();
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM entry_work WHERE kind = 'delete';"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM entry_work WHERE kind = 'insert';"));
        Assert.AreEqual(background, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201;"));
        CollectionAssert.AreEqual(new[] { destination, "folder" }, db.Table<BMSTable>().Single(row => row.playlist_id == 9201).Folder_order.ToArray());
    }

    /// <summary>memo 確定と URL 補完は所持数の依存を変更せず、既に温めた表の全 entry 再走査を要求しません。</summary>
    [TestMethod]
    public void CommitMemoAndRuntimeUrl_KeepSummaryCountCache()
    {
        using var fixture = new Fixture(128);
        BMSTable table = fixture.Tables[0];
        IReadOnlyList<PlaylistFolderNode> nodes = table.FolderNodes;
        IReadOnlyList<string> names = table.folder_list, order = table.Folder_order;
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
        Assert.AreSame(nodes, table.FolderNodes); Assert.AreSame(names, table.folder_list); Assert.AreSame(order, table.Folder_order);
        using (table.ReaderWriterLock.GetWriterGuard())
        {
            BMSTableEntry entry = table.entries[0];
            PlaylistEntryValue before = entry.CaptureValue();
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.test/completed"), null, overwriteExisting: true);
            table.CommitEntryValues([table.CaptureEntryChange(entry, before)]);
        }
        PlaylistSummaryCountResult last = summary.GetOrBuildTableCount(table, owned, CancellationToken.None, out bool urlHit);
        Assert.IsTrue(urlHit);
        Assert.AreSame(nodes, table.FolderNodes); Assert.AreSame(names, table.folder_list); Assert.AreSame(order, table.Folder_order);
        Assert.AreEqual(128, last.TotalCharts);
        Assert.AreEqual(1, last.OwnedCharts);
        Assert.AreEqual("memo after", table.entries[0].memo);
        using LR2SongDBExtended database = fixture.OpenRead();
        Assert.AreEqual("memo after", database.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=? AND md5=?;", table.playlist_id, table.entries[0].md5));
    }

    /// <summary>同表の背景だけを増やし、正規memo保存の候補SELECTの実VM_STEPが全件走査へ増えないことを確認します。</summary>
    /// <remarks>
    /// <para>md5・sha256・none は同folder・NULL titleで hash だけが違う背景です。</para>
    /// <para>
    /// none-folder・none-title は両 hash が NULL で、folder または title だけが違う背景です。
    /// hash 列の探索の後で所属キー列を索引で絞れない退行（残余条件だけの照合）を、背景件数に比例する VM_STEP として検出します。
    /// </para>
    /// <para>SEARCHによる広い索引範囲走査はFULLSCAN_STEPや変更行triggerだけでは検出できないため、PROFILEの実VM_STEPを比較します。</para>
    /// </remarks>
    [TestMethod]
    [DataRow("md5")]
    [DataRow("sha256")]
    [DataRow("none")]
    [DataRow("none-folder")]
    [DataRow("none-title")]
    public void CommitDraft_CandidateLookupWorkDoesNotGrowWithBackground(string hashKind)
    {
        CandidateLookupWork small = MeasureCandidateLookup(16, hashKind);
        CandidateLookupWork large = MeasureCandidateLookup(128, hashKind);
        Assert.AreEqual(2, small.StatementCount);
        Assert.AreEqual(2, large.StatementCount);
        Assert.AreEqual(1, small.ReturnedRows);
        Assert.AreEqual(1, large.ReturnedRows);
        Assert.AreEqual(0, small.FullScanSteps);
        Assert.AreEqual(0, large.FullScanSteps);
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
        bool observeCommit = false;
        using var fixture = new Fixture(background, db => { if (observeCommit) { observation.Attach(db); } });
        fixture.SeedCandidateLookupEntries(background, hashKind);
        BMSTable table = fixture.Tables[0];
        table.entries = fixture.Repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true);
        BMSTableEntry target = table.entries.Single(entry => hashKind switch
        {
            "md5" => entry.md5 == LowerMd5,
            "sha256" => entry.sha256 == LowerSha256,
            _ => entry.md5 == null && entry.sha256 == null && entry.folder == "folder" && string.IsNullOrEmpty(entry.title)
        });
        BMSTableEntry retained = table.entries.First(entry => !ReferenceEquals(entry, target));
        observeCommit = true;
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
        return new(statements.Length, statements.Sum(statement => statement.RowCount), statements.Sum(statement => statement.VmSteps), statements.Sum(statement => statement.FullScanSteps),
            [.. statements.SelectMany(statement => fixture.Explain(statement.Sql))]);
    }

    /// <summary>実DBのNULL/空/Unicode blank/大小文字と従来受理する最終LFを、setterと同じ意味の保存キーで扱います。</summary>
    /// <remarks>非blankをTrimしてLF付きhashを通常hashへ統合する誤りと、blankの旧行が残る誤りを検出します。</remarks>
    [TestMethod]
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
    [TestMethod]
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
        Assert.ThrowsExactly<FormatException>(() => fixture.Repository.LoadStartupPlaylistEntries());
    }

    /// <summary>通常readerのNULL title→空とraw startupのNULL保持の両方から、同値候補だけを保存・削除します。</summary>
    /// <remarks>NULL/空titleは同じentry identityです。最後の実entry削除はfolder用dummyを残し、削除済み・別表は保持します。</remarks>
    [TestMethod]
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

    private sealed record CandidateLookupWork(int StatementCount, int ReturnedRows, int VmSteps, int FullScanSteps, IReadOnlyList<string> Plans);

    private sealed class QueryPlanRow
    {
        public string detail { get; set; } = string.Empty;
    }

    private sealed class RawEntryRow
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ColumnRow
    {
        public string name { get; set; } = string.Empty;
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
        private readonly TestWorkspace ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        private readonly ApplicationDatabaseScope databaseScope = new();
        /// <summary>このfixtureの接続とモデルが共有するDB所有者です。</summary>
        internal ApplicationDatabaseScope DatabaseScope => databaseScope;

        private readonly string directory;
        private readonly string dbPath;
        private readonly ReaderWriterLockSlimWrapper collectionLock = new();
        internal WorkObserver Work { get; } = new();
        internal PlaylistAggregatePersistenceOwner Owner { get; }
        internal PlaylistPersistenceRepository Repository { get; }
        internal BMSTable[] Tables { get; }
        internal List<PlaylistEntryCommitReceipt> Receipts { get; } = [];

        internal Fixture(int background = 2, Action<LR2SongDBExtended>? initializeEntryWriteConnection = null, double? initialLevel = 2d)
        {
            try
            {
                directory = Path.Combine(ownedWorkspace.RootDirectory, nameof(PlaylistEntryCommitTests), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                dbPath = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, directory);
                PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
                Repository = new(databaseScope, dbPath, initializeEntryWriteConnection);
                Owner = new(Repository, collectionLock, new TestUiScheduler(() => null), Work);
                Tables = [CreateTable(9201, background, initialLevel), CreateTable(9202, 2)];
                Owner.MarkActiveTables(Tables);
                Owner.ReplaceTablesWithEntries(Tables);

            }
            catch (Exception primaryFailure)
            {
                try { ownedWorkspace.Dispose(); }
                catch (Exception cleanupFailure) { throw new AggregateException(primaryFailure, cleanupFailure); }
                throw;
            }
        }

        private static BMSTable CreateTable(int id, int count, double? initialLevel = 2d) => new()
        {
            playlist_id = id,
            name = "table-" + id,
            last_update = new DateTime(2026, 1, 1),
            entries = [.. Enumerable.Range(0, count).Select(index => new BMSTableEntry
            {
                md5 = (index + 1).ToString("x32"), title = "row-" + index, folder = "folder", level = initialLevel,
                memo = "memo before", comment = "comment before", Url = new Uri("https://example.test/original")
            })]
        };

        internal PlaylistEntryCommitReceipt Commit(BMSTableEntry entry, string property, string text)
            => Owner.CommitEntry(entry, property, text, null, Receipts.Add, () => collectionLock.GetReaderGuard());
        internal LR2SongDBExtended OpenRead() => new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), dbPath).OpenSongDbReadOnly();
        internal IReadOnlyList<BMSTableEntry> FreshStartupEntries() => new PlaylistPersistenceRepository(databaseScope, dbPath).LoadStartupPlaylistEntries().Entries;
        internal void EnsureSchema() => PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
        /// <summary>LR2連携の通常起動と同じ gateway のアプリ所有スキーマ準備を、このfixtureのDBへ実行します。</summary>
        internal void EnsureAppOwnedSchema() => new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), dbPath).EnsureAppOwnedSchema();
        /// <summary>修復承認後と同じ gateway のアプリ所有スキーマ修復を、このfixtureのDBへ実行します。</summary>
        internal void RepairAppOwnedSchema() => new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), dbPath).RepairAppOwnedSchema();
        /// <summary>候補索引の sqlite_master 行（rowid・rootpage・sql）を一つの文字列として読みます。</summary>
        internal static string ReadCandidateIndexDefinition(LR2SongDBExtended db)
            => db.Query<RawEntryRow>("SELECT rowid || '|' || rootpage || '|' || sql AS Value FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_candidate';").Single().Value;
        /// <summary>playlist_entry の全行を rowid 順に、rowid と全列の quote() 値の連結として読みます。</summary>
        internal static string[] ReadRawEntryRows(LR2SongDBExtended db)
            => [.. db.Query<RawEntryRow>("SELECT " + RawEntryRowExpression(db) + " AS Value FROM playlist_entry ORDER BY rowid;").Select(row => row.Value)];
        /// <summary>指定 rowid の playlist_entry 行を、rowid と全列の quote() 値の連結として読みます。</summary>
        internal string ReadRawEntryRow(long rowId)
        {
            using LR2SongDBExtended db = OpenRead();
            return db.Query<RawEntryRow>("SELECT " + RawEntryRowExpression(db) + " AS Value FROM playlist_entry WHERE rowid = ?;", rowId).Single().Value;
        }
        /// <summary>表 9201 の指定 memo の行の rowid を返します。</summary>
        internal long ReadRowIdByMemo(string memo)
        {
            using LR2SongDBExtended db = OpenRead();
            return db.ExecuteScalar<long>("SELECT rowid FROM playlist_entry WHERE playlist_id = 9201 AND memo = ?;", memo);
        }
        /// <summary>表 9201 の指定 memo の行数を返します。</summary>
        internal int CountByMemo(string memo)
        {
            using LR2SongDBExtended db = OpenRead();
            return db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id = 9201 AND memo = ?;", memo);
        }
        private static string RawEntryRowExpression(LR2SongDBExtended db)
            => "rowid || " + string.Join(" || ", db.Query<ColumnRow>("PRAGMA table_info(playlist_entry);")
                .Select(column => "'|' || quote(\"" + column.name + "\")"));
        /// <summary>同じhash・folder・title・生存状態の行を、指定列だけ置き換えて raw 値のまま挿入し、その rowid を返します。</summary>
        internal long InsertKeyVariantEntry(string column, string? value, string memo)
        {
            using LR2SongDBExtended db = databaseScope.OpenSongWrite(dbPath);
            db.Execute("INSERT INTO playlist_entry (playlist_id, md5, sha256, folder, title, lr2_bmsid, is_removed, memo) VALUES (9201, ?, NULL, 'folder', 'title', NULL, 0, ?);",
                LowerMd5, memo);
            long rowId = db.ExecuteScalar<long>("SELECT last_insert_rowid();");
            db.Execute("UPDATE playlist_entry SET " + column + " = ? WHERE rowid = ?;", value, rowId);
            return rowId;
        }
        internal IReadOnlyList<string> Explain(string sql)
        {
            using LR2SongDBExtended db = OpenRead();
            return [.. db.Query<QueryPlanRow>("EXPLAIN QUERY PLAN " + sql).Select(row => row.detail)];
        }
        /// <summary>対象を含む指定行数をraw値のまま一接続・一transactionで準備し、本番保存前に閉じます。</summary>
        internal void SeedCandidateLookupEntries(int count, string hashKind)
        {
            using LR2SongDBExtended db = databaseScope.OpenSongWrite(dbPath);
            db.RunInTransaction(() =>
            {
                db.Execute("DELETE FROM playlist_entry WHERE playlist_id = 9201;");
                for (int index = 0; index < count; index++)
                {
                    bool noHash = hashKind.StartsWith("none-", StringComparison.Ordinal);
                    string? md5 = noHash || hashKind == "sha256" || (hashKind == "none" && index == 0)
                        ? null : index == 0 ? LowerMd5 : index.ToString("x32");
                    string? sha256 = hashKind == "sha256" ? index == 0 ? LowerSha256 : index.ToString("x64") : null;
                    // 対象は index 0。none-folder・none-title の背景は hash を持たず、folder または title だけが対象と違います。
                    string folder = hashKind == "none-folder" && index != 0 ? "folder-" + index : "folder";
                    string? title = hashKind == "none-title" && index != 0 ? "title-" + index : null;
                    db.Execute("INSERT INTO playlist_entry (playlist_id, md5, sha256, folder, title, lr2_bmsid, is_removed, memo) VALUES (9201, ?, ?, ?, ?, NULL, NULL, 'before');",
                        md5, sha256, folder, title);
                }
            });
        }

        internal void InsertRawEntry(string? md5, string? sha256, bool removed = false, string memo = "before", string? title = null)
        {
            using LR2SongDBExtended db = databaseScope.OpenSongWrite(dbPath);
            db.Execute("INSERT INTO playlist_entry (playlist_id, md5, sha256, folder, title, lr2_bmsid, is_removed, memo) VALUES (9201, ?, ?, 'folder', ?, NULL, ?, ?);",
                md5, sha256, title, removed ? 1 : 0, memo);
        }
        /// <summary>両 hash と lr2_bmsid が NULL の行を、指定の folder・title・is_removed の生値のまま表 9201 へ挿入します。</summary>
        internal void InsertBlankHashEntry(string? folder, string? title, int? isRemoved, string memo)
        {
            using LR2SongDBExtended db = databaseScope.OpenSongWrite(dbPath);
            db.Execute("INSERT INTO playlist_entry (playlist_id, md5, sha256, folder, title, lr2_bmsid, is_removed, memo) VALUES (9201, NULL, NULL, ?, ?, NULL, ?, ?);",
                folder, title, isRemoved, memo);
        }
        internal void Execute(params string[] statements) { using LR2SongDBExtended db = databaseScope.OpenSongWrite(dbPath); foreach (string sql in statements) { db.Execute(sql); } }
        public void Dispose()
        {
            foreach (BMSTable table in Tables) { table.ReaderWriterLock.Dispose(); }
            collectionLock.Dispose();
            Directory.Delete(directory, recursive: true); ownedWorkspace.Dispose();
        }
    }

}
