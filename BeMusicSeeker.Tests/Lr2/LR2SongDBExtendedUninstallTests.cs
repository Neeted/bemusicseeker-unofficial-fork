using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LR2SongDBExtendedUninstallTests
{
    /// <summary>実起動・アプリスキーマ・IR保存・フォルダ索引の生成差分を実撤去し、LR2と未知の利用者スキーマ、行、sequence、譜面ファイルを新規接続で保全確認します。削除リストから期待対象を作りません。</summary>
    /// <remarks>実起動用スキーマと撤去storeを使い、独立した新規読取り接続で利用者DB・譜面の保全を観測します。</remarks>
    [TestMethod]
    public void Uninstall_RemovesGeneratedApplicationSchemaAndPreservesUserData()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(LR2SongDBExtendedUninstallTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "song.db");
        string chartPath = Path.Combine(directory, "chart.bms");
        const string chartText = "#TITLE retained chart\n";
        File.WriteAllText(chartPath, chartText);
        try
        {
            using (var setup = new LR2SongDBExtended(path))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.Execute("CREATE INDEX hashidx ON song(hash);");
                    setup.Execute("CREATE INDEX parentidx ON song(parent);");
                    setup.Insert(new LR2SongDB.song { hash = new string('a', 32), path = chartPath, title = "LR2 original" });
                    setup.Insert(new LR2SongDB.folder { path = directory, title = "LR2 folder" });
                    setup.Execute("CREATE TABLE user_data (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT);");
                    setup.Execute("CREATE INDEX user_value_idx ON user_data(value);");
                    setup.Execute("CREATE TABLE user_events (value TEXT);");
                    setup.Execute("CREATE TRIGGER user_song_update AFTER UPDATE OF title ON song BEGIN INSERT INTO user_events VALUES (NEW.title); END;");
                    setup.Execute("INSERT INTO user_data(value) VALUES ('利用者の値');");
                });
            }
            var gateway = new BmsLibraryDbGateway(path);
            string[] baseline;
            using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
            {
                baseline = ReadSchema(read);
            }
            gateway.EnsureLibraryStartupSchema();
            gateway.EnsureAppOwnedSchema();
            gateway.UpsertIrScoreRefreshMetadata(1234, "digest");
            using (var setup = new LR2SongDBExtended(path))
            {
                BmsLibraryDbGateway.EnsureFolderLookupIndexes(setup);
                setup.Insert(new LR2SongDBExtended.playlist { name = "generated sequence" });
                setup.Execute("INSERT INTO playlist_custom_folder_output_status VALUES (1, 'output', 0, 0, 0, 0, 1, 0, 'header', 'data', 1, 'signature');");
            }
            string[] generated;
            using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
            {
                generated = ReadSchema(read).Except(baseline, StringComparer.Ordinal).ToArray();
                Assert.IsTrue(generated.Length > 0);
                Assert.IsTrue(generated.Any(row => row.StartsWith("table:playlist_custom_folder_output_status:", StringComparison.Ordinal)));
                Assert.IsTrue(generated.Any(row => row.StartsWith("index:folder_idx_path_nocase:", StringComparison.Ordinal)));
                Assert.AreEqual(1L, read.ExecuteScalar<long>("SELECT COUNT(*) FROM playlist_custom_folder_output_status;"));
                Assert.AreEqual(1L, read.ExecuteScalar<long>("SELECT COUNT(*) FROM ir_score_refresh_metadata WHERE lr2id=1234;"));
                Assert.AreEqual(1L, read.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_sequence WHERE name='playlist';"));
            }

            new Lr2ApplicationDataUninstallStore().Uninstall(path);

            using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
            {
                string[] remaining = ReadSchema(read);
                Assert.IsFalse(generated.Intersect(remaining, StringComparer.Ordinal).Any(), "撤去後の生成物: " + string.Join(", ", generated.Intersect(remaining, StringComparer.Ordinal)));
                CollectionAssert.AreEquivalent(baseline, remaining);
                Assert.AreEqual("LR2 original", read.ExecuteScalar<string>("SELECT title FROM song WHERE hash=?;", new string('a', 32)));
                Assert.AreEqual(chartPath, read.ExecuteScalar<string>("SELECT path FROM song;"));
                Assert.AreEqual("LR2 folder", read.ExecuteScalar<string>("SELECT title FROM folder WHERE path=?;", directory));
                Assert.AreEqual("利用者の値", read.ExecuteScalar<string>("SELECT value FROM user_data WHERE id=1;"));
                Assert.AreEqual(0L, read.ExecuteScalar<long>("SELECT COUNT(*) FROM user_events;"));
                Assert.AreEqual(1L, read.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_sequence;"));
                Assert.AreEqual(1L, read.ExecuteScalar<long>("SELECT seq FROM sqlite_sequence WHERE name='user_data';"));
            }
            Assert.AreEqual(chartText, File.ReadAllText(chartPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] ReadSchema(SQLiteConnection db)
        => db.Query<SchemaRow>("SELECT type AS Type, name AS Name, tbl_name AS TableName, sql AS Sql FROM sqlite_schema WHERE name <> 'sqlite_sequence';")
            .Select(row => row.Type + ":" + row.Name + ":" + row.TableName + ":" + row.Sql).OrderBy(row => row, StringComparer.Ordinal).ToArray();

    private sealed class SchemaRow
    {
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string? Sql { get; set; }
    }
}
