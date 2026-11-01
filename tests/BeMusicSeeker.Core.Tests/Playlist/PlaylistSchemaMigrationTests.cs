using System.Collections.ObjectModel;
using System.Globalization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;


/// <summary>旧プレイリストDBの移行とバックアップ復元の保存互換・原子性を検証する。</summary>
/// <remarks>専用SQLiteを新しい接続で読み、値だけでなく保存型・索引・他テーブルの不変を確認する。構造検査は既存DBの読取りと検索互換を担う。</remarks>
[TestClass]
public sealed class PlaylistSchemaMigrationTests
{
    private TestWorkspace? ownedCaseWorkspace;

    /// <summary>返却されたfixtureの終端まで、ケースが専有媒体の親領域を保持します。</summary>
    private TestWorkspace CaseWorkspace => ownedCaseWorkspace ?? throw new InvalidOperationException("ケースの媒体は初期化後に使用します。");

    /// <summary>ケース専有領域を開始し、既存の相対pathの存在条件を維持します。</summary>
    [TestInitialize]
    public void InitializeCaseWorkspace() => ownedCaseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース本文のfinallyとfixtureの終端後に外側の媒体を回収します。</summary>
    [TestCleanup]
    public void CleanupCaseWorkspace() => ownedCaseWorkspace?.Dispose();

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>異なるカルチャーでdump・復元し、数値風TEXT・空白・引用符・日本語・NULLとINTEGER/REALを型ごと保持する。制御文字と途中SQL失敗時の旧3表・対象外保全も確認し、文字列損失・型変換・部分復元を検出する。</summary>
    [TestMethod]
    // fr-FRからの復元はNUL・垂直TAB・CRLFも含む。en-USからは同じ保存型を別カルチャーで確認する。
    [DataRow("fr-FR", "ja-JP")]
    [DataRow("en-US", "fr-FR")]
    public void PlaylistDump_RoundTripPreservesValuesAndStorageTypesAcrossCultures(string dumpCulture, string restoreCulture)
    {
        string path = CreateEmptySongDbPath(databaseScope);
        CultureInfo previous = CultureInfo.CurrentCulture;
        bool hasNulMemo = dumpCulture == "fr-FR";
        // 数値風TEXT・空文字・空白・多言語・SQL引用符・NULLを独立の期待値として往復する。
        string?[] values = ["1e3", "1.00", "1,000", "001", "", "   ", "日本語", "O'Brien", null];
        const long ticks = 638857728000000000;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, path);
            using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(path))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.Execute("CREATE TABLE user_backup_data (value TEXT);");
                    setup.Execute("INSERT INTO user_backup_data VALUES ('non-target');");
                    setup.Insert(new LR2SongDB.song { hash = new string('a', 32), title = "song remains" });
                    for (int index = 0; index < values.Length; index++)
                    {
                        setup.Insert(new LR2SongDBExtended.playlist
                        {
                            playlist_id = index + 1,
                            name = values[index],
                            last_update = new DateTime(ticks)
                        });
                        setup.Insert(new LR2SongDBExtended.playlist_entry
                        {
                            playlist_id = index + 1,
                            md5 = index.ToString("x32", CultureInfo.InvariantCulture),
                            title = values[index],
                            folder = "folder",
                            level = index == values.Length - 1 ? null : index + .125
                        });
                        setup.Insert(new LR2SongDBExtended.playlist_course
                        {
                            playlist_id = index + 1,
                            course_order = index,
                            course_json = values[index]
                        });
                    }
                    // titleのモデルsetterはNULLを空文字に変えるため、SQLiteに保存済みのNULL行を直接準備する。
                    setup.Execute("UPDATE playlist_entry SET title=NULL WHERE playlist_id=?;", values.Length);
                    if (hasNulMemo)
                    {
                        setup.Execute("UPDATE playlist_entry SET memo=? WHERE playlist_id=1;", "before\0after");
                        setup.Execute("UPDATE playlist_entry SET memo='' WHERE playlist_id=2;");
                        setup.Execute("UPDATE playlist_entry SET memo=NULL WHERE playlist_id=3;");
                        setup.Execute("UPDATE playlist_entry SET comment=? WHERE playlist_id=4;", "before\v\r\nafter");
                        setup.Execute("UPDATE playlist_entry SET comment='' WHERE playlist_id=5;");
                        setup.Execute("UPDATE playlist_entry SET comment=NULL WHERE playlist_id=6;");
                    }
                });
            }
            var repository = new PlaylistPersistenceRepository(databaseScope, path);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(dumpCulture);
            string dump = repository.GetPlaylistDump();
            if (hasNulMemo)
            {
                using LR2SongDBExtended mutate = databaseScope.OpenSongWrite(path);
                mutate.Execute("UPDATE playlist_entry SET memo='replacement marker' WHERE playlist_id=1;");
                mutate.Execute("UPDATE playlist_entry SET comment='replacement marker' WHERE playlist_id=4;");
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(restoreCulture);
            repository.LoadPlaylistDump(dump);
            VerifyDumpRows(databaseScope, path, values, ticks, hasNulMemo);

            // 有効な部分INSERTを通過した後でSQL実行を失敗させ、旧三表の全行を保全する。
            string separator = "\v" + Environment.NewLine;
            string partial = string.Join(separator,
                [dump.Split(separator)[0], "INSERT INTO missing_backup_table VALUES (1);", ""]);
            SQLite.SQLiteException failure = Assert.ThrowsExactly<SQLite.SQLiteException>(() => repository.LoadPlaylistDump(partial));
            StringAssert.Contains(failure.Message, "missing_backup_table");
            VerifyDumpRows(databaseScope, path, values, ticks, hasNulMemo);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            DeleteTempSongDbDirectory(path);
        }
    }

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private static void VerifyDumpRows(ApplicationDatabaseScope databaseScope, string path, IReadOnlyList<string?> values, long ticks, bool hasNulMemo)
    {
        using LR2SongDBExtended read = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), path).OpenSongDbReadOnly();
        DumpValueRow[] headers = read.Query<DumpValueRow>("SELECT playlist_id AS Id, name AS Value, typeof(name) AS StorageType, last_update AS IntegerValue, typeof(last_update) AS IntegerType FROM playlist ORDER BY playlist_id;").ToArray();
        DumpValueRow[] entries = read.Query<DumpValueRow>("SELECT playlist_id AS Id, title AS Value, typeof(title) AS StorageType, level AS RealValue, typeof(level) AS RealType, folder AS Folder FROM playlist_entry ORDER BY playlist_id;").ToArray();
        DumpValueRow[] courses = read.Query<DumpValueRow>("SELECT playlist_id AS Id, course_json AS Value, typeof(course_json) AS StorageType, course_order AS IntegerValue, typeof(course_order) AS IntegerType FROM playlist_course ORDER BY playlist_id;").ToArray();
        foreach (DumpValueRow[] rows in new[] { headers, entries, courses })
        {
            Assert.AreEqual(values.Count, rows.Length);
            for (int index = 0; index < values.Count; index++)
            {
                Assert.AreEqual(index + 1, rows[index].Id);
                Assert.AreEqual(values[index], rows[index].Value);
                Assert.AreEqual(values[index] == null ? "null" : "text", rows[index].StorageType);
            }
        }
        for (int index = 0; index < values.Count; index++)
        {
            Assert.AreEqual(ticks, headers[index].IntegerValue);
            Assert.AreEqual("integer", headers[index].IntegerType);
            Assert.AreEqual((long)index, courses[index].IntegerValue);
            Assert.AreEqual("integer", courses[index].IntegerType);
            Assert.AreEqual("folder", entries[index].Folder);
            Assert.AreEqual(index == values.Count - 1 ? null : (double?)(index + .125), entries[index].RealValue);
            Assert.AreEqual(index == values.Count - 1 ? "null" : "real", entries[index].RealType);
        }
        Assert.AreEqual("song remains", read.ExecuteScalar<string>("SELECT title FROM song WHERE hash=?;", new string('a', 32)));
        Assert.AreEqual("non-target", read.ExecuteScalar<string>("SELECT value FROM user_backup_data;"));
        if (hasNulMemo)
        {
            Assert.AreEqual("text", read.ExecuteScalar<string>("SELECT typeof(memo) FROM playlist_entry WHERE playlist_id=1;"));
            Assert.AreEqual("6265666F7265006166746572", read.ExecuteScalar<string>("SELECT hex(CAST(memo AS BLOB)) FROM playlist_entry WHERE playlist_id=1;"));
            Assert.AreEqual("text", read.ExecuteScalar<string>("SELECT typeof(memo) FROM playlist_entry WHERE playlist_id=2;"));
            Assert.AreEqual(string.Empty, read.ExecuteScalar<string>("SELECT hex(CAST(memo AS BLOB)) FROM playlist_entry WHERE playlist_id=2;"));
            Assert.AreEqual("null", read.ExecuteScalar<string>("SELECT typeof(memo) FROM playlist_entry WHERE playlist_id=3;"));
            Assert.IsNull(read.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=3;"));
            Assert.AreEqual("text", read.ExecuteScalar<string>("SELECT typeof(comment) FROM playlist_entry WHERE playlist_id=4;"));
            Assert.AreEqual("6265666F72650B0D0A6166746572", read.ExecuteScalar<string>("SELECT hex(CAST(comment AS BLOB)) FROM playlist_entry WHERE playlist_id=4;"));
            Assert.AreEqual("text", read.ExecuteScalar<string>("SELECT typeof(comment) FROM playlist_entry WHERE playlist_id=5;"));
            Assert.AreEqual(string.Empty, read.ExecuteScalar<string>("SELECT hex(CAST(comment AS BLOB)) FROM playlist_entry WHERE playlist_id=5;"));
            Assert.AreEqual("null", read.ExecuteScalar<string>("SELECT typeof(comment) FROM playlist_entry WHERE playlist_id=6;"));
            Assert.IsNull(read.ExecuteScalar<string>("SELECT comment FROM playlist_entry WHERE playlist_id=6;"));
        }
    }

    private sealed class DumpValueRow
    {
        public int Id { get; set; }
        public string? Value { get; set; }
        public string StorageType { get; set; } = string.Empty;
        public long IntegerValue { get; set; }
        public string? IntegerType { get; set; }
        public double? RealValue { get; set; }
        public string? RealType { get; set; }
        public string? Folder { get; set; }
    }

    /// <summary>旧SHAなしschemaのDBでプレイリストを構築しても列・索引を変更しない。生成だけで既存DBを書き換える境界違反を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSPlaylist_Constructor_DoesNotMigrateLegacyPlaylistEntrySchema()
    {
        string tempDbPath = CreateEmptySongDbPath(databaseScope);
        try
        {
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist>();
                db.DropTable<LR2SongDBExtended.playlist_entry>();
                db.CreateTable<LR2SongDBExtended.playlist>();
                db.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                db.Execute("CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
            }

            _ = new TestBmsPlaylist(databaseScope, tempDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, tempDbPath), uiScheduler: new TestUiScheduler(() => null));

            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            Assert.IsFalse(tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            string uniqueIndexSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            Assert.IsFalse(uniqueIndexSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>明示schema整備で旧エントリ表へSHA列と検索・一意索引を追加する。SHA対応の移行漏れや旧一意条件の残存を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSPlaylist_EnsureSchema_MigratesLegacyPlaylistEntrySchema()
    {
        string tempDbPath = CreateEmptySongDbPath(databaseScope);
        try
        {
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist>();
                db.DropTable<LR2SongDBExtended.playlist_entry>();
                db.CreateTable<LR2SongDBExtended.playlist>();
                db.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NOT NULL, md5 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                db.Execute("CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry(md5, playlist_id, folder, lr2_bmsid, title, is_removed);");
            }

            PlaylistPersistenceRepository.EnsureSchema(databaseScope, tempDbPath);

            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            StringAssert.Contains(tableSql, "sha256");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            string uniqueIndexSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            StringAssert.Contains(uniqueIndexSql, "sha256");
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>schema整備は出力設定・外部テーブルmetadataの保存列とcourse表の検索・一意索引を用意する。保存項目の欠落とcourse所属の保存不能を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSPlaylist_EnsureSchema_AddsPlaylistMetadataAndCourseStorage()
    {
        string tempDbPath = CreateEmptySongDbPath(databaseScope);
        try
        {
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(tempDbPath))
            {
                db.DropTable<LR2SongDBExtended.playlist_course>();
            }

            PlaylistPersistenceRepository.EnsureSchema(databaseScope, tempDbPath);

            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(tempDbPath);
            string playlistSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist';");
            StringAssert.Contains(playlistSql, "tag");
            StringAssert.Contains(playlistSql, "header_sha256");
            StringAssert.Contains(playlistSql, "data_sha256");
            StringAssert.Contains(playlistSql, "bmt_sort");
            StringAssert.Contains(playlistSql, "is_bmt_output");
            StringAssert.Contains(playlistSql, "custom_folder_output_base_name");
            string courseSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_course';");
            StringAssert.Contains(courseSql, "course_json");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_course_idx_id';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_course_idx_uniq';"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>BMT出力列を持たない旧dumpを復元し、従来のsort・出力有効値と未設定basenameを補完する。旧バックアップの拒否や互換値の変更を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadPlaylistDump_RestoresLegacyDumpWithoutBmtColumns()
    {
        string tempDbPath = CreateEmptySongDbPath(databaseScope);
        try
        {
            var playlist = new TestBmsPlaylist(databaseScope, tempDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, tempDbPath), uiScheduler: new TestUiScheduler(() => null));
            string separator = "\v" + Environment.NewLine;
            string legacyPlaylistDump =
                "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) "
                + "VALUES (1, 'Legacy', 'lg', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);";
            string dump = string.Join(separator, [legacyPlaylistDump, string.Empty, string.Empty]);

            playlist.LoadPlaylistDump(dump);

            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(tempDbPath);
            BMSTable restored = verify.Table<BMSTable>().Single();
            Assert.AreEqual(1, restored.bmt_sort);
            Assert.AreEqual(true, restored.is_bmt_output);
            Assert.IsNull(restored.custom_folder_output_base_name);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>空エントリのdumpを復元してもSHA列と検索・一意索引を保持する。復元時の表再作成による検索互換の消失を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadPlaylistDump_RestoresSha256Indexes()
    {
        string tempDbPath = CreateEmptySongDbPath(databaseScope);
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, tempDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, tempDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, tempDbPath), uiScheduler: new TestUiScheduler(() => null));
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(tempDbPath))
            {
                db.Execute("DELETE FROM playlist_entry;");
                db.Execute("DELETE FROM playlist;");
            }
            string dump = playlist.GetPlaylistDump();

            playlist.LoadPlaylistDump(dump);

            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(tempDbPath);
            string tableSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'playlist_entry';");
            StringAssert.Contains(tableSql, "sha256");
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_sha256';"));
            string uniqueIndexSql = verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
            StringAssert.Contains(uniqueIndexSql, "sha256");
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>途中course INSERTをトリガーで失敗させ、liveの旧テーブルとDBの旧所属を維持して新世代の部分行を残さない。SQLステップ失敗時の部分公開・部分保存を検出する。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadPlaylistDump_StepFailureAfterPartialRowsPreservesPriorGeneration()
    {
        string tempDbPath = CreateEmptySongDbPath(databaseScope);
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, tempDbPath);
            var previousTable = new BMSTable
            {
                playlist_id = 71,
                name = "Prior generation",
                symbol = "prior"
            };
            using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(tempDbPath))
            {
                setup.InsertOrReplace(previousTable, typeof(LR2SongDBExtended.playlist));
            }

            var playlist = new TestBmsPlaylist(databaseScope, tempDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, tempDbPath), uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([previousTable])
            };
            const string separator = "\v" + "\r\n";
            string partialPlaylistInsert =
                "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, is_external_sync, is_root_folder) "
                + "VALUES (72, 'Partial generation', 'partial', '', 0, 1, 0, 0, 0);";
            string abortTrigger =
                "CREATE TRIGGER restore_abort BEFORE INSERT ON playlist_course "
                + "BEGIN SELECT RAISE(ABORT, 'deterministic restore step failure'); END;";
            string failingCourseInsert =
                "INSERT INTO playlist_course (playlist_id, course_order, course_json) VALUES (72, 0, '{}');";
            string partialDump = string.Join(separator, [partialPlaylistInsert, abortTrigger, failingCourseInsert]);

            Exception? failure = null;
            try
            {
                playlist.LoadPlaylistDump(partialDump);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            Assert.AreEqual(1, playlist.BMSTables.Count);
            BMSTable liveTable = playlist.BMSTables.Single();
            Assert.AreEqual(71, liveTable.playlist_id);
            Assert.AreEqual("Prior generation", liveTable.name);
            Assert.AreEqual("prior", liveTable.symbol);
            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(tempDbPath);
            Assert.AreEqual(
                "Prior generation",
                verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = 71;"));
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = 72;"));
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_course WHERE playlist_id = 72;"));
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private string CreateEmptySongDbPath(ApplicationDatabaseScope databaseScope)
    {
        string tempDirectory = Path.Combine(CaseWorkspace.RootDirectory, "PlaylistSchemaMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(tempDbPath))
        {
        }
        return tempDbPath;
    }

    private static void DeleteTempSongDbDirectory(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath))
        {
            return;
        }
        string directoryPath = Path.GetDirectoryName(songDbPath)!;
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

}
