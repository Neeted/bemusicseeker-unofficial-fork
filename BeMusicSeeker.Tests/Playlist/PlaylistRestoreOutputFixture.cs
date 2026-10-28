using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>SQL復元の配置整理だけに使う実SQLite・専用LR2設定・物理出力を所有します。画面は生成しません。</summary>
internal sealed class PlaylistRestoreOutputFixture : IAsyncDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), nameof(PlaylistRestoreOutputFixture), Guid.NewGuid().ToString("N"));
    internal string SongDb { get; }
    internal Settings Settings { get; }
    internal CustomFolderOutputSettingsSnapshot OutputSettings => CustomFolderOutputSettingsSnapshot.CreateCurrent(Settings);
    internal Lr2ConfigOwner Config { get; }
    internal TestBmsLibrary Library { get; }
    internal TestBmsPlaylist Playlist { get; }
    internal string ExternalFile { get; }
    internal string SiblingFile { get; }

    internal PlaylistRestoreOutputFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        SongDb = BmsPlaylistTestSupport.CreateTempSongDbPath(DirectoryPath);
        PlaylistPersistenceRepository.EnsureSchema(SongDb);
        string bms = Path.Combine(DirectoryPath, "BMS");
        string additional = Path.Combine(bms, "Additional");
        Settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
        {
            values.OperationModeLR2DB = true;
            values.EnablePlaylistUrlCompletion = false;
            values.EnableBeatorajaBmtOutput = false;
            values.LR2RootPath = Path.Combine(DirectoryPath, "LR2");
            values.LR2CustomFolderOutputBaseDir = Path.Combine(bms, "CustomFolder");
            values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(DirectoryPath, "RootOutput");
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additional]);
        });
        Directory.CreateDirectory(bms);
        Directory.CreateDirectory(additional);
        Directory.CreateDirectory(Settings.LR2CustomFolderOutputBaseDirRootType);
        Config = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(Settings.LR2RootPath, bms));
        Config.SetPlacement(OutputSettings);
        Library = new TestBmsLibrary(SongDb, Config, null, null,
            () => BmsLibraryOptionsSnapshot.CreateCurrent(Settings), settings: Settings);
        var synchronization = new TestLr2PlaylistFolderSynchronizationPort(SongDb)
        {
            PhysicalSurfaceFactory = () => CustomFolderOutputPhysicalSurface.FromEntries(
                Directory.GetFiles(DirectoryPath, "*.lr2folder", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .Select(file => new RootFileEnumerationEntry(file.FullName, file.LastWriteTimeUtc, file.Length)), true)
        };
        Playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(Library), SongDb, () => OutputSettings, Config,
            () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
            settings: Settings, lr2PlaylistFolderSynchronization: synchronization)
        { StartupBackgroundTaskScheduler = (_, _, _, _) => false };
        ExternalFile = Path.Combine(bms, "external.lr2folder");
        SiblingFile = Path.Combine(Settings.LR2CustomFolderOutputBaseDir, "Unmanaged", "external.lr2folder");
        Directory.CreateDirectory(Path.GetDirectoryName(SiblingFile) ?? throw new InvalidOperationException());
        foreach (string path in new[] { ExternalFile, SiblingFile })
        {
            File.WriteAllText(path, "#TITLE external\r\n#COMMAND 1\r\n");
            using var db = new LR2SongDBExtended(SongDb);
            db.InsertOrReplace(new LR2SongDB.folder { path = path, title = "external" });
        }
        using var setup = new LR2SongDBExtended(SongDb);
        setup.InsertOrReplace(new LR2SongDB.song { path = "unrelated.bms", hash = new string('a', 32), title = "kept song" });
        setup.Execute("CREATE TABLE user_data (value TEXT);");
        setup.Execute("INSERT INTO user_data VALUES ('kept user value');");
    }

    /// <summary>出力位置と本文を判別できる一フォルダの表を作ります。全曲有効時は二ファイルです。</summary>
    internal static BMSTable Table(int id, string output, string folder = "Saved", bool root = false, string? baseName = null,
        LR2SongDBExtended.playlist.CustomFolderType? ignored = null) => new()
        {
            playlist_id = id,
            name = "Table " + id,
            symbol = "T",
            Output_dir = output,
            is_root_folder = root,
            custom_folder_output_base_name = baseName,
            ignore_folder_output = ignored ?? (LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder),
            entries = [new BMSTableEntry { playlist_id = id, md5 = new string('a', 32), title = "Song " + id, folder = folder }],
            Folder_order = [folder]
        };

    /// <summary>復元前後の独立した保存状態を準備し、本番の生成・同期入口で物理出力も成立させます。</summary>
    internal void PersistAndOutput(params BMSTable[] tables)
    {
        using (var db = new LR2SongDBExtended(SongDb))
        {
            db.Execute("DELETE FROM playlist_entry;");
            db.Execute("DELETE FROM playlist;");
        }
        Playlist.BMSTables = new ObservableCollection<BMSTable>(tables);
        Playlist.CommitBMSTablesWithEntriesToDB(tables);
        if (Settings.OperationModeLR2DB) { Playlist.ReOutputCustomFoldersAndCommitHeadersToDB(tables, "restore_fixture"); }
    }

    internal string OutputDirectory(BMSTable table) => Path.Combine(table.is_root_folder
        ? Settings.LR2CustomFolderOutputBaseDirRootType
        : table.custom_folder_output_base_name == "Additional"
            ? Path.Combine(DirectoryPath, "BMS", "Additional") : Settings.LR2CustomFolderOutputBaseDir, table.Output_dir);

    /// <summary>復元Task直後の新ファイル・本文・行集合と、管理範囲外の物理物・song・利用者表を観測します。</summary>
    internal void AssertCurrentOutput(params BMSTable[] expected)
    {
        string[] files = expected.SelectMany(table => Directory.Exists(OutputDirectory(table))
            ? Directory.GetFiles(OutputDirectory(table), "*.lr2folder", SearchOption.AllDirectories) : []).ToArray();
        int count = expected.Sum(table => table.ignore_folder_output == LR2SongDBExtended.playlist.CustomFolderType.AllFolders ? 0
            : LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(table.ignore_folder_output,
                LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder) ? 2 : 1);
        Assert.AreEqual(count, files.Length);
        foreach (BMSTable table in expected.Where(table => table.ignore_folder_output != LR2SongDBExtended.playlist.CustomFolderType.AllFolders))
        {
            Assert.IsTrue(Directory.GetFiles(OutputDirectory(table), "*.lr2folder").Any(path =>
                BmsPlaylistTestSupport.ReadShiftJisText(path).Contains("#TITLE " + table.entries.Single().folder, StringComparison.Ordinal)));
        }
        using var db = new LR2SongDBExtended(SongDb);
        foreach (string file in files)
        {
            string command = BmsPlaylistTestSupport.ReadShiftJisText(file).Split(["\r\n", "\n"], StringSplitOptions.None)
                .Single(line => line.StartsWith("#COMMAND ", StringComparison.Ordinal))["#COMMAND ".Length..];
            CollectionAssert.AreEqual(new[] { new string('a', 32) },
                db.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + command).Select(song => song.hash).ToArray(), file);
        }
        string[] fileRows = db.Table<LR2SongDB.folder>().AsEnumerable().Where(row => row.path.EndsWith(".lr2folder", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.path).ToArray();
        CollectionAssert.AreEquivalent(new[] { ExternalFile, SiblingFile }.Concat(files).ToArray(), fileRows);
        foreach (string path in new[] { ExternalFile, SiblingFile })
        { Assert.AreEqual("#TITLE external\r\n#COMMAND 1\r\n", File.ReadAllText(path)); }
        Assert.AreEqual("kept song", db.ExecuteScalar<string>("SELECT title FROM song WHERE path = 'unrelated.bms';"));
        Assert.AreEqual("kept user value", db.ExecuteScalar<string>("SELECT value FROM user_data;"));
    }

    /// <summary>復元する保存状態にあったディレクトリ行・ROOT親・ファイル行の配置を捕捉します。日時と生成順は固定しません。</summary>
    internal string[] CaptureFolderLayout(params string[] outputDirectories)
    {
        using var db = new LR2SongDBExtended(SongDb);
        return db.Table<LR2SongDB.folder>().AsEnumerable().Where(row => outputDirectories.Any(directory =>
                string.Equals(row.path.TrimEnd(Path.DirectorySeparatorChar), directory, StringComparison.OrdinalIgnoreCase)
                || row.path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Select(row => row.path + "\t" + row.type + "\t" + row.parent).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        Playlist.RequestShutdown("restore_fixture_cleanup");
        await Playlist.WaitForPlaylistMutationIdleAsync();
        Library.RequestShutdown("restore_fixture_cleanup");
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
