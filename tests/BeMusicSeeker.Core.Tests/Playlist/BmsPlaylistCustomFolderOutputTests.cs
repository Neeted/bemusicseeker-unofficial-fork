using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Util.Extensions;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>実プレイリスト保存に伴うLR2ファイル・DB同期と、出力失敗後の明示保存継続を確認します。</summary>
/// <remarks>SQL本文、実ファイルの整理、SQLite確定は下位の自己転記では代替できません。画面なしの保存serviceを主に使い、共有Dispatcherを設定する代表だけ非並列で接続を確認します。</remarks>
[TestClass]
public sealed class BmsPlaylistCustomFolderOutputTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>生成した全所属/各folderの通常・RANDOM COMMANDでscoreなしとclear=0だけをNO PLAYへ入れ、出力単位の範囲と明示再出力を保ちます。</summary>
    [TestMethod]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.File)]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.Folder)]
    public void CustomFolderOutput_NoPlayIncludesStoredZero(LR2SongDBExtended.playlist.EntryUnitType unit)
    {
        using var owned = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.OperationModeLR2DB = true;
        testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(owned.RootDirectory, "output");
        string dbPath = CreateTempSongDbPath(databaseScope, owned.RootDirectory);
        PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
        var table = new BMSTable
        {
            playlist_id = 7399,
            name = "NoPlay",
            Output_dir = "NoPlay",
            entry_type = unit,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.RandomFolder,
            Folder_order = ["Group", "Other"],
            entries = Enumerable.Range(1, 5).Select(number => new BMSTableEntry
            {
                md5 = number.ToString("x32"),
                title = "Song " + number,
                folder = number == 4 ? "Other" : "Group",
                is_removed = number == 5
            }).ToArray()
        };
        using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(dbPath))
        {
            seed.Execute("CREATE TABLE score(hash TEXT PRIMARY KEY, clear INTEGER, rank INTEGER, op_history INTEGER, minbp INTEGER);");
            foreach (int number in Enumerable.Range(1, 7))
            {
                seed.Execute("INSERT INTO song(hash,title,folder,path) VALUES (?,?,?,?);", number.ToString("x32"), "Song " + number,
                    number == 6 ? "Package1" : "Package" + number, number + ".bms");
                if (number != 1) { seed.Execute("INSERT INTO score(hash,clear,minbp) VALUES (?,?,0);", number.ToString("x32"), number == 3 ? 1 : 0); }
            }
        }
        var playlist = new TestBmsPlaylist(databaseScope, dbPath,
            CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, dbPath, CustomFolderOutputPhysicalSurface.Empty),
            uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
        { BMSTables = new ObservableCollection<BMSTable>([table]) };
        try
        {
            foreach (bool reoutput in new[] { false, true })
            {
                if (reoutput) { playlist.ReOutputCustomFolder(table); } else { playlist.ReOutputCustomFolderAndCommitToDB(table); }
                string directory = Path.Combine(testSettings.LR2CustomFolderOutputBaseDir, table.Output_dir, "CLEAR FOLDER", "0 NO PLAY");
                string[] files = Directory.GetFiles(directory, "*.lr2folder");
                Assert.AreEqual(6, files.Length);
                using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(dbPath);
                foreach (string file in files)
                {
                    string text = ReadShiftJisText(file);
                    string title = text.Split('\n').Single(line => line.StartsWith("#TITLE ", StringComparison.Ordinal));
                    int[] numbers = title.Contains("Other", StringComparison.Ordinal) ? [4]
                        : title.Contains("ALL", StringComparison.Ordinal) ? unit == LR2SongDBExtended.playlist.EntryUnitType.Folder ? [1, 2, 4, 6] : [1, 2, 4]
                        : unit == LR2SongDBExtended.playlist.EntryUnitType.Folder ? [1, 2, 6] : [1, 2];
                    string command = ReadCustomFolderCommand(text);
                    CollectionAssert.AreEquivalent(numbers.Select(number => number.ToString("x32")).ToArray(),
                        verify.Query<LR2SongDB.song>("SELECT song.* FROM song LEFT JOIN score ON song.hash=score.hash WHERE " + command).Select(song => song.hash).ToArray());
                }
            }
        }
        finally { playlist.RequestShutdown("no-play-command"); }
    }

    /// <summary>負小数のfloor・負整数・0・正値・未定義を実COMMANDと保存DBで選曲し、通常/RANDOMの集合とASCII名を地域によらず保ちます。明示再出力は旧不良本文も修復します。</summary>
    [TestMethod]
    [DataRow("sv-SE")]
    [DataRow("ar-SA")]
    public void ReOutputCustomFolderAndCommitToDB_LevelBucketsUseInvariantSqlAndRepairOldOutput(string cultureName)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            string outputBase = Path.Combine(directory, "Output");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            string dbPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
            double?[] levels = [-1.25, -2, -1, 0, 1.25, null];
            var table = new BMSTable
            {
                playlist_id = 7319,
                name = "Invariant levels",
                Output_dir = "Levels",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.RandomFolder,
                entries = [.. levels.Select((level, i) => new BMSTableEntry
                {
                    md5 = (i + 1).ToString("x32"), title = "row-" + i, level = level, folder = ""
                })]
            };
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(dbPath))
            {
                seed.RunInTransaction(() =>
                {
                    foreach (BMSTableEntry entry in table.entries)
                    { seed.Insert(new LR2SongDB.song { hash = entry.md5, path = entry.title + ".bms", title = entry.title }); }
                });
            }
            var playlist = new TestBmsPlaylist(databaseScope, dbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, dbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>([table]) };
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            string[] paths = Directory.GetFiles(Path.Combine(outputBase, "Levels"), "*.lr2folder", SearchOption.AllDirectories);
            // 旧地域の負号で壊れた出力は、同じ表を明示保存したときだけ現在定義へ置き換えます。
            File.WriteAllText(paths[0], "#TITLE LEVEL -2\r\n#COMMAND broken_regional_minus\r\n", Encoding.GetEncoding("shift_jis"));
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            (string Title, int[] Rows)[] expected = [("LEVEL -2", [1, 2]), ("LEVEL -1", [3]), ("LEVEL 0", [4]), ("LEVEL 1", [5]), ("LEVEL ???", [6])];
            using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), dbPath).OpenSongDbReadOnly();
            LR2SongDB.folder[] saved = db.Table<LR2SongDB.folder>().Where(f => f.type == 2).ToArray();
            Assert.AreEqual(10, saved.Length);
            foreach ((string title, int[] rows) in expected)
            {
                string[] hashes = rows.Select(n => n.ToString("x32")).ToArray();
                foreach (string name in new[] { title, title + " RANDOM" })
                {
                    LR2SongDB.folder folder = saved.Single(f => f.title == name);
                    string text = ReadShiftJisText(folder.path);
                    StringAssert.Contains(text, "#TITLE " + name);
                    string command = ReadCustomFolderCommand(text);
                    Assert.AreEqual(folder.command, command);
                    CollectionAssert.AreEquivalent(hashes,
                        db.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + command).Select(s => s.hash).ToArray());
                    CollectionAssert.AreEquivalent(hashes,
                        db.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + folder.command).Select(s => s.hash).ToArray());
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// 通常・ROOT・追加通常の非空宛先へプロパティ保存で移行し、実列挙の更新時刻がある旧本文も現在の表定義へ置換します。
    /// 実本文と開き直したDBの選曲を照合し、DBだけが新しくなる退行と管理範囲外の削除を検出します。
    /// </summary>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("root")]
    [DataRow("additional")]
    public async Task PlaylistPropertyDialog_MigrationReplacesExistingDestinationFromCurrentDefinition(string role)
    {
        await using var fixture = new PlaylistRestoreOutputFixture();
        BMSTable table = PlaylistRestoreOutputFixture.Table(901, "New", "Current folder",
            root: role == "root", baseName: role == "additional" ? "Additional" : null);
        fixture.PersistAndOutput(table);
        string before = fixture.OutputDirectory(table);
        string outputBase = Path.GetDirectoryName(before) ?? throw new AssertFailedException("Missing output base.");
        string destination = Path.Combine(outputBase, "Old");
        string destinationFile = Path.Combine(destination, Path.GetFileName(Directory.GetFiles(before, "*.lr2folder").Single()));
        string obsolete = Path.Combine(destination, "Nested", "unused.lr2folder");
        string[] external = [Path.Combine(outputBase, "manual.lr2folder"), Path.Combine(outputBase, "OldSibling", "manual.lr2folder")];
        const string OldText = "#TITLE Legacy folder\r\n#CATEGORY Legacy table\r\n#COMMAND song.hash = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'\r\n";
        Directory.CreateDirectory(Path.GetDirectoryName(obsolete) ?? throw new AssertFailedException("Missing nested directory."));
        File.WriteAllText(destinationFile, OldText, Encoding.GetEncoding("shift_jis"));
        File.WriteAllText(obsolete, OldText, Encoding.GetEncoding("shift_jis"));
        foreach (string path in external)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new AssertFailedException("Missing external directory."));
            File.WriteAllText(path, OldText, Encoding.GetEncoding("shift_jis"));
        }
        // fixtureの探索は実ファイルとmtimeをDiscoveryComplete=trueで捕捉し、空の探索面への本文再検査では代替しません。
        Assert.IsTrue(new FileInfo(destinationFile).Exists);
        using (LR2SongDBExtended setup = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb))
        { setup.InsertOrReplace(new LR2SongDB.song { path = "legacy.bms", hash = new string('b', 32) }); }
        using PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(
            fixture.Playlist, fixture.Library, () => fixture.OutputSettings, () => fixture.Config);
        using PlaylistPropertyDialogViewModel dialog = await workspace.OpenPropertyDialogAsync(table)
            ?? throw new AssertFailedException("Missing property dialog.");
        dialog.output_dir = "Old";

        Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());

        string text = File.ReadAllText(destinationFile, Encoding.GetEncoding("shift_jis"));
        StringAssert.Contains(text, "#TITLE Current folder");
        StringAssert.Contains(text, "#CATEGORY Table 901");
        string command = ReadCustomFolderCommand(text);
        using (LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly())
        {
            Assert.AreEqual("Old", verify.Table<BMSTable>().Single(row => row.playlist_id == 901).Output_dir);
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(row => row.path == destinationFile);
            Assert.AreEqual("Current folder", row.title);
            Assert.AreEqual("Table 901", row.category);
            Assert.AreEqual(command, row.command);
            CollectionAssert.AreEqual(new[] { new string('a', 32) },
                verify.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + command).Select(song => song.hash).ToArray());
        }
        Assert.IsFalse(Directory.Exists(before));
        Assert.IsFalse(File.Exists(obsolete));
        foreach (string path in external)
        { Assert.AreEqual(OldText, File.ReadAllText(path, Encoding.GetEncoding("shift_jis"))); }
    }

    /// <summary>
    /// 旧表登録と出力状態のないDBへSQLを復元し、同名の残置ファイルを復元した現在定義へ置換します。
    /// 復元Taskの終端直後に実本文とDBを観測し、非空先の拒否や後続readerによる修復への依存を検出します。
    /// </summary>
    [TestMethod]
    public async Task PlaylistRestore_ReplacesExistingOutputWithoutPreviousPlaylistState()
    {
        string dump;
        await using (var source = new PlaylistRestoreOutputFixture())
        {
            source.PersistAndOutput(PlaylistRestoreOutputFixture.Table(902, "Restored", "Restored folder"));
            dump = source.Playlist.GetPlaylistDump();
        }
        await using var fixture = new PlaylistRestoreOutputFixture();
        using (LR2SongDBExtended empty = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly())
        {
            Assert.AreEqual(0, empty.Table<BMSTable>().Count());
            Assert.AreEqual(0L, empty.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_custom_folder_output_status;"));
        }
        string destination = fixture.OutputDirectory(PlaylistRestoreOutputFixture.Table(902, "Restored"));
        Directory.CreateDirectory(destination);
        string path = Path.Combine(destination, "0000.lr2folder");
        File.WriteAllText(path, "#TITLE Legacy folder\r\n#CATEGORY Legacy table\r\n#COMMAND 0\r\n", Encoding.GetEncoding("shift_jis"));

        await fixture.Playlist.RestorePlaylistDumpAsync(dump);

        string text = File.ReadAllText(path, Encoding.GetEncoding("shift_jis"));
        StringAssert.Contains(text, "#TITLE Restored folder");
        StringAssert.Contains(text, "#CATEGORY Table 902");
        string command = ReadCustomFolderCommand(text);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
        LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(row => row.path == path);
        Assert.AreEqual("Restored folder", row.title);
        Assert.AreEqual("Table 902", row.category);
        Assert.AreEqual(command, row.command);
        CollectionAssert.AreEqual(new[] { new string('a', 32) },
            verify.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + command).Select(song => song.hash).ToArray());
    }

    /// <summary>既存DBの未登録名は通常出力だけを止めて対象名を知らせ、正常な兄弟表とROOT出力を継続します。プロパティの未変更保存でも名を保ち、通常先の再選択で解消します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnregisteredOutputBase_PreservesNameAndStopsOnlyNormalOutputUntilReselected(bool useSummaryFlags)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsPlaylist? playlist = null;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string normal = Path.Combine(directory, "Normal"), root = Path.Combine(directory, "Root");
            var settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = normal,
                LR2CustomFolderOutputBaseDirRootType = root,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var config = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(directory, normal));
            config.SetPlacement(settings);
            var library = new TestBmsLibrary(databaseScope, songPath, config, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), () => BmsLibraryOptionsSnapshot.CreateCurrent(testSettings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => settings, config, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable Table(int id, string name, string? baseName) => new()
            {
                playlist_id = id,
                name = name,
                custom_folder_output_base_name = baseName,
                symbol = "T",
                entries = [CreateEntry(new string('a', 32), "Folder")],
                Folder_order = ["Folder"],
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>([Table(7901, "Unknown", "Missing"), Table(7902, "Healthy", null)]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            playlist.BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(databaseScope, songPath).LoadPlaylistHeaders());
            BMSTable unknown = playlist.BMSTables.Single(table => table.playlist_id == 7901);
            using (PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession())
            {
                playlist.RegenerateCustomFolderOutputs([], settings);
                PlaylistOperationNotificationOwner.OperationNotification warning = notifications.TakeReceipt().Notifications.Single();
                StringAssert.Contains(warning.FormatMessage(), "Unknown"); StringAssert.Contains(warning.FormatMessage(), "Missing");
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(normal, "Unknown")));
            Assert.IsTrue(File.Exists(Path.Combine(normal, "Healthy", "0000.lr2folder")));
            workspace = CreatePlaylistWorkspace(playlist, library, () => settings, () => config);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RebuildPlaylistSummaryView(runAsync: false);
            Assert.AreEqual("Missing", workspace.PlaylistSummaryView.Single(row => row.PlaylistId == 7901).OutputBaseDisplayName);
            workspace.IsPlaylistSummaryMode = false;
            if (useSummaryFlags)
            {
                PlaylistSummaryRow[] rows = new[] { new PlaylistSummaryRow { PlaylistId = unknown.playlist_id, TableRef = unknown } };
                await workspace.ApplyPlaylistSummaryFlags(rows, isRootFolder: true);
                Assert.IsTrue(File.Exists(Path.Combine(root, "Unknown", "0000.lr2folder")));
                await workspace.ApplyPlaylistSummaryFlags(rows, isRootFolder: false);
                Assert.IsFalse(Directory.Exists(Path.Combine(normal, "Unknown")));
                Assert.AreEqual("Missing", unknown.custom_folder_output_base_name);
            }
            dialog = await workspace.OpenPropertyDialogAsync(unknown) ?? throw new AssertFailedException("Missing dialog.");
            Assert.AreEqual("Missing", dialog.custom_folder_output_base_option.BaseName);
            Assert.AreEqual("Missing", dialog.custom_folder_output_base_option.Label);
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.AreEqual("Missing", unknown.custom_folder_output_base_name);
            dialog.is_root_folder = true;
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.IsTrue(File.Exists(Path.Combine(root, "Unknown", "0000.lr2folder")));
            Assert.AreEqual("Missing", unknown.custom_folder_output_base_name);
            dialog.is_root_folder = false;
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.IsFalse(Directory.Exists(Path.Combine(normal, "Unknown")));
            dialog.custom_folder_output_base_option = dialog.OutputBaseOptions.Single(option => option.BaseName == null);
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.IsTrue(File.Exists(Path.Combine(normal, "Unknown", "0000.lr2folder")));
            using LR2SongDBExtended read = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly();
            Assert.IsNull(read.Table<BMSTable>().Single(table => table.playlist_id == 7901).custom_folder_output_base_name);
        }
        finally
        {
            dialog?.Dispose(); workspace?.Dispose(); playlist?.RequestShutdown("unknown-output-fixture");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>実保存したFile/Folder方式の所属を局所出力と全体同期の実SQLで選曲し、Shift_JISで同化する한글/??と日本語・単引用符・接頭辞を区別します。準備後に本文だけを同mtimeの旧条件・別条件・空へ変えても、DBが確定定義を使い物理bytesを修復しないことを保証します。</summary>
    [TestMethod]
    // Fileは指定hashだけ、Folderは同じsong.folderの兄弟も選曲します。各本文改変は生成本文の再parseへの退行を識別します。
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.File, 0)]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.File, 1)]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.File, 2)]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.Folder, 0)]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.Folder, 1)]
    [DataRow(LR2SongDBExtended.playlist.EntryUnitType.Folder, 2)]
    public void ManagedDefinitions_PreserveConfirmedSelectionsWhenPhysicalTextChangesAtSameMtime(
        LR2SongDBExtended.playlist.EntryUnitType entryType, int replacement)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory), output = Path.Combine(directory, "Output");
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            var settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = output,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string[] names = ["★한글", "★??", "★日本語' 内部  空白"];
            var table = new BMSTable
            {
                playlist_id = 7981,
                name = "日本語' Table",
                symbol = "T",
                Output_dir = "Table",
                compat_prefix = "★",
                entry_type = entryType,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries = names.Select((name, index) => CreateEntry((index + 1).ToString("x32"), name)).ToList(),
                Folder_order = names.ToList()
            };
            playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => settings, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = new ObservableCollection<BMSTable>([table]) };
            playlist.CommitBMSTablesWithEntriesToDB([table]);
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songPath))
            {
                seed.CreateTable<LR2SongDB.folder>();
                seed.CreateTable<LR2ScoreDB.score>();
                for (int number = 1; number <= 4; number++)
                {
                    seed.Execute("INSERT INTO song (hash, title, folder, path) VALUES (?, ?, ?, ?);",
                        number.ToString("x32"), "Song " + number, number == 4 ? "Package1" : "Package" + number, "song" + number + ".bms");
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(databaseScope, songPath).LoadPlaylistHeaders());
            table = playlist.BMSTables.Single();
            playlist.ReOutputCustomFolder(table);
            void AssertSelections()
            {
                using LR2SongDBExtended verify = databaseScope.OpenSongWrite(songPath);
                foreach ((string name, int index) in names.Select((name, index) => (name, index)))
                {
                    LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(folder => folder.type == 2 && folder.title == name);
                    int[] expected = entryType == LR2SongDBExtended.playlist.EntryUnitType.Folder && index == 0 ? [1, 4] : [index + 1];
                    AssertCustomFolderCommandMatches(verify, row.command, expected);
                    Assert.AreEqual("日本語' Table", row.category);
                }
                Assert.AreEqual(4, verify.Table<LR2SongDB.song>().Count(), "folder全体同期はsong所属を変更しません。");
            }
            AssertSelections();
            Lr2SongDbSyncPreparedDataSurface surface;
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable lease));
            using (lease)
            using (LibraryFileMutationCapability capability = playlist.CreatePlaylistMutationCapability(lease))
            {
                surface = playlist.ReOutputAllCustomFoldersForLr2SongDbSyncUnderExistingReservation("test_confirmed_definitions", capability, settings: settings);
            }
            Assert.IsNotNull(surface.ManagedFolderDefinitions);
            Assert.AreEqual(3, surface.ManagedFolderDefinitions.Count);
            byte[] changedBytes = Encoding.GetEncoding("shift_jis").GetBytes(replacement switch
            {
                0 => "#TITLE Old\r\n#COMMAND song.hash = '00000000000000000000000000000002'\r\n",
                1 => "#TITLE Different\r\n#COMMAND 0\r\n",
                _ => string.Empty
            });
            foreach (string path in surface.Lr2FolderFilePaths)
            {
                File.WriteAllBytes(path, changedBytes);
                File.SetLastWriteTimeUtc(path, surface.Lr2FolderFileEntries[path].LastWriteTimeUtc ?? throw new AssertFailedException("Missing mtime."));
            }
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songPath))
            {
                Lr2FolderTableReconciliationService.Reconcile(db, new Lr2SongDbSyncRequest
                {
                    Lr2NormalCustomFolderOutputBaseDir = output,
                    Lr2RootCustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDirRootType,
                    Lr2FolderFilePaths = surface.Lr2FolderFilePaths,
                    Lr2FolderFileEntries = surface.Lr2FolderFileEntries,
                    DirectoryEntries = surface.DirectoryEntries,
                    ManagedFolderDefinitions = surface.ManagedFolderDefinitions
                });
            }
            AssertSelections();
            foreach (string path in surface.Lr2FolderFilePaths)
            {
                CollectionAssert.AreEqual(changedBytes, File.ReadAllBytes(path), "本文の物理修復後を読む方法でparse依存を隠しません。");
                Assert.AreEqual(surface.Lr2FolderFileEntries[path].LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
            }
        }
        finally
        {
            playlist?.RequestShutdown("confirmed-definition-fixture");
            Directory.Delete(directory, recursive: true);
        }
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>利用者フォルダを実出力し、Shift_JIS本文と対応するLR2の種別・カテゴリ・SQL・日時を確定定義から保存します。登録rootのfolderinfoタイトルとCRCを局所出力・全体同期の後も保持し、追加親生成による表示退行を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsLr2FolderRowsFromConfirmedPlaylist()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null;
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = tempDirectory;
            string rootFolderInfo = Path.Combine(tempDirectory, "folderinfo.txt");
            File.WriteAllText(rootFolderInfo, "#TITLE Library root title", Encoding.ASCII);
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7301,
                name = "FolderTable",
                symbol = "FT",
                Output_dir = "FolderTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = Lr2FolderPath.ToFolderPath(tempDirectory), title = "Library root title", type = 1, parent = Lr2SongFolderParentNormalizer.RootParentHash, adddate = 123 });
            }
            string outputPath = Path.Combine(outputBaseDir, "FolderTable", "0001.lr2folder");
            var physicalSurface = CustomFolderOutputPhysicalSurface.FromEntries(
                [new RootFileEnumerationEntry(outputPath, DateTime.UtcNow)],
                discoveryComplete: true);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, physicalSurface);
            playlist = new TestBmsPlaylist(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(tempDirectory, tempDirectory)), null, null, () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings), () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), synchronization, uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsTrue(File.Exists(outputPath));
            string text = File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(text, "#TITLE Folder A");
            StringAssert.Contains(text, "#CATEGORY FolderTable");
            StringAssert.Contains(text, "#COMMAND song.hash");

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
                Assert.AreEqual(2, folder.type);
                Assert.AreEqual("Folder A", folder.title);
                Assert.AreEqual("FolderTable", folder.category);
                StringAssert.Contains(folder.command, "playlist_entry");
                Assert.AreEqual(0, folder.max);
                Assert.IsTrue(folder.date.HasValue);
                Assert.IsTrue(folder.adddate.HasValue);
                LR2SongDB.folder root = verify.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(tempDirectory));
                Assert.AreEqual("Library root title", root.title);
                Assert.AreEqual(123, root.adddate);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, root.parent);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBaseDir), verify.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(Path.Combine(outputBaseDir, "FolderTable"))).parent);
            }
            Lr2SongDbSyncPreparedDataSurface surface;
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable lease));
            using (lease)
            using (LibraryFileMutationCapability capability = playlist.CreatePlaylistMutationCapability(lease))
            {
                surface = playlist.ReOutputAllCustomFoldersForLr2SongDbSyncUnderExistingReservation("test_root_folderinfo", capability);
            }
            using LR2SongDBExtended whole = databaseScope.OpenSongWrite(songDbPath);
            Lr2FolderTableReconciliationService.Reconcile(whole, new Lr2SongDbSyncRequest
            {
                RootDirectories = [tempDirectory],
                Lr2NormalCustomFolderOutputBaseDir = outputBaseDir,
                FolderInfoFilePaths = [rootFolderInfo],
                Lr2FolderFilePaths = surface.Lr2FolderFilePaths,
                Lr2FolderFileEntries = surface.Lr2FolderFileEntries,
                DirectoryEntries = surface.DirectoryEntries,
                ManagedFolderDefinitions = surface.ManagedFolderDefinitions
            });
            Assert.AreEqual("Library root title", whole.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(tempDirectory)).title);
            Assert.AreEqual(123, whole.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(tempDirectory)).adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, whole.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(tempDirectory)).parent);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBaseDir), whole.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(Path.Combine(outputBaseDir, "FolderTable"))).parent);

        }
        finally
        {
            playlist?.RequestShutdown("root-folderinfo-fixture");
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同期port失敗の元例外を通知sessionの有無によらず返し、session内では表名付き警告を一件蓄積します。失敗の置換と警告の欠落・重複を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolder_FailureRequiresNotificationScopeOrQueuesWarning()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "FailureTable",
                symbol = "FTF",
                Output_dir = "FailureTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")],
                Folder_order = ["Folder A"]
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope,
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            var expectedFailure = new InvalidOperationException("forced custom-folder sync failure");
            synchronization.Failure = expectedFailure;
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, synchronization, uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            Assert.AreSame(
                expectedFailure,
                Assert.ThrowsExactly<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table)));

            using (PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession())
            {
                Assert.AreSame(
                    expectedFailure,
                    Assert.ThrowsExactly<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table)));
                PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
                Assert.AreEqual(1, receipt.Notifications.Count);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, receipt.Notifications[0].Severity);
                StringAssert.Contains(receipt.Notifications[0].FormatMessage(), table.name);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存値と異なる注入配置・未送信検知を採用して実出力し、保存値側には生成しません。出力先や種類を共有設定へ戻す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_UsesInjectedCustomFolderOutputSettings()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = false;
            string settingsOutputBaseDir = Path.Combine(tempDirectory, "SettingsOutput");
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput");
            testSettings.LR2CustomFolderOutputBaseDir = settingsOutputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "SettingsRootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = true
            };
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "InjectedOutputTable",
                symbol = "IOT",
                Output_dir = "InjectedOutputTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Injected Folder")
                ],
                Folder_order = ["Injected Folder"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), getOutputSettings, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsTrue(File.Exists(Path.Combine(providerOutputBaseDir, "InjectedOutputTable", "0001.lr2folder")));
            Assert.IsFalse(File.Exists(Path.Combine(settingsOutputBaseDir, "InjectedOutputTable", "0001.lr2folder")));
            Assert.IsTrue(Directory.GetFiles(Path.Combine(providerOutputBaseDir, "InjectedOutputTable"), "*.lr2folder")
                .Any(path => File.ReadAllText(path, Encoding.GetEncoding("shift_jis")).IndexOf("#TITLE UNSENT SONGS", StringComparison.Ordinal) >= 0));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>注入した通常先とルート型表ディレクトリを実configへ補い、保存値側のルート基点は登録しません。注入配置を無視する誤りと表ルートの作成漏れを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void SyncCustomFolderOutputSearchRootsAfterSettingsChange_UsesInjectedOutputSettings()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = false;
            string settingsNormalOutputBaseDir = Path.Combine(tempDirectory, "SettingsNormalOutput");
            string settingsRootOutputBaseDir = Path.Combine(tempDirectory, "SettingsRootOutput");
            string providerNormalOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootOutput");
            testSettings.LR2CustomFolderOutputBaseDir = settingsNormalOutputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = settingsRootOutputBaseDir;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var table = new BMSTable
            {
                playlist_id = 7303,
                is_root_folder = true,
                Output_dir = "InjectedRootOutput"
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, configOwner, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            Assert.IsTrue(playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChange(settingsRootOutputBaseDir));

            string providerRootOutput = Path.Combine(providerRootOutputBaseDir, table.Output_dir);
            CollectionAssert.Contains(configOwner.Capture().RegisteredRoots.ToArray(), providerRootOutput);
            CollectionAssert.Contains(configOwner.Capture().RegisteredRoots.ToArray(), providerNormalOutputBaseDir);
            CollectionAssert.DoesNotContain(configOwner.Capture().RegisteredRoots.ToArray(), settingsRootOutputBaseDir);
            Assert.IsTrue(Directory.Exists(providerRootOutput));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>長い実出力パスにもShift_JISファイルと対応DB行を生成します。通常のFile API制限で出力を欠落させる誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesLr2FolderUnderLongPath()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = BuildLongDirectoryPath(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7319,
                name = "LongFolderTable",
                symbol = "LFT",
                Output_dir = "LongFolderTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder B"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(outputBaseDir, "LongFolderTable", "0001.lr2folder");
            Assert.IsTrue(LongPathFileSystem.FileExists(outputPath));
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE Folder B");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", outputPath));
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempDirectory))
            {
                LongPathFileSystem.DeleteDirectory(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>追加出力先の基点、表ディレクトリ、生成ファイルを実DBへ階層保存します。親hashを取り違えてLR2の階層を崩す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_GeneratesAdditionalOutputBaseParentRow()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string defaultOutputBaseDir = Path.Combine(tempDirectory, "DefaultCustomFolder");
            string additionalOutputBaseDir = Path.Combine(tempDirectory, "Additional");
            Directory.CreateDirectory(additionalOutputBaseDir);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = defaultOutputBaseDir;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBaseDir]);
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7303,
                name = "AdditionalTable",
                symbol = "AT",
                Output_dir = "AdditionalTable",
                custom_folder_output_base_name = "Additional",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDirectory = Path.Combine(additionalOutputBaseDir, "AdditionalTable");
            string outputPath = Path.Combine(outputDirectory, "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder? outputBaseRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(additionalOutputBaseDir));
            Assert.IsNotNull(outputBaseRow, rowSummary);
            LR2SongDB.folder outputBaseFolder = outputBaseRow!;
            Assert.AreEqual(1, outputBaseFolder.type);
            Assert.AreEqual("Additional", outputBaseFolder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseFolder.parent);
            LR2SongDB.folder? tableRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(outputDirectory));
            Assert.IsNotNull(tableRow, rowSummary);
            LR2SongDB.folder outputTableFolder = tableRow!;
            Assert.AreEqual(1, outputTableFolder.type);
            Assert.AreEqual("AdditionalTable", outputTableFolder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(additionalOutputBaseDir), outputTableFolder.parent);
            LR2SongDB.folder? folderFileRow = rows.SingleOrDefault(row => row.path == outputPath);
            Assert.IsNotNull(folderFileRow, rowSummary);
            LR2SongDB.folder outputFileRow = folderFileRow!;
            Assert.AreEqual(2, outputFileRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDirectory), outputFileRow.parent);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 同期元と出力先A→Bの同時保存を実JSON・DBで確定した後のDB失敗でも、同じ編集sessionの再保存で旧A整理・LR2登録・新項目BMTを完了します。
    /// 実CollectionChangedの置換確定後だけ対象B配下のfolder INSERTを拒否し、取得や正本置換自体の失敗と分けます。通常移行・config失敗は既存継続ケースへ分担します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_ExternalReloadCommitRetainsOutputMigrationAfterLaterDatabaseFailure()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsPlaylist? playlist = null;
        Task<PlaylistPropertyDialogOperationResult>? saving = null;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler? afterReplace = null;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string root = Path.Combine(directory, "root-output");
            string a = Path.Combine(root, "A"), b = Path.Combine(root, "B");
            string bmt = Path.Combine(directory, "bmt");
            string oldHeader = Path.Combine(directory, "old-header.json"), oldData = Path.Combine(directory, "old-data.json");
            string newHeader = Path.Combine(directory, "new-header.json"), newData = Path.Combine(directory, "new-data.json");
            File.WriteAllText(oldHeader, "{\"name\":\"External migration\",\"symbol\":\"X\",\"data_url\":\"old-data.json\"}");
            File.WriteAllText(oldData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            File.WriteAllText(newHeader, "{\"name\":\"External migration\",\"symbol\":\"X\",\"data_url\":\"new-data.json\"}");
            File.WriteAllText(newData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(directory, a);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.EnablePlaylistUrlCompletion = false;
            });
            var output = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = Path.Combine(directory, "normal-output"),
                LR2CustomFolderOutputBaseDirRootType = root,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var library = new TestBmsLibrary(databaseScope, songPath, configOwner, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => output, configOwner, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt }, settings: settings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(oldHeader));
            table.playlist_id = 7461;
            table.Page_url = new Uri(oldHeader);
            Assert.IsTrue(table.EnableExternalSync());
            table.Output_dir = "A";
            table.is_root_folder = true;
            table.is_bmt_output = true;
            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            var other = new BMSTable { playlist_id = 7462, name = "Other", Output_dir = "Other", entries = [] };
            playlist.BMSTables = new ObservableCollection<BMSTable>([table, other]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            playlist.ReOutputCustomFolder(table);
            Assert.IsTrue(Directory.GetFiles(a, "*.lr2folder").Length > 0);
            string otherFile = Path.Combine(root, "Other", "manual.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(otherFile) ?? throw new AssertFailedException("Other output has no parent."));
            File.WriteAllText(otherFile, "other");
            string unmanaged = Path.Combine(directory, "unmanaged.txt");
            File.WriteAllText(unmanaged, "keep");
            workspace = CreatePlaylistWorkspace(playlist, library, () => output, () => configOwner);
            int replacements = 0;
            afterReplace = (_, change) =>
            {
                if (change.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace) { return; }
                replacements++;
                BMSTable confirmed = playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id);
                Assert.AreNotSame(table, confirmed);
                using LR2SongDBExtended db = databaseScope.OpenSongWrite(songPath);
                Assert.AreEqual("B", db.Table<BMSTable>().Single(candidate => candidate.playlist_id == table.playlist_id).Output_dir);
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == table.playlist_id));
                string bPrefix = (b + Path.DirectorySeparatorChar).Replace("'", "''", StringComparison.Ordinal);
                db.Execute($"CREATE TRIGGER fail_later_folder BEFORE INSERT ON folder WHEN substr(NEW.path, 1, length('{bPrefix}')) = '{bPrefix}' BEGIN SELECT RAISE(ABORT, 'test_late_database_failure'); END;");
            };
            playlist.BMSTables.CollectionChanged += afterReplace;
            dialog = await workspace.OpenPropertyDialogAsync(table) ?? throw new AssertFailedException("Property dialog was not created.");
            dialog.Page_url = new Uri(newHeader);
            dialog.output_dir = "B";
            saving = dialog.SaveAndApplyAsync();
            Exception? failure = null;
            try { await saving; } catch (Exception caught) { failure = caught; }
            Assert.IsNotNull(failure);
            StringAssert.Contains(failure.ToString(), "test_late_database_failure");
            Assert.AreEqual(1, replacements);
            BMSTable replacement = playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id);
            Assert.AreNotSame(table, replacement);
            Assert.AreEqual("B", dialog.output_dir);
            Assert.IsTrue(Directory.Exists(a));
            playlist.BMSTables.CollectionChanged -= afterReplace;
            afterReplace = null;
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songPath)) { db.Execute("DROP TRIGGER fail_later_folder;"); }
            File.Delete(newHeader);
            File.Delete(newData);
            saving = dialog.SaveAndApplyAsync();
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await saving);
            Assert.AreSame(replacement, playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id));
            Assert.IsFalse(Directory.Exists(a));
            Assert.IsTrue(Directory.GetFiles(b, "*.lr2folder").Length > 0);
            LR2Config disk = TestLr2ConfigFactory.Load(Path.Combine(directory, "LR2files", "Config", "config.xml"));
            CollectionAssert.DoesNotContain(disk.GetBMSSearchDirectoriesForChangeTracking(), a);
            CollectionAssert.Contains(disk.GetBMSSearchDirectoriesForChangeTracking(), b);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.IsFalse(db.Table<LR2SongDB.folder>().Any(row => row.path.StartsWith(a, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.path.StartsWith(b, StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == table.playlist_id));
                Assert.AreEqual("Other", db.Table<BMSTable>().Single(candidate => candidate.playlist_id == other.playlist_id).Output_dir);
            }
            string bmtPath = Directory.GetFiles(bmt, "*.bmt").Single();
            Newtonsoft.Json.Linq.JObject exported = BeatorajaBmtTableImportService.ReadCachedTableData(bmtPath);
            StringAssert.Contains(exported.ToString(), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Assert.AreEqual("other", File.ReadAllText(otherFile));
            Assert.AreEqual("keep", File.ReadAllText(unmanaged));
        }
        finally
        {
            if (saving != null) { try { await saving; } catch (Exception) { } }
            if (playlist != null && afterReplace != null) { playlist.BMSTables.CollectionChanged -= afterReplace; }
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// DB=B確定後のLR2新先出力・config保存・BMT出力の三つの失敗境界でDB=Bを保持し、
    /// 同じ編集画面の明示再保存だけで旧Aを整理します。C再編集では未完了を先に終えるPとTask終端も確認します。
    /// bmt行は必要なBMT出力の途中でPを保持したまま止め、migration/config行は通知を受付の解放後に依頼するため、完了後の状態だけを確認します。
    /// 通知の表示を保存の失敗・Pの保持へ加えず、下位移行の管理領域・モード網羅は既存移行テスト、画面の所有と表示はWPF代表へ分担します。
    /// </summary>
    [TestMethod]
    // DB=B確定後、migrationは新先Bのディレクトリ生成、configは登録保存、bmtは台帳保存を失敗させる。bmt行だけ追加編集Cを行う。
    [DataRow("migration", false)]
    [DataRow("config", false)]
    [DataRow("bmt", true)]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_UnfinishedOutputsCompleteBeforeSavingFurtherEdits(string failurePoint, bool editAgain)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsLibrary? library = null;
        TestBmsPlaylist? playlist = null;
        FileStream? blocker = null;
        Task<PlaylistPropertyDialogOperationResult>? saving = null;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holdNotification = false;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string rootBase = Path.Combine(directory, "root-output");
            string normalBase = Path.Combine(directory, "normal-output");
            string a = Path.Combine(rootBase, "A");
            string b = Path.Combine(rootBase, "B");
            string c = Path.Combine(rootBase, "C");
            string unmanaged = Path.Combine(directory, "unmanaged.txt");
            File.WriteAllText(unmanaged, "preserve");
            string otherOutput = Path.Combine(rootBase, "Other");
            Directory.CreateDirectory(otherOutput);
            string otherFile = Path.Combine(otherOutput, "manual.txt");
            File.WriteAllText(otherFile, "other-table");
            string bmt = Path.Combine(directory, "bmt-output");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(directory, Path.Combine(directory, "manual-root"), a);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = normalBase,
                LR2CustomFolderOutputBaseDirRootType = rootBase,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = directory;
                values.BMSRootPath = Path.Combine(directory, "manual-root");
                values.LR2CustomFolderOutputBaseDir = normalBase;
                values.LR2CustomFolderOutputBaseDirRootType = rootBase;
                values.EnablePlaylistUrlCompletion = false;
            });
            library = new TestBmsLibrary(databaseScope, songPath, configOwner, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => outputSettings, configOwner, () => new PlaylistUrlCompletionOptionsSnapshot(), () =>
                {
                    if (holdNotification && failurePoint == "bmt") { entered.TrySetResult(); release.Wait(); }
                    return new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt };
                }, settings: settings, uiScheduler: new TestUiScheduler(() => null));
            var table = new BMSTable
            {
                playlist_id = 7401,
                name = "Continuation",
                symbol = "X",
                Output_dir = "A",
                is_root_folder = true,
                is_bmt_output = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries = [CreateEntry("1234567890abcdef1234567890abcdef", "Alpha")],
                Folder_order = ["Alpha"]
            };
            var other = new BMSTable { playlist_id = 7402, name = "Other", Output_dir = "Other", is_root_folder = true, is_bmt_output = false, entries = [] };
            playlist.BMSTables = new ObservableCollection<BMSTable>([table, other]);
            playlist.CommitBMSTablesWithEntriesToDB([table, other]);
            playlist.ReOutputCustomFolder(table);
            Assert.IsTrue(Directory.GetFiles(a, "*.lr2folder").Length > 0);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, library, () => outputSettings, () => configOwner);
            dialog = await workspace.OpenPropertyDialogAsync(table)
                ?? throw new AssertFailedException("Edit dialog was not created.");
            dialog.output_dir = "B";
            string configPath = Path.Combine(directory, "LR2files", "Config", "config.xml");
            if (failurePoint == "migration") { File.WriteAllText(b, "blocks-directory"); }
            else
            {
                string blockPath = failurePoint == "config" ? configPath : manifest;
                if (!File.Exists(blockPath)) { File.WriteAllText(blockPath, "{\"files\":[],\"playlists\":{}}"); }
                blocker = new FileStream(blockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            if (failurePoint == "bmt")
            {
                await Assert.ThrowsExactlyAsync<PlaylistMutationPostCommitException>(async () => await dialog.SaveAndApplyAsync());
            }
            else { await Assert.ThrowsExactlyAsync<IOException>(async () => await dialog.SaveAndApplyAsync()); }
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
            }
            if (failurePoint == "migration") { Assert.IsTrue(Directory.Exists(a)); }
            if (failurePoint == "config")
            {
                Assert.IsFalse(Directory.Exists(a));
                Assert.IsTrue(Directory.GetFiles(b, "*.lr2folder").Length > 0);
                LR2Config disk = TestLr2ConfigFactory.Load(configPath);
                CollectionAssert.Contains(disk.GetBMSSearchDirectoriesForChangeTracking(), a);
                CollectionAssert.DoesNotContain(disk.GetBMSSearchDirectoriesForChangeTracking(), b);
            }
            if (editAgain) { dialog.output_dir = "C"; }
            holdNotification = true;
            saving = dialog.SaveAndApplyAsync();
            IDisposable? rejected;
            if (failurePoint == "bmt")
            {
                // 必要なBMT出力の途中（Pの保持中）で止め、保存の終端とPの保持を確認します。
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, saving, "property-save.pending-output-failure");
                Assert.IsFalse(saving.IsCompleted);
                Assert.IsFalse(playlist.TryEnterPlaylistMutation(out rejected));
                rejected?.Dispose();
                Assert.IsFalse(Directory.Exists(c));
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
                }
            }
            release.Set();
            if (failurePoint == "bmt")
            {
                await Assert.ThrowsExactlyAsync<PlaylistMutationPostCommitException>(async () => await saving);
            }
            else { await Assert.ThrowsExactlyAsync<IOException>(async () => await saving); }
            Assert.IsFalse(Directory.Exists(c));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
            }
            Assert.AreEqual(editAgain ? "C" : "B", dialog.output_dir);
            blocker?.Dispose();
            blocker = null;
            if (failurePoint == "migration") { File.Delete(b); }
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release.Reset();
            saving = dialog.SaveAndApplyAsync();
            if (failurePoint == "bmt")
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, saving, "property-save.pending-output-success");
                Assert.IsFalse(saving.IsCompleted);
                Assert.IsFalse(playlist.TryEnterPlaylistMutation(out rejected));
                rejected?.Dispose();
                Assert.IsFalse(Directory.Exists(c));
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
                }
            }
            holdNotification = false;
            release.Set();
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await saving);
            string finalDirectory = editAgain ? c : b;
            Assert.IsFalse(Directory.Exists(a));
            if (editAgain) { Assert.IsFalse(Directory.Exists(b)); }
            Assert.IsTrue(Directory.GetFiles(finalDirectory, "*.lr2folder").Length > 0);
            LR2Config finalConfig = TestLr2ConfigFactory.Load(configPath);
            CollectionAssert.DoesNotContain(finalConfig.GetBMSSearchDirectoriesForChangeTracking(), a);
            CollectionAssert.Contains(finalConfig.GetBMSSearchDirectoriesForChangeTracking(), finalDirectory);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(editAgain ? "C" : "B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
                Assert.AreEqual("Other", db.Table<BMSTable>().Single(row => row.playlist_id == other.playlist_id).Output_dir);
                Assert.IsFalse(db.Table<LR2SongDB.folder>().Any(row => row.path.StartsWith(a, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.path.StartsWith(finalDirectory, StringComparison.OrdinalIgnoreCase)));
            }
            Assert.IsTrue(File.Exists(manifest));
            Assert.AreEqual("preserve", File.ReadAllText(unmanaged));
            Assert.AreEqual("other-table", File.ReadAllText(otherFile));
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            holdNotification = false;
            release.Set();
            if (saving != null) { try { await saving; } catch (Exception) { } }
            blocker?.Dispose();
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 旧出力先の削除失敗は、DBをB確定のまま保留出力として残し、保存の例外本文と通知の警告に同じ整形済み警告を使います。
    /// ロック解除後の再保存で旧Aが回収されることで、警告の事実化が保留出力の分類と回収を変えていないことを確認します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertySave_OldOutputCleanupFailureKeepsWarningTextAsPendingFailure()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsLibrary? library = null;
        TestBmsPlaylist? playlist = null;
        FileStream? lockedFile = null;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string rootBase = Path.Combine(directory, "root-output");
            string normalBase = Path.Combine(directory, "normal-output");
            string a = Path.Combine(rootBase, "A");
            string b = Path.Combine(rootBase, "B");
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(directory, Path.Combine(directory, "manual-root"), a);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = normalBase,
                LR2CustomFolderOutputBaseDirRootType = rootBase,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = directory;
                values.BMSRootPath = Path.Combine(directory, "manual-root");
                values.LR2CustomFolderOutputBaseDir = normalBase;
                values.LR2CustomFolderOutputBaseDirRootType = rootBase;
                values.EnablePlaylistUrlCompletion = false;
            });
            library = new TestBmsLibrary(databaseScope, songPath, configOwner, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => outputSettings, configOwner, () => new PlaylistUrlCompletionOptionsSnapshot(), () =>
                new BeatorajaBmtOptionsSnapshot(), settings: settings, uiScheduler: new TestUiScheduler(() => null));
            var table = new BMSTable
            {
                playlist_id = 7411,
                name = "CleanupFailure",
                symbol = "X",
                Output_dir = "A",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries = [CreateEntry("1234567890abcdef1234567890abcdef", "Alpha")],
                Folder_order = ["Alpha"]
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTablesWithEntriesToDB([table]);
            playlist.ReOutputCustomFolder(table);
            string[] outputFiles = Directory.GetFiles(a, "*.lr2folder");
            Assert.IsTrue(outputFiles.Length > 0);
            var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, library, () => outputSettings, () => configOwner,
                playlistWorkspaceDialogService: dialogs);
            dialog = await workspace.OpenPropertyDialogAsync(table)
                ?? throw new AssertFailedException("Edit dialog was not created.");
            dialog.output_dir = "B";
            lockedFile = new FileStream(outputFiles[0], FileMode.Open, FileAccess.Read, FileShare.None);

            IOException failure = await Assert.ThrowsExactlyAsync<IOException>(async () => await dialog.SaveAndApplyAsync());

            string expected = string.Format(Resources.Warn_FileOrDirDeleteFailed, a);
            Assert.AreEqual(expected, failure.Message);
            UiMessageRequest warning = dialogs.MessageRequests.Single();
            Assert.AreEqual(System.Windows.MessageBoxImage.Exclamation, warning.Icon);
            Assert.AreEqual(Resources.MessageBoxTitle_Warning, warning.Caption);
            Assert.AreEqual(expected, warning.MessageBoxText);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
            }
            Assert.IsTrue(Directory.Exists(a));

            lockedFile.Dispose();
            lockedFile = null;
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.IsFalse(Directory.Exists(a));
            Assert.IsTrue(Directory.GetFiles(b, "*.lr2folder").Length > 0);
        }
        finally
        {
            lockedFile?.Dispose();
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>DB確定後の必須header公開で故障し、同じ原因を返してDB・live・参照・dialog resetをNEWへ保ちます。未変更の次保存で旧公開を再実行しません。故障内でDB NEWも確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PostSaveFailureReconcilesDialogWithDurableActiveState()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsPlaylist? playlistForCleanup = null;
        TestBmsLibrary? libraryForCleanup = null;
        BMSTable? subscribedTable = null;
        PropertyChangedEventHandler? headerPublication = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7313,
                name = "PostSaveFailure",
                symbol = "OLD",
                Output_dir = "PostSaveFailure",
                entries = [CreateEntry("ffffffffffffffffffffffffffffffff", "Folder")]
            };
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: testSettings);
            libraryForCleanup = library;
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath, () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(), beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlistForCleanup = playlist;
            playlist.CommitBMSTableWithEntriesToDB(table);
            var expectedFailure = new InvalidOperationException("test post-save header publication failure");
            int headerPublicationCount = 0;
            headerPublication = (_, change) =>
            {
                if (change.PropertyName != nameof(BMSTable.symbol)) { return; }
                Interlocked.Increment(ref headerPublicationCount);
                // CommitPlaylistHeaderEditsがDBと参照を確定した後に必須公開する地点でのみ故障する。
                using LR2SongDBExtended committed = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual("NEW", committed.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).symbol);
                Assert.AreEqual("NEW", table.symbol);
                Assert.AreEqual("NEW", library.GetPlaylistReferenceDisplay(table.entries.Single().md5, null).Symbols);
                throw expectedFailure;
            };
            table.PropertyChanged += headerPublication;
            subscribedTable = table;
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });

            service.PlaylistPropertyCatalogChanged += (_, _) => { };
            service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session, UndeclaredDialogs.Create())
            {
                symbol = "NEW"
            };

            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreSame(expectedFailure, failure);
            Assert.AreEqual(1, headerPublicationCount);
            Assert.AreEqual("NEW", table.symbol);
            using (LR2SongDBExtended durable = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            { Assert.AreEqual("NEW", durable.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).symbol); }
            Assert.AreEqual("NEW", library.GetPlaylistReferenceDisplay(table.entries.Single().md5, null).Symbols);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            Assert.AreEqual("NEW", dialog.symbol);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            Assert.AreEqual("NEW", dialog.symbol);
            Assert.AreEqual(1, headerPublicationCount, "未変更の明示保存で、失敗した旧header公開を予約・再実行しません。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                "NEW",
                verify.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).symbol);
        }
        finally
        {
            if (subscribedTable != null && headerPublication != null) { subscribedTable.PropertyChanged -= headerPublication; }
            dialog?.Dispose();
            playlistForCleanup?.RequestShutdown("playlist_property_post_save_failure_test");
            libraryForCleanup?.RequestShutdown("playlist_property_post_save_failure_test");
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>接頭辞書換え後の選択remap通知が失敗しても、次の明示保存で行の接頭辞を二重付与せず保存値を保持します。公開失敗を未保存扱いする誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_FreshSaveAfterFailureDoesNotRewriteCompatiblePrefixTwice()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSTableEntry entry = CreateEntry("abababababababababababababababab", "Alpha");
            entry.folder = "Alpha";
            var table = new BMSTable
            {
                playlist_id = 7314,
                name = "PrefixRetry",
                symbol = "PR",
                Output_dir = "PrefixRetry",
                compat_prefix = string.Empty,
                entries = [entry],
                Folder_order = ["Alpha"]
            };
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: testSettings);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            int remapRequestCount = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) =>
            {
                if (Interlocked.Increment(ref remapRequestCount) == 1)
                {
                    throw new InvalidOperationException("test remap presentation failure");
                }
            };

            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session, UndeclaredDialogs.Create())
            {
                compat_prefix = "★"
            };

            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test remap presentation failure", failure.Message);
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(1, remapRequestCount);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                "★Alpha",
                verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id).folder);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>元から接頭辞を含む行も実書換えし、Folder_order通知失敗後の次保存では確定した一回分だけを保持します。通知例外で書換えを重ねる誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PrefixNotificationFailureKeepsDurableRewriteWithoutReplay()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSTableEntry entry = CreateEntry("acacacacacacacacacacacacacacacac", "Alpha");
            entry.folder = "Alpha";
            BMSTableEntry prefixedEntry = CreateEntry("aeaeaeaeaeaeaeaeaeaeaeaeaeaeaeae", "★Alpha");
            prefixedEntry.folder = "★Alpha";
            var table = new BMSTable
            {
                playlist_id = 7316,
                name = "PrefixNotificationRetry",
                symbol = "PNR",
                Output_dir = "PrefixNotificationRetry",
                compat_prefix = string.Empty,
                entries = [entry, prefixedEntry],
                Folder_order = ["Alpha", "★Alpha"]
            };
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                seed.InsertOrReplace(prefixedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: testSettings),
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) => { };

            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            bool failRewriteNotification = true;
            table.PropertyChanged += (_, change) =>
            {
                if (failRewriteNotification
                    && string.Equals(change.PropertyName, "Folder_order", StringComparison.Ordinal))
                {
                    failRewriteNotification = false;
                    throw new InvalidOperationException("test prefix rewrite notification failure");
                }
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session, UndeclaredDialogs.Create())
            {
                compat_prefix = "★"
            };

            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test prefix rewrite notification failure", failure.Message);
            Assert.AreEqual("★Alpha", table.entries.Single(candidate => candidate.md5 == entry.md5).folder);
            Assert.AreEqual("★★Alpha", table.entries.Single(candidate => candidate.md5 == prefixedEntry.md5).folder);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual("★Alpha", table.entries.Single(candidate => candidate.md5 == entry.md5).folder);
            Assert.AreEqual("★★Alpha", table.entries.Single(candidate => candidate.md5 == prefixedEntry.md5).folder);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                "★Alpha",
                verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5).folder);
            Assert.AreEqual(
                "★★Alpha",
                verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == prefixedEntry.md5).folder);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>公開失敗後に正本symbolが別変更された場合は古い編集sessionの再保存を拒み、その値と確定済み接頭辞を保持します。古い編集内容による上書きを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_FreshSaveRejectsChangedSavedValues()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSTableEntry entry = CreateEntry("adadadadadadadadadadadadadadadad", "Alpha");
            entry.folder = "Alpha";
            var table = new BMSTable
            {
                playlist_id = 7317,
                name = "PrefixStaleRetry",
                symbol = "PSR",
                Output_dir = "PrefixStaleRetry",
                compat_prefix = string.Empty,
                entries = [entry],
                Folder_order = ["Alpha"]
            };
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: testSettings),
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            int remapCount = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) =>
            {
                if (Interlocked.Increment(ref remapCount) == 1)
                {
                    throw new InvalidOperationException("test pending follow-up failure");
                }
            };

            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session, UndeclaredDialogs.Create())
            {
                compat_prefix = "★"
            };

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());
            table.symbol = "CONCURRENT";

            InvalidOperationException stale = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            StringAssert.Contains(stale.Message, "changed after editing began");
            Assert.AreEqual("CONCURRENT", table.symbol);
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(1, remapCount);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>確認の失敗では同期を有効にせず、承認後の完了表示・BMT進捗の失敗は保存成功を変えません。次保存で確定済み取得・出力を繰り返さず同期設定と行を保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_FreshSaveAfterPublicationFailureDoesNotReplayExternalReload()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalRetry\",\r\n\"symbol\":\"ER\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd\",\"title\":\"External Retry Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 7315;
            table.Page_url = new Uri(headerJsonPath);
            table.DisableExternalSync();
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
            }
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: testSettings);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            bool failConfirmation = true;
            var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService
            {
                ConfirmationTaskFactory = _ =>
                {
                    if (failConfirmation) { throw new IOException("confirmation unavailable"); }
                    return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                }
            };
            int bmtNotifications = 0;
            playlist.BmtOutput.RequestProgressReporter = (_, _) =>
            {
                Interlocked.Increment(ref bmtNotifications);
                throw new IOException("BMT progress unavailable");
            };
            int activeSyncOperationCount = 0;
            service.PlaylistPropertySyncStarted += (_, _) =>
                Interlocked.Increment(ref activeSyncOperationCount);
            int completionProgressCount = 0;
            service.PlaylistPropertySyncProgressChanged += (_, request) =>
            {
                if (request.Snapshot.CompletedTableCount == 1
                    && Interlocked.Increment(ref completionProgressCount) == 1)
                {
                    throw new InvalidOperationException("test post-reload completion presentation failure");
                }
            };
            service.PlaylistPropertySyncFinished += (_, _) =>
                Interlocked.Decrement(ref activeSyncOperationCount);
            int referenceReplacementPresentationCount = 0;
            service.PlaylistPropertyReferenceTableReplaced += (_, _) =>
                Interlocked.Increment(ref referenceReplacementPresentationCount);
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) => { };

            service.PlaylistPropertySyncResultReported += (_, _) => { };
            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            int summaryRefreshCount = 0;
            service.PlaylistPropertyCatalogChanged += (_, _) => { };
            service.PlaylistPropertySummaryDataRefreshRequested += (_, request) =>
            {
                if (string.Equals(request.Reason, "playlist_property_resync", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref summaryRefreshCount);
                }
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session, dialogs);
            // 確認の表示の失敗は、承認にも取消にも読み替えず、setter から例外を出さない（bindingへ漏らさない）。値と表は変えず、通知は一件だけ出す。
            dialog.is_external_sync = true;
            Assert.IsFalse(dialog.is_external_sync);
            Assert.IsFalse(table.is_external_sync);
            Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
            Assert.AreEqual(1, dialogs.MessageRequests.Count);
            Assert.AreEqual(Resources.Error, dialogs.MessageRequests[0].Caption);
            Assert.AreEqual(MessageBoxImage.Hand, dialogs.MessageRequests[0].Icon);
            failConfirmation = false;
            dialog.is_external_sync = true;
            Assert.AreEqual(2, dialogs.ConfirmationRequests.Count, "失敗の後の切替えは新しい確認を出す。");

            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.AreEqual(0, activeSyncOperationCount);
            File.Delete(headerJsonPath);
            File.Delete(scoreJsonPath);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual(1, referenceReplacementPresentationCount);
            Assert.AreEqual(1, completionProgressCount);
            Assert.AreEqual(0, activeSyncOperationCount);
            Assert.AreEqual(1, summaryRefreshCount);
            Assert.AreEqual(2, bmtNotifications, "通知故障だけでBMTの未完了を作り、次保存で再出力しません。");
            Assert.IsTrue(dialog.is_external_sync);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.IsTrue(
                verify.Table<BMSTable>()
                    .Single(row => row.playlist_id == table.playlist_id)
                    .is_external_sync);
            Assert.AreEqual(
                1,
                verify.Table<BMSTableEntry>()
                    .Count(row => row.playlist_id == table.playlist_id && !row.is_removed));
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }


    /// <summary>
    /// 同期ON・PageなしでHeaderを変更した単表保存の未分類取得失敗でも属性DB保存と必要BMT出力を終え、保存済み値をdialogVMへ採用してから同期未完了を返します。
    /// Resetと次の明示保存は保存済み基準を使い、一般同期から取得失敗を外す変更でこの失敗を成功へ消す誤りを検出します。
    /// 実Windowのfalse結果と寿命は既存terminal操作の接続・回帰に分担し、ここでは実SQLite・出力・service・dialogVMを観測します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_ExternalSourceFailureAdoptsSavedValuesAndCompletesRequiredOutput()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            Settings settings = TestSettingsFactory.CreateIsolatedSettings();
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            string bmtDirectory = Path.Combine(directory, "bmt");
            File.WriteAllText(header, "{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var output = new CustomFolderOutputSettingsSnapshot();
            var playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmtDirectory }, () => output, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songPath, CustomFolderOutputPhysicalSurface.Empty), settings: settings, uiScheduler: new TestUiScheduler(() => null));
            var library = new TestBmsLibrary(databaseScope, songPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: settings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(new Uri(header).AbsoluteUri));
            table.playlist_id = 7316;
            table.Page_url = null;
            table.is_external_sync = true;
            table.is_bmt_output = true;
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);
            string oldHash = table.data_sha256;
            string failedHeader = Path.Combine(directory, "failed-header.json");
            File.WriteAllText(failedHeader, "{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"bad%00-data.json\"}");
            var service = new PlaylistPropertySaveService(() => playlist, () => library, () => null, () => output);
            var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            service.PlaylistPropertySyncStarted += (_, _) => { };
            service.PlaylistPropertySyncProgressChanged += (_, _) => { };
            service.PlaylistPropertySyncFinished += (_, _) => { };

            service.PlaylistPropertyCatalogChanged += (_, _) => { };
            service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
            PlaylistPropertyExternalSyncFailedEventArgs? reportedFailure = null;
            string? symbolAtFailure = null;
            bool enabledAtFailure = false;
            service.PlaylistPropertyExternalSyncFailed += (_, failure) =>
            {
                reportedFailure = failure;
                symbolAtFailure = dialog?.symbol;
                enabledAtFailure = dialog?.is_external_sync == true;
                Assert.AreEqual(new Uri(failedHeader), dialog?.Header_url);
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session, dialogs);
            dialog.symbol = "Saved";
            dialog.is_external_sync = false;
            dialog.Header_url = new Uri(failedHeader);
            dialog.is_external_sync = true;
            Assert.AreEqual(2, dialogs.ConfirmationRequests.Count, "無効化と再有効化の切替えごとに確認が一件ずつ届く。");
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => dialog.SaveAndApplyAsync());
            Assert.IsNotNull(reportedFailure);
            Assert.IsInstanceOfType<ArgumentException>(reportedFailure.Exception);
            Assert.AreEqual("Saved", symbolAtFailure);
            Assert.IsTrue(enabledAtFailure);
            Assert.AreEqual("Saved", session.OriginalValues.Symbol);
            Assert.IsTrue(session.OriginalValues.IsExternalSync);
            Assert.IsNull(session.PendingOutput);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.AreEqual(oldHash, table.data_sha256);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                BMSTable saved = db.Table<BMSTable>().Single();
                Assert.AreEqual("Saved", saved.symbol);
                Assert.AreEqual(new Uri(failedHeader), saved.Header_url);
                Assert.IsTrue(saved.is_external_sync);
                Assert.AreEqual(oldHash, saved.data_sha256);
                Assert.AreEqual("Before", db.Table<LR2SongDBExtended.playlist_entry>().Single().title);
            }
            string bmtPath = Directory.GetFiles(bmtDirectory, "*.bmt").Single();
            Newtonsoft.Json.Linq.JObject exported = BeatorajaBmtTableImportService.ReadCachedTableData(bmtPath);
            StringAssert.Contains(exported.ToString(), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            bool accepted = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsTrue(accepted);
            dialog.symbol = "Unsaved";
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.ResetPropertiesAsync());
            Assert.AreEqual("Saved", dialog.symbol);
            Assert.AreEqual(new Uri(failedHeader), dialog.Header_url);
            Assert.IsTrue(dialog.is_external_sync);
            // 次保存では保存済みの外部同期有効化を再実行せず、入力が壊れたままでも新しい属性を保存できる。
            dialog.symbol = "Next";
            reportedFailure = null;
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.IsNull(reportedFailure);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly();
            Assert.AreEqual("Next", verify.Table<BMSTable>().Single().symbol);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>保存済み階層名の単一名化後にROOT実出力とDB親を揃え、保存原値と旧階層ファイルを維持します。通常型の基点階層を流用する誤りも検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsRootOutputRowsUnderTableDirectory()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootCustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "RootFolderTable",
                symbol = "RFT",
                output_dir = @"RootFolder\Table",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Root Folder")
                ],
                Folder_order = ["Root Folder"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.CommitBMSTableHeaderToDB(table);
            string oldFile = Path.Combine(rootOutputBaseDir, "RootFolder", "Table", "sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile) ?? throw new AssertFailedException());
            File.WriteAllText(oldFile, "legacy output");
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            Assert.AreEqual("legacy output", File.ReadAllText(oldFile));

            string outputPath = Path.Combine(rootOutputBaseDir, "RootFolderTable", "0001.lr2folder");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(@"RootFolder\Table", verify.ExecuteScalar<string>("SELECT output_dir FROM playlist;"));
            LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("Root Folder", folder.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(outputPath)),
                folder.parent);
            LR2SongDB.folder parentFolder = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(Path.GetDirectoryName(outputPath)));
            Assert.AreEqual(1, parentFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentFolder.parent);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>指定maskからクリア・DJ・ランダム・各sortを階層出力し、ASSIST/EASYの生成SQLを実行してoption bitとNULLの分類を確認します。分類混同とファイル・DB階層の欠落を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesHierarchicalRandomAndSortFolders()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
                | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder
                | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder
                | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder
                | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder;
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "Stella",
                symbol = "ST",
                Output_dir = "Stella",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "st0"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "st1")
                ],
                Folder_order = ["st0", "st1"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "Stella");
            string allText = ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder"));
            StringAssert.Contains(allText, "#TITLE Stella ALL");
            string st0Text = ReadShiftJisText(Path.Combine(outputDir, "0001.lr2folder"));
            StringAssert.Contains(st0Text, "#TITLE st0");
            string randomText = ReadShiftJisText(Path.Combine(outputDir, "0003.lr2folder"));
            StringAssert.Contains(randomText, "#TITLE Stella ALL RANDOM");
            StringAssert.Contains(randomText, "#MAXTRACKS 1");
            StringAssert.Contains(randomText, "ORDER BY random()");

            string noPlayText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"));
            StringAssert.Contains(noPlayText, "#TITLE Stella ALL NO PLAY");
            StringAssert.Contains(noPlayText, "score.clear IS NULL");
            string noPlayRandomText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0003.lr2folder"));
            StringAssert.Contains(noPlayRandomText, "#TITLE Stella ALL NO PLAY RANDOM");
            string assistText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "2 ASSIST", "0000.lr2folder"));
            StringAssert.Contains(assistText, "score.clear = 2");
            StringAssert.Contains(assistText, "NOT (score.clear = 2");
            StringAssert.Contains(assistText, "(IFNULL(score.op_history, 0) & 8) != 0");
            Assert.IsFalse(assistText.Contains("16777216"));
            Assert.IsFalse(assistText.Contains("score.rank"));
            string easyText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "3 EASY", "0000.lr2folder"));
            StringAssert.Contains(easyText, "score.clear = 2");
            StringAssert.Contains(easyText, "(IFNULL(score.op_history, 0) & 8) != 0");
            Assert.IsFalse(easyText.Contains("16777216"));
            Assert.IsFalse(easyText.Contains("score.rank"));
            AssertClearFolderCommandMatchesAssistAndEasyRows(databaseScope, songDbPath, assistText, easyText);
            string fcText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "6 FC", "0000.lr2folder"));
            StringAssert.Contains(fcText, "(IFNULL(score.op_history, 0) & 16) = 0");
            string paText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "7 P.A", "0000.lr2folder"));
            StringAssert.Contains(paText, "(IFNULL(score.op_history, 0) & 16) != 0");
            string underAText = ReadShiftJisText(Path.Combine(outputDir, "DJ LEVEL", "UNDER A", "0000.lr2folder"));
            StringAssert.Contains(underAText, "score.rank < 6 OR score.rank IS NULL");
            string bpmSortText = ReadShiftJisText(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder"));
            StringAssert.Contains(bpmSortText, "FROM chart_info");
            StringAssert.Contains(bpmSortText, "mainbpm");
            StringAssert.Contains(bpmSortText, "IS NULL ASC");
            string playCountSortText = ReadShiftJisText(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder"));
            StringAssert.Contains(playCountSortText, "score.playcount IS NULL ASC");
            StringAssert.Contains(playCountSortText, "score.playcount DESC");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder")));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(Path.Combine(outputDir, "CLEAR FOLDER"))));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>新規生成と既存ファイルの明示再出力で実COMMANDを実行し、BP0・20の後へ負値・NULL・scoreなしを置きます。ASSISTの分類とNO PLAY、対象所属・削除除外、BP以外の負値を含む降順と元スコアを保ち、欠損同士の順は固定しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CustomFolderOutput_BpMissingValuesFollowNonnegativeValuesOnCreationAndExplicitReoutput()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null;
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7392,
                name = "BpMissing",
                symbol = "BP",
                Output_dir = "BpMissing",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder,
                entries = Enumerable.Range(1, 7).Select(number => new BMSTableEntry
                {
                    md5 = number.ToString("x32"),
                    title = "Song " + number,
                    folder = number == 6 ? "Other" : "Group",
                    is_removed = number == 7
                }).ToArray(),
                Folder_order = ["Group", "Other"]
            };
            const string RawScoreSnapshotSql = "SELECT group_concat(hash || ':' || quote(minbp) || ':' || quote(rate) || ':' || quote(playcount) || ':' || quote(clear) || ':' || quote(op_history) || ':' || quote(rank), ';') FROM (SELECT * FROM score ORDER BY hash);";
            string originalScores;
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                // NULLをCLRの数値既定値へ変換せず、外部scoreの保存値のままCOMMANDへ渡す。
                db.RunInTransaction(() =>
                {
                    db.Execute("CREATE TABLE score(hash TEXT PRIMARY KEY, clear INTEGER, rank INTEGER, op_history INTEGER, minbp INTEGER, rate REAL, playcount INTEGER);");
                    foreach (int number in Enumerable.Range(1, 8))
                    {
                        string hash = number.ToString("x32");
                        db.Execute("INSERT INTO song(hash, title, path) VALUES (?, ?, ?);", hash, "Song " + number, hash + ".bms");
                        if (number == 5)
                        {
                            continue;
                        }
                        int? bp = number switch { 1 => -1, 2 => 0, 3 => 20, 4 => null, _ => -number };
                        double rate = number switch { 1 => -10, 2 => 80, 3 => 90, 4 => -5, _ => 0 };
                        int playCount = number switch { 1 => -2, 2 => 1, 3 => 8, 4 => -1, _ => 0 };
                        db.Execute("INSERT INTO score(hash, clear, rank, op_history, minbp, rate, playcount) VALUES (?, 2, 0, ?, ?, ?, ?);",
                            hash, ClearTypeStorageConverter.OptionHistoryAssist, bp, rate, playCount);
                    }
                });
                originalScores = db.ExecuteScalar<string>(RawScoreSnapshotSql);
            }
            playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            string outputDir = Path.Combine(testSettings.LR2CustomFolderOutputBaseDir, table.Output_dir);
            string bpPath = Path.Combine(outputDir, "BP SORT", "0000.lr2folder");
            string assistPath = Path.Combine(outputDir, "CLEAR FOLDER", "2 ASSIST", "0000.lr2folder");

            foreach (bool explicitReoutput in new[] { false, true })
            {
                if (explicitReoutput)
                {
                    // 既存ファイルも明示再出力で置換する。新規ファイルの生成だけでは成功しない入力とする。
                    File.WriteAllText(bpPath, "#TITLE stale BP\n#COMMAND 0", Encoding.GetEncoding("shift_jis"));
                    File.WriteAllText(assistPath, "#TITLE stale ASSIST\n#COMMAND 0", Encoding.GetEncoding("shift_jis"));
                    playlist.ReOutputCustomFolder(table);
                }
                else
                {
                    playlist.ReOutputCustomFolderAndCommitToDB(table);
                }

                using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
                foreach ((string path, int[] missingNumbers) in new[] { (bpPath, new[] { 1, 4, 5 }), (assistPath, new[] { 1, 4 }) })
                {
                    string command = ReadCustomFolderCommand(ReadShiftJisText(path));
                    string[] hashes = verify.Query<LR2SongDB.song>("SELECT song.* FROM song LEFT JOIN score ON song.hash = score.hash WHERE " + command)
                        .Select(song => song.hash).ToArray();
                    CollectionAssert.AreEqual(new[] { 2, 3 }.Select(number => number.ToString("x32")).ToArray(), hashes.Take(2).ToArray(), path);
                    CollectionAssert.AreEquivalent(missingNumbers.Select(number => number.ToString("x32")).ToArray(), hashes.Skip(2).ToArray(), path);
                }
                AssertCustomFolderCommandHashOrder(verify,
                    ReadCustomFolderCommand(ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"))), [5]);
                AssertCustomFolderCommandHashOrder(verify,
                    ReadCustomFolderCommand(ReadShiftJisText(Path.Combine(outputDir, "DJ LEVEL", "UNDER A", "0000.lr2folder"))), [3, 2, 4, 1, 5]);
                AssertCustomFolderCommandHashOrder(verify,
                    ReadCustomFolderCommand(ReadShiftJisText(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder"))), [3, 2, 4, 1, 5]);
                Assert.AreEqual(originalScores, verify.ExecuteScalar<string>(RawScoreSnapshotSql), "出力はNULL・負値を含む保存済みscoreを変更しません。");
            }
        }
        finally
        {
            playlist?.RequestShutdown("bp-sort-fixture");
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>旧DBのNULL/0を生存、1を削除済みとして公開再出力し、実COMMANDの抽出とLEVEL/ADDDATEの値順を検証します。全保存によるNULLの0化を介さず、全体・folder指定・型別のSQL境界を通します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, LR2SongDBExtended.playlist.EntryUnitType.File)]
    [DataRow(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, LR2SongDBExtended.playlist.EntryUnitType.Folder)]
    [DataRow(LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE, LR2SongDBExtended.playlist.EntryUnitType.File)]
    [DataRow(LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE, LR2SongDBExtended.playlist.EntryUnitType.Folder)]
    public void ReOutputCustomFolder_RawNullLiveEntriesRemainIncludedAndUsePersistedSortValues(
        LR2SongDBExtended.playlist.CustomFolderSortType sortType,
        LR2SongDBExtended.playlist.EntryUnitType entryType)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder
                | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder
                | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder;
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2ScoreDB.score>();
                db.CreateTable<LR2SongDBExtended.ir_score>();
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, output_dir, folder_order, folder_sort_key, folder_sort_ascending, entry_type, ignore_folder_output) VALUES (7390, 'Legacy', 'L', 'Legacy', ?, ?, ?, ?, ?);",
                    "[\"Group\"]", sortType, sortType == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, entryType,
                    LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes);
                // NULLはbool変換を通さず旧行の値を保持する。levelとadddateは曲ごとに異なり、NULLをsort値と誤読すると順序が変わる。
                foreach ((int number, string removed, int level, int year, string title) in new[]
                {
                    (1, "NULL", 3, 2023, "Alpha NULL"),
                    (2, "0", 1, 2021, "Alpha ZERO"),
                    (3, "1", 0, 2025, "Alpha REMOVED"),
                    (4, "NULL", 4, 2024, "2 NULL"),
                    (5, "0", 2, 2022, "2 ZERO"),
                    (6, "1", 0, 2026, "2 REMOVED")
                })
                {
                    string hash = number.ToString("x32");
                    db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, adddate, is_removed) VALUES (7390, ?, ?, 'Group', ?, ?, " + removed + ");",
                        hash, title, level, new DateTime(year, 1, 1));
                    db.Execute("INSERT INTO song (hash, title, folder, path, longnote, judge) VALUES (?, ?, ?, ?, 1, 2);", hash, title, "Package" + number, hash + ".bms");
                    db.Execute("INSERT INTO score (hash, clear, playcount) VALUES (?, 3, ?);", hash, number);
                }
                Assert.AreEqual(2L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = 7390 AND is_removed IS NULL;"));
            }
            BMSTable table = new PlaylistPersistenceRepository(databaseScope, songDbPath).LoadPlaylistHeaders().Single();
            Assert.IsFalse(table.ArePlaylistEntriesLoaded);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolder(table);

            Assert.IsTrue(table.ArePlaylistEntriesLoaded);
            string outputDir = Path.Combine(outputBaseDir, "Legacy");
            var commands = Directory.GetFiles(outputDir, "*.lr2folder", SearchOption.AllDirectories)
                .Select(ReadShiftJisText)
                .ToDictionary(text => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Single(line => line.StartsWith("#TITLE ", StringComparison.Ordinal)).Substring("#TITLE ".Length), ReadCustomFolderCommand);
            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(songDbPath);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = 7390 AND is_removed IS NULL;"));
            int[] expectedOrder = sortType == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL ? [2, 5, 1, 4] : [4, 1, 5, 2];
            AssertCustomFolderCommandHashOrder(verify, commands["Legacy ALL"], expectedOrder);
            AssertCustomFolderCommandHashOrder(verify, commands["Group"], expectedOrder);
            AssertCustomFolderCommandHashOrder(verify, commands["NEW SONGS"], [4, 1, 5, 2]);
            foreach (string title in new[] { "ALL LONG NOTES", "ALL NORMAL JUDGES", "MY BEST", "UNSENT SONGS" })
            {
                AssertCustomFolderCommandMatches(verify, commands[title], [1, 2, 4, 5]);
            }
            AssertCustomFolderCommandMatches(verify, commands["A.B.C.D."], [1, 2]);
            AssertCustomFolderCommandMatches(verify, commands["OTHERS"], [4, 5]);
            AssertCustomFolderCommandMatches(verify, commands["REMOVED SONGS"], [3, 6]);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>名前なし所属のLEVEL/ADDDATE両方向と受理済み空白名を実保存・出力し、同hashの別所属の値や別所属専用曲の混入を検出します。空白名の保存値を保ち、名前なしLEVEL昇順は読取り接続の未指定走査順を反転しても同じ所属内順を保つことと、方向変更保存後に同じファイルのCOMMANDが降順へ変わることを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow("", LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, true)]
    [DataRow("", LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, false)]
    [DataRow("", LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE, true)]
    [DataRow("", LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE, false)]
    [DataRow("   ", LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, true)]
    public void ReOutputCustomFolderAndCommitToDB_UserFolderSortUsesOwnMembershipValues(
        string folder,
        LR2SongDBExtended.playlist.CustomFolderSortType sortType,
        bool ascending)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7391,
                name = "MembershipSort",
                symbol = "MS",
                Output_dir = "MembershipSort",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
                folder_sort_key = sortType,
                folder_sort_ascending = ascending,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                // 別所属を先に保存し、同hashのlevel/adddateが対象所属と逆の順序になる入力を作る。
                entries = new[]
                {
                    (Number: 1, Folder: "Other", Level: 9, Year: 2019),
                    (Number: 2, Folder: "Other", Level: 1, Year: 2011),
                    (Number: 3, Folder: "Other", Level: 0, Year: 2010),
                    (Number: 1, Folder: folder, Level: 3, Year: 2013),
                    (Number: 2, Folder: folder, Level: 6, Year: 2016)
                }.Select(value => new BMSTableEntry
                {
                    md5 = value.Number.ToString("x32"),
                    title = "Song " + value.Number,
                    folder = value.Folder,
                    level = value.Level,
                    adddate = new DateTime(value.Year, 1, 1)
                }).ToArray(),
                Folder_order = [folder, "Other"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2ScoreDB.score>();
                    foreach (int number in new[] { 1, 2, 3 })
                    {
                        string hash = number.ToString("x32");
                        db.Execute("INSERT INTO song (hash, title, path) VALUES (?, ?, ?);", hash, "Song " + number, hash + ".bms");
                    }
                });
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            // 空文字・空白名の表示titleは表名となるため、対象先頭の実ファイルを読む。
            string outputPath = Path.Combine(outputBaseDir, "MembershipSort", "0000.lr2folder");
            string command = ReadCustomFolderCommand(ReadShiftJisText(outputPath));
            using (LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath))
            {
                CollectionAssert.AreEqual(new[] { folder, folder },
                    verify.Query<LR2SongDBExtended.playlist_entry>("SELECT folder FROM playlist_entry WHERE playlist_id = ? AND folder <> ?;", table.playlist_id, "Other")
                        .Select(entry => entry.folder).ToArray());
                AssertCustomFolderCommandHashOrder(verify, command, ascending ? [1, 2] : [2, 1]);
                if (folder.Length == 0 && sortType == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL && ascending)
                {
                    verify.Execute("PRAGMA reverse_unordered_selects = ON;");
                    AssertCustomFolderCommandHashOrder(verify, command, [1, 2]);
                }
            }
            if (folder.Length == 0 && sortType == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL && ascending)
            {
                table.folder_sort_ascending = false;

                playlist.ReOutputCustomFolderAndCommitToDB(table);

                string regeneratedCommand = ReadCustomFolderCommand(ReadShiftJisText(outputPath));
                using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
                AssertCustomFolderCommandHashOrder(verify, regeneratedCommand, [2, 1]);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>導入済み履歴表を参照する最終プレイ降順と欠損末尾のSQLを実ファイル・DBへ出力し、プレイ回数sortを代用しません。異なる日時・回数への置換を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesLastPlaySortFolderWhenSchemaInstalled()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
            var table = new BMSTable
            {
                playlist_id = 7306,
                name = "LastPlay",
                symbol = "LP",
                Output_dir = "LastPlay",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, scoreDbPath, null, () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings), () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LastPlay");
            string lastPlayText = ReadShiftJisText(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder"));
            StringAssert.Contains(lastPlayText, "#TITLE LastPlay ALL");
            StringAssert.Contains(lastPlayText, "ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC");
            StringAssert.Contains(lastPlayText, "#INFORMATION_A Sort: LAST PLAY DESC");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder"));
            StringAssert.Contains(row.command, "bms_lr2_last_play");
            StringAssert.Contains(row.command, "ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>履歴スキーマ未導入でも選択された最終プレイsortのファイルとDB行を生成します。schema状態を出力禁止へ転用する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_WritesLastPlaySortWhenSchemaNotInstalled()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            string scoreDbPath = CreateBaseScoreDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7307,
                name = "MissingLastPlaySchema",
                symbol = "ML",
                Output_dir = "MissingLastPlaySchema",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, scoreDbPath, null, () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings), () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_last_play_schema_missing");

            string outputDir = Path.Combine(outputBaseDir, "MissingLastPlaySchema");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>最終プレイsortを除外へ変え、生成済みと余分なファイル・DB行・空階層を除去し通常出力を保持します。種類を切った後も旧投影を残す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesLastPlaySortWhenOutputBitTurnsOff()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
            var table = new BMSTable
            {
                playlist_id = 7308,
                name = "LastPlayPrune",
                symbol = "LPP",
                Output_dir = "LastPlayPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, scoreDbPath, null, () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings), () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LastPlayPrune");
            string lastPlayDir = Path.Combine(outputDir, "LAST PLAY SORT");
            string generatedPath = Path.Combine(lastPlayDir, "0000.lr2folder");
            string stalePath = Path.Combine(lastPlayDir, "stale.lr2folder");
            Assert.IsTrue(File.Exists(generatedPath));
            File.WriteAllText(stalePath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
            {
                seed.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2, command = "song.hash IS NOT NULL ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC" }, typeof(LR2SongDB.folder));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsFalse(File.Exists(generatedPath));
            Assert.IsFalse(File.Exists(stalePath));
            Assert.IsFalse(Directory.Exists(lastPlayDir));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "0000.lr2folder")));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path LIKE ?;", lastPlayDir + "%"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>全曲・利用者フォルダ・レベルを指定して実出力し、連番上の通常版を先、ランダム版を後へ配置します。LR2表示で通常版とランダム版が交錯する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesRootRandomFoldersAfterNormalFolders()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder;
            BMSTableEntry first = CreateEntryWithLevel("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1);
            BMSTableEntry second = CreateEntryWithLevel("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2);
            var table = new BMSTable
            {
                playlist_id = 7308,
                name = "RootRandomOrder",
                symbol = "RRO",
                Output_dir = "RootRandomOrder",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries = [first, second],
                Folder_order = ["1", "2"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "RootRandomOrder");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder")), "#TITLE RootRandomOrder ALL");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0001.lr2folder")), "#TITLE 1");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0002.lr2folder")), "#TITLE 2");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0003.lr2folder")), "#TITLE LEVEL 1");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0004.lr2folder")), "#TITLE LEVEL 2");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0005.lr2folder")), "#TITLE RootRandomOrder ALL RANDOM");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0008.lr2folder")), "#TITLE LEVEL 1 RANDOM");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>全曲だけを対象範囲にした場合は全体とそのクリア区分を出力し、利用者フォルダ分を追加しません。対象範囲のbitを相互依存させる誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_AllSongsScopeDoesNotRequireUserFolder()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7311,
                name = "AllScopeOnly",
                symbol = "ASO",
                Output_dir = "AllScopeOnly",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "AllScopeOnly");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder")), "#TITLE AllScopeOnly ALL");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "0001.lr2folder")));
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder")), "#TITLE AllScopeOnly ALL NO PLAY");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0001.lr2folder")));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>利用者フォルダだけを対象範囲にして通常・クリア区分を生成し、全曲分を混ぜません。全曲bitを無視する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_UserFolderScopeDoesNotWriteAllSongs()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7312,
                name = "FolderScopeOnly",
                symbol = "FSO",
                Output_dir = "FolderScopeOnly",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "FolderScopeOnly");
            string folderText = ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder"));
            StringAssert.Contains(folderText, "#TITLE Folder A");
            Assert.IsFalse(folderText.Contains("#TITLE FolderScopeOnly ALL"));
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "0001.lr2folder")));
            string clearText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"));
            StringAssert.Contains(clearText, "#TITLE Folder A NO PLAY");
            Assert.IsFalse(clearText.Contains("#TITLE FolderScopeOnly ALL NO PLAY"));
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0001.lr2folder")));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存済み旧mask 0x7Fを現在の全除外へ拡張せず、後から追加されたsort種類を実出力します。旧値の解釈を変更して利用者の出力を失う誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_DoesNotExpandOldAllFoldersMaskToNewTypes()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "LegacyDisabled",
                symbol = "LD",
                Output_dir = "LegacyDisabled",
                ignore_folder_output = (LR2SongDBExtended.playlist.CustomFolderType)0x7F,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LegacyDisabled");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BP SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同じ管理先の余分なlr2folderを実ファイルとDBから除去し、必要出力を再生成します。物理整理とDB整理の片方だけを行う誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesExtraLr2FolderRowsInSameOutputDirectory()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7306,
                name = "SameOutputPrune",
                symbol = "SOP",
                Output_dir = "SameOutputPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string outputDir = Path.Combine(outputBaseDir, "SameOutputPrune");
            string stalePath = Path.Combine(outputDir, "external.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(stalePath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string expectedPath = Path.Combine(outputDir, "0001.lr2folder");
            Assert.IsTrue(File.Exists(expectedPath));
            Assert.IsFalse(File.Exists(stalePath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", expectedPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>物理クリア階層を先に失った状態で種類を除外し、DBだけに残るファイル・祖先行も除去します。ファイル列挙だけに頼る整理漏れを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesDbOnlyStaleHierarchicalRowsInSameOutputDirectory()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder;
            var table = new BMSTable
            {
                playlist_id = 7307,
                name = "HierarchyPrune",
                symbol = "HP",
                Output_dir = "HierarchyPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("ffffffffffffffffffffffffffffffff", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "HierarchyPrune");
            string clearDir = Path.Combine(outputDir, "CLEAR FOLDER");
            string noPlayDir = Path.Combine(clearDir, "0 NO PLAY");
            string noPlayFile = Path.Combine(noPlayDir, "0000.lr2folder");
            using (LR2SongDBExtended verifyInitial = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", noPlayFile));
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
            }
            Directory.Delete(clearDir, recursive: true);
            Assert.IsFalse(Directory.Exists(clearDir));

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsFalse(Directory.Exists(clearDir));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", noPlayFile));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Path.Combine(outputDir, "0000.lr2folder")));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>既存の古い本文を一括出力で書き換え、実更新時刻とタイトルをDBへ同期します。存在するファイルを現行と誤認する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_RewritesExistingLr2FolderFiles()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string outputDir = Path.Combine(outputBaseDir, "BulkRewrite");
            string outputFile = Path.Combine(outputDir, "0000.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputFile, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            DateTime oldTimestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(outputFile, oldTimestamp);
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = testSettings.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7612,
                name = "BulkRewrite",
                symbol = "BR",
                Output_dir = "BulkRewrite",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot)), null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), getOutputSettings, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_bulk_rewrite");

            string text = ReadShiftJisText(outputFile);
            Assert.AreNotEqual(oldTimestamp, File.GetLastWriteTimeUtc(outputFile));
            Assert.IsTrue(text.IndexOf("#TITLE stale", StringComparison.Ordinal) < 0);
            StringAssert.Contains(text, "#TITLE BulkRewrite ALL");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == outputFile);
            Assert.AreEqual(File.GetLastWriteTimeUtc(outputFile).ToUnixtime(), row.date);
            Assert.AreEqual("BulkRewrite ALL", row.title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実カスタムフォルダ同期の進捗を、出力段階・DB同期段階・生の表名の事実で届け、翻訳済み文言を持たせません。全体song DB同期の段階を流用して処理内容を誤って案内する接続を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_ProgressReportsStageFactsWithRawTableName()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7616,
                name = "ProgressLabel",
                symbol = "PL",
                Output_dir = "ProgressLabel",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            BMSPlaylist playlist = CreatePlaylist(databaseScope, songDbPath, synchronization, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot)), settings: testSettings, modelUiScheduler: new TestUiScheduler(() => null));
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            var progressDetails = new List<CustomFolderProgressDetail>();

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB(
                [table],
                "test_progress_label",
                (_, _, detail) => progressDetails.Add(detail));

            // 段階は翻訳済み文言ではなく段階の事実で渡し、表名は生の名前のまま渡す。
            Assert.IsTrue(progressDetails.Any(detail => detail.Stage == CustomFolderProgressStage.DbSync));
            Assert.IsTrue(progressDetails.Any(detail => detail.Stage == CustomFolderProgressStage.Output));
            Assert.IsTrue(progressDetails.Any(detail => detail.Stage == CustomFolderProgressStage.TableName && detail.TableName == "ProgressLabel"));
            Assert.AreEqual(CustomFolderProgressStage.DbSync, progressDetails[^1].Stage);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>表ルート登録と対応DB行を欠落させて実reloadし、注入されたルートを補完して現行ファイルの時刻を保持します。登録補完後のDB修復漏れと不要再出力を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_RepairsRootOutputDirectoryRowWhenJukeboxRootWasMissing()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string settingsRootOutputBaseDir = Path.Combine(tempDirectory, "SettingsRootOutput");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootOutput");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDirRootType = settingsRootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7619,
                name = "ReloadRootStatusCurrent",
                symbol = "RRSC",
                Output_dir = "ReloadRootStatusCurrent",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    entry.playlist_id = table.playlist_id;
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput"),
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, configOwner, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, synchronization, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_reload_root_status_current");
            string outputDirectory = Path.Combine(providerRootOutputBaseDir, "ReloadRootStatusCurrent");
            string outputFile = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime outputFileTimestamp = File.GetLastWriteTimeUtc(outputFile);
            string outputDirectoryRowPath = Lr2FolderPath.ToFolderPath(outputDirectory);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.Execute("DELETE FROM folder WHERE path = ?;", outputDirectoryRowPath);
            }
            config.SetBMSSearchDirectories([bmsRoot]);
            config.Save(config.ConfigFilePath);
            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);

            LR2Config persistedConfig = TestLr2ConfigFactory.Load(config.ConfigFilePath);
            CollectionAssert.Contains(persistedConfig.GetBMSSearchDirectories(), outputDirectory);
            CollectionAssert.DoesNotContain(persistedConfig.GetBMSSearchDirectories(), settingsRootOutputBaseDir);
            Assert.AreEqual(outputFileTimestamp, File.GetLastWriteTimeUtc(outputFile));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder outputDirectoryRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputDirectoryRowPath);
            Assert.AreEqual(1, outputDirectoryRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputDirectoryRow.parent);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：出力状態が現行の通常・追加通常の表について、再読込時の必要出力判定が出力基点の folder 行を完全一致で確認する。
    /// 行の date が基点の実際の更新時刻（Unix秒）と異なる場合だけ、その基点の配下の表を再確認し、一回の同期で date を実際の時刻へ揃える。
    /// 本文と時刻の変わらない .lr2folder は書き直さず、既存の adddate を保持する。基点の時刻が変わっても行が一致していれば戻さず、
    /// 行がない基点の表も戻さない。修復後の再判定は0件で収束する。
    /// 走査対象外の追加先の取りこぼし、出力状態の署名だけによる判定、全表や毎回の再生成、adddate の再生成を検出する。
    /// </summary>
    /// <remarks>DataRow は date を古くする基点。他方の基点では行の欠落時に戻さないことを確認する。ROOT 型は既存の ROOT 行ケースが担う。</remarks>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReloadTables_StatusCurrentOutputBaseRowFollowsActualMtimeOnlyUnderStaleBase(bool staleAdditionalBase)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null;
        var acceptedWork = new System.Collections.Concurrent.ConcurrentQueue<Task>();
        Exception? primaryFailure = null;
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string normalBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string additionalBase = Path.Combine(tempDirectory, "Additional");
            Directory.CreateDirectory(normalBase);
            Directory.CreateDirectory(additionalBase);
            string additionalSerialized = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalBase]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = normalBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = additionalSerialized;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = normalBase,
                LR2CustomFolderOutputBaseDirRootType = testSettings.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = additionalSerialized
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSTable CreateBaseRowTable(int playlistId, string name, string? baseName) => new()
            {
                playlist_id = playlistId,
                name = name,
                symbol = name[..2],
                Output_dir = name,
                custom_folder_output_base_name = baseName,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry(new string(playlistId % 2 == 0 ? 'a' : 'b', 32), "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            BMSTable normalTable = CreateBaseRowTable(7630, "NormalBaseRow", null);
            BMSTable additionalTable = CreateBaseRowTable(7631, "AdditionalBaseRow", "Additional");
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                foreach (BMSTable table in new[] { normalTable, additionalTable })
                {
                    db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries)
                    {
                        entry.playlist_id = table.playlist_id;
                        db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                }
            }
            string[] EnumerateOutputFiles() => [.. new[] { normalBase, additionalBase }
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.lr2folder", SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            // 修復の前後で古い一覧を使わないよう、呼ばれた時点の実ファイルを返します。
            synchronization.PhysicalSurfaceFactory = () => CustomFolderOutputPhysicalSurface.FromEntries(
                EnumerateOutputFiles().Select(path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path))),
                discoveryComplete: true);
            playlist = new TestBmsPlaylist(databaseScope, songDbPath, configOwner, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, synchronization, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { normalTable, additionalTable })
            };
            var repairProgress = new List<PlaylistSyncProgressSnapshot>();
            playlist.CustomFolderOutputRepairProgressReporter = repairProgress.Add;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                acceptedWork.Enqueue(Task.Run(work));
                return true;
            };
            TestBmsPlaylist reloadTarget = playlist;
            async Task ReloadAsync()
            {
                repairProgress.Clear();
                await reloadTarget.ReloadTablesAsync(exportBeatorajaBmt: false);
                // 任意の補完workを次の段階へ持ち越さず、段階ごとの観測を独立させます。
                await Task.WhenAll(acceptedWork.ToArray());
            }

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([normalTable, additionalTable], "test_seed_output_base_row");
            // 出力直後の状態保存と再読込時の署名を揃え、以後の段階を「出力状態が現行」から始めます。
            await ReloadAsync();

            string staleBase = staleAdditionalBase ? additionalBase : normalBase;
            string missingRowBase = staleAdditionalBase ? normalBase : additionalBase;
            string staleTableName = staleAdditionalBase ? additionalTable.name : normalTable.name;
            string staleTableDirectory = Path.Combine(staleBase, staleTableName);
            string staleBaseRowPath = Lr2FolderPath.ToFolderPath(staleBase);
            string missingBaseRowPath = Lr2FolderPath.ToFolderPath(missingRowBase);
            string staleTableRowPath = Lr2FolderPath.ToFolderPath(staleTableDirectory);
            string[] outputFiles = EnumerateOutputFiles();
            string staleFileRowPath = outputFiles.First(path => path.StartsWith(staleTableDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(outputFiles.Any(path => path.StartsWith(missingRowBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            var firstTime = new DateTime(2025, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc);
            var secondTime = new DateTime(2025, 2, 3, 4, 5, 6, 500, DateTimeKind.Utc);
            const int baseAddDate = 111111;
            const int tableAddDate = 222222;
            const int fileAddDate = 333333;
            Directory.SetLastWriteTimeUtc(staleBase, firstTime);
            Assert.AreEqual(firstTime, Directory.GetLastWriteTimeUtc(staleBase));
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                Assert.AreEqual(1, db.Execute("UPDATE folder SET date = ?, adddate = ? WHERE path = ?;", firstTime.ToUnixtime(), baseAddDate, staleBaseRowPath));
                Assert.AreEqual(1, db.Execute("UPDATE folder SET adddate = ? WHERE path = ?;", tableAddDate, staleTableRowPath));
                Assert.AreEqual(1, db.Execute("UPDATE folder SET adddate = ? WHERE path = ?;", fileAddDate, staleFileRowPath));
                Assert.AreEqual(1, db.Execute("DELETE FROM folder WHERE path = ?;", missingBaseRowPath));
            }
            Dictionary<string, (string Body, long Ticks)> CaptureFiles() => EnumerateOutputFiles().ToDictionary(
                path => path,
                path => (Convert.ToHexString(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path).Ticks),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (string Body, long Ticks)> filesBefore = CaptureFiles();
            void AssertFilesUnchanged()
            {
                Dictionary<string, (string Body, long Ticks)> current = CaptureFiles();
                CollectionAssert.AreEquivalent(filesBefore.Keys, current.Keys);
                foreach (KeyValuePair<string, (string Body, long Ticks)> file in filesBefore)
                {
                    Assert.AreEqual(file.Value, current[file.Key], file.Key);
                }
            }
            List<LR2SongDB.folder> ReadRows(string path)
            {
                using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
                return db.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ?;", path);
            }
            void AssertNoRepair(int operationsBefore)
            {
                Assert.IsFalse(repairProgress.Any(snapshot => snapshot.IsActive));
                Assert.AreEqual(operationsBefore, synchronization.Operations.Count);
                AssertFilesUnchanged();
                Assert.AreEqual(0, ReadRows(missingBaseRowPath).Count);
            }
            void AssertOnlyStaleBaseTableRepaired(int operationsBefore, DateTime actualBaseTime)
            {
                List<PlaylistSyncProgressSnapshot> active = [.. repairProgress.Where(snapshot => snapshot.IsActive)];
                // 進捗の総数は表以外の段階も数えるため、再生成した表は進捗に現れた表名の集合で特定します。
                CollectionAssert.AreEquivalent(
                    new[] { staleTableName },
                    active.Select(snapshot => snapshot.CurrentTableName)
                        .Where(tableName => !string.IsNullOrEmpty(tableName))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
                CollectionAssert.AreEqual(
                    new[] { "playlist_lr2folder_batch_sync" },
                    synchronization.Operations.Skip(operationsBefore).ToArray());
                Assert.AreEqual(actualBaseTime, Directory.GetLastWriteTimeUtc(staleBase));
                LR2SongDB.folder baseRow = ReadRows(staleBaseRowPath).Single();
                Assert.AreEqual(actualBaseTime.ToUnixtime(), baseRow.date);
                Assert.AreEqual(baseAddDate, baseRow.adddate);
                Assert.AreEqual(tableAddDate, ReadRows(staleTableRowPath).Single().adddate);
                Assert.AreEqual(fileAddDate, ReadRows(staleFileRowPath).Single().adddate);
                AssertFilesUnchanged();
                Assert.AreEqual(0, ReadRows(missingBaseRowPath).Count);
            }

            // 行が基点の時刻（秒未満を切り捨てた値）と一致する基点と、行のない基点は、どちらも再確認へ戻しません。
            int operations = synchronization.Operations.Count;
            await ReloadAsync();
            AssertNoRepair(operations);
            Assert.AreEqual(firstTime.ToUnixtime(), ReadRows(staleBaseRowPath).Single().date);

            // 基点の更新時刻だけが変わった場合は、その基点の表だけを再確認して行を追従させます。
            Directory.SetLastWriteTimeUtc(staleBase, secondTime);
            operations = synchronization.Operations.Count;
            await ReloadAsync();
            AssertOnlyStaleBaseTableRepaired(operations, secondTime);

            // 物理状態が変わらず行の date だけが異なる場合も、行と実際の時刻の比較で検出します。
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                Assert.AreEqual(1, db.Execute("UPDATE folder SET date = ? WHERE path = ?;", secondTime.ToUnixtime() - 1, staleBaseRowPath));
            }
            operations = synchronization.Operations.Count;
            await ReloadAsync();
            AssertOnlyStaleBaseTableRepaired(operations, secondTime);

            // 修復後の再判定は再確認を起こさず収束します。
            operations = synchronization.Operations.Count;
            await ReloadAsync();
            AssertNoRepair(operations);
            Assert.AreEqual(secondTime.ToUnixtime(), ReadRows(staleBaseRowPath).Single().date);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            var workCompletion = Task.WhenAll(acceptedWork.ToArray());
            try { await workCompletion; }
            catch (Exception ex) { if (!ReferenceEquals(ex, primaryFailure)) { cleanupFailures.Add(workCompletion.Exception ?? ex); } }
            if (playlist != null)
            {
                try
                {
                    playlist.RequestShutdown("output-base-row-test-complete");
                    await playlist.WaitForPlaylistMutationIdleAsync();
                }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            try
            {
                if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); }
            }
            catch (Exception ex) { cleanupFailures.Add(ex); }
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException(cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                System.Diagnostics.Trace.WriteLine("output-base-row fixtureのcleanup二次失敗: " + cleanupFailure);
            }
        }
    }

    /// <summary>
    /// 保証：通常・追加通常の基点に出力ファイルを持つ別の表が残る状態で、一つの表を削除するか出力名を改名すると、
    /// 整理と同じ一回の同期で、削除した表ディレクトリを含む基点の folder 行の date を実際の更新時刻（Unix秒）へ揃える。
    /// 続く必要出力の判定は再確認対象を作らず、同期操作と書込みを起こさず、残した表・移行した表のファイルを書き直さない。
    /// 操作前に基点の更新時刻と行の date を実時刻と異なる古い固定値にしておく。
    /// 表ディレクトリの削除後に基点の行を更新せず、次の判定で基点配下の全表を再確認させる誤りを検出する。
    /// </summary>
    /// <remarks>
    /// DataRow は削除・改名と、通常・追加通常の基点。基点配下に出力を持つ表が残らない場合は既存仕様どおりで本ケースの対象外。ROOT型は基点の行を持たないため対象外。
    /// 改名は新先のファイルの生成後に旧ディレクトリを削除するため、判定は実DB・実ファイルの最終状態で行う。
    /// </remarks>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow("remove", false)]
    [DataRow("remove", true)]
    [DataRow("rename", false)]
    [DataRow("rename", true)]
    public async Task TableRemoveOrRename_UpdatesBaseRowToActualMtimeAndNextJudgmentIsClean(string operation, bool additionalBase)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null;
        var acceptedWork = new System.Collections.Concurrent.ConcurrentQueue<Task>();
        Exception? primaryFailure = null;
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string normalBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string extraBase = Path.Combine(tempDirectory, "Additional");
            Directory.CreateDirectory(normalBase);
            Directory.CreateDirectory(extraBase);
            string additionalSerialized = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([extraBase]);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = normalBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = additionalSerialized;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = normalBase,
                LR2CustomFolderOutputBaseDirRootType = testSettings.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = additionalSerialized
            };
            string targetBase = additionalBase ? extraBase : normalBase;
            string? baseName = additionalBase ? "Additional" : null;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSTable CreateTargetBaseTable(int playlistId, string name, char hash) => new()
            {
                playlist_id = playlistId,
                name = name,
                symbol = name[..2],
                Output_dir = name,
                custom_folder_output_base_name = baseName,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry(new string(hash, 32), "Folder A")],
                Folder_order = ["Folder A"]
            };
            BMSTable keptTable = CreateTargetBaseTable(7640, "KeptTable", 'a');
            BMSTable targetTable = CreateTargetBaseTable(7641, "TargetTable", 'b');
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                foreach (BMSTable table in new[] { keptTable, targetTable })
                {
                    db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries)
                    {
                        entry.playlist_id = table.playlist_id;
                        db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                }
            }
            string[] EnumerateOutputFiles() => [.. new[] { normalBase, extraBase }
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.lr2folder", SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            synchronization.PhysicalSurfaceFactory = () => CustomFolderOutputPhysicalSurface.FromEntries(
                EnumerateOutputFiles().Select(path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path))),
                discoveryComplete: true);
            playlist = new TestBmsPlaylist(databaseScope, songDbPath, configOwner, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, synchronization, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { keptTable, targetTable })
            };
            var repairProgress = new List<PlaylistSyncProgressSnapshot>();
            playlist.CustomFolderOutputRepairProgressReporter = repairProgress.Add;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                acceptedWork.Enqueue(Task.Run(work));
                return true;
            };
            TestBmsPlaylist target = playlist;
            async Task ReloadAsync()
            {
                repairProgress.Clear();
                await target.ReloadTablesAsync(exportBeatorajaBmt: false);
                await Task.WhenAll(acceptedWork.ToArray());
            }
            List<LR2SongDB.folder> ReadRows(string path)
            {
                using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
                return db.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ?;", path);
            }

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([keptTable, targetTable], "test_seed_remove_or_rename_base_row");
            await ReloadAsync();
            // 再読込みは正本の表を読み直して置き換えるため、以後の操作は現行の表で行います。
            targetTable = target.BMSTables.Single(table => table.playlist_id == 7641);

            string targetDirectory = Path.Combine(targetBase, targetTable.name);
            string baseRowPath = Lr2FolderPath.ToFolderPath(targetBase);
            string keptDirectory = Path.Combine(targetBase, keptTable.name);
            Assert.IsTrue(Directory.EnumerateFiles(targetDirectory, "*.lr2folder").Any());
            Assert.IsTrue(Directory.EnumerateFiles(keptDirectory, "*.lr2folder").Any(), "同じ基点に出力ファイルを持つ別の表が残る前提です。");

            // 基点の更新時刻と行の date を、操作後の実時刻と異なる古い固定値にそろえます。
            var staleTime = new DateTime(2025, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(targetBase, staleTime);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                Assert.AreEqual(1, db.Execute("UPDATE folder SET date = ? WHERE path = ?;", staleTime.ToUnixtime(), baseRowPath));
            }

            if (operation == "remove")
            {
                playlist.RemoveCustomFolder(targetTable);
                playlist.RemoveBMSTable(targetTable);
                Assert.IsFalse(Directory.Exists(targetDirectory));
            }
            else
            {
                targetTable.Output_dir = "TargetRenamed";
                playlist.MigrateCustomFolderOutputDirectory(targetTable, targetDirectory, outputBaseDirBefore: targetBase);
                Assert.IsFalse(Directory.Exists(targetDirectory));
                Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(targetBase, "TargetRenamed"), "*.lr2folder").Any());
            }

            DateTime actualBaseTime = Directory.GetLastWriteTimeUtc(targetBase);
            Assert.AreNotEqual(staleTime.ToUnixtime(), actualBaseTime.ToUnixtime(), "操作で基点の更新時刻が進む前提です。");
            LR2SongDB.folder baseRow = ReadRows(baseRowPath).Single();
            Assert.AreEqual(actualBaseTime.ToUnixtime(), baseRow.date, "削除・移行の終端の同期で基点の行の date を実際の更新時刻へ揃えます。");

            if (operation == "rename")
            {
                // 製品の改名は、移行と同じ操作で表の保存と出力状態も確定します。基点の行の確認の後に、その確定を再現します。
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([targetTable], "test_rename_commit_status");
                Assert.AreEqual(actualBaseTime.ToUnixtime(), ReadRows(baseRowPath).Single().date);
            }

            // 続く必要出力の判定は、再確認も同期もファイルの書直しも起こしません。
            Dictionary<string, (string Body, long Ticks)> CaptureFiles() => EnumerateOutputFiles().ToDictionary(
                path => path,
                path => (Convert.ToHexString(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path).Ticks),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (string Body, long Ticks)> filesBefore = CaptureFiles();
            int operations = synchronization.Operations.Count;
            await ReloadAsync();
            Assert.IsFalse(repairProgress.Any(snapshot => snapshot.IsActive), "基点配下の表を再確認対象へ戻しません。");
            Assert.AreEqual(operations, synchronization.Operations.Count);
            Dictionary<string, (string Body, long Ticks)> filesAfter = CaptureFiles();
            CollectionAssert.AreEquivalent(filesBefore.Keys, filesAfter.Keys);
            foreach (KeyValuePair<string, (string Body, long Ticks)> file in filesBefore)
            {
                Assert.AreEqual(file.Value, filesAfter[file.Key], file.Key);
            }
            Assert.AreEqual(actualBaseTime.ToUnixtime(), ReadRows(baseRowPath).Single().date);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            var workCompletion = Task.WhenAll(acceptedWork.ToArray());
            try { await workCompletion; }
            catch (Exception ex) { if (!ReferenceEquals(ex, primaryFailure)) { cleanupFailures.Add(workCompletion.Exception ?? ex); } }
            if (playlist != null)
            {
                try
                {
                    playlist.RequestShutdown("remove-or-rename-base-row-test-complete");
                    await playlist.WaitForPlaylistMutationIdleAsync();
                }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            try
            {
                if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); }
            }
            catch (Exception ex) { cleanupFailures.Add(ex); }
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException(cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                System.Diagnostics.Trace.WriteLine("remove-or-rename base row fixtureのcleanup二次失敗: " + cleanupFailure);
            }
        }
    }

    /// <summary>実生成済み表の出力を削除すると保存した出力状態も除去します。次の修復判断が消した出力を現行扱いする誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_ClearsCustomFolderOutputStatus()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7618,
                name = "StatusDelete",
                symbol = "SD",
                Output_dir = "StatusDelete",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    1L,
                    before.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                        table.playlist_id.Value));
            }

            playlist.RemoveCustomFolder(table);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                    table.playlist_id.Value));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>生成後に全種類を除外して実一括出力し、ファイルと配下DB行を残しません。空の期待集合を整理不要扱いする誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesFilesAndRows()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7613,
                name = "DisableAllOutput",
                symbol = "DAO",
                Output_dir = "DisableAllOutput",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot)), null, null, () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings), () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_output");
            string outputDir = Path.Combine(outputBaseDir, "DisableAllOutput");
            Assert.IsTrue(Directory.Exists(outputDir));
            Assert.IsTrue(Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using (LR2SongDBExtended verifySeed = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_output");

            Assert.IsFalse(Directory.Exists(outputDir)
                && Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList()
                .Count(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>ルート型の物理出力を先に失った状態から全種類を除外し、安全な管理先に残るDB階層を整理します。物理不在時の旧DB行放置を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesDbOnlyRootRows()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string rootOutputBaseDir = Path.Combine(tempDirectory, "ManagedRootOutput");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7614,
                name = "DisableAllRootOutput",
                symbol = "DAR",
                Output_dir = "DisableAllRootOutput",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_root_output");
            string outputDir = Path.Combine(rootOutputBaseDir, "DisableAllRootOutput");
            string relativeOutputDir = outputDir;
            using (LR2SongDBExtended verifySeed = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
            }
            Directory.Delete(outputDir, recursive: true);

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_root_output");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList()
                .Count(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>出力基点を実ファイルで塞いで生成を失敗させ、保存済みsymbolと除外maskを保持します。出力未完了のheaderを先に保存する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DoesNotCommitHeaderWhenOutputFails()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            LR2SongDBExtended.playlist.CustomFolderType initialMask =
                LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            var table = new BMSTable
            {
                playlist_id = 7615,
                name = "CommitFailure",
                symbol = "CF",
                Output_dir = "CommitFailure",
                ignore_folder_output = initialMask,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot)), null, null, () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings), () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_commit_failure");
            using (LR2SongDBExtended verifySeed = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    (int)initialMask,
                    verifySeed.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = 7615;"));
            }

            string outputBaseFile = Path.Combine(tempDirectory, "output-base-file");
            File.WriteAllText(outputBaseFile, "not a directory", Encoding.UTF8);
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseFile;
            table.symbol = "CF2";

            Exception? exception = null;
            try
            {
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_commit_failure");
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception);
            Assert.IsTrue(
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException,
                exception.GetType().FullName);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                (int)initialMask,
                verify.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = 7615;"));
            Assert.AreEqual(
                "CF",
                verify.ExecuteScalar<string>("SELECT symbol FROM playlist WHERE playlist_id = 7615;"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実全体同期の準備から二表三ファイルの生成面と進捗件数を返し、出力状態は保存しても既存folder行を変更しません。準備とDB反映の混同を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2SongDbSyncPreparation_ReportsActualTablesAndFilesThroughWorkflowWithoutSavingFolderRows()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            var outputSettings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = testSettings.LR2RootPath,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = "sentinel", title = "Preserved", type = 2, adddate = 123 });
            }
            // Dispatcherを持たない既存スケジューラーで生産値を観測する。全中間値のUI描画は要求しない。
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new WpfUiScheduler(() => null), settings: testSettings);
            var stages = new List<Lr2SongDbSyncStatusSnapshot>();
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStatusVersion))
                {
                    stages.Add(library.GetLr2SongDbSyncStatusSnapshot());
                }
            };
            BMSTable[] tables =
            [
                new BMSTable
                {
                    playlist_id = 7811, name = "First", Output_dir = "First", symbol = "F",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A"), CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B")],
                    Folder_order = ["A", "B"]
                },
                new BMSTable
                {
                    playlist_id = 7812, name = "Second", Output_dir = "Second", symbol = "S",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "C")],
                    Folder_order = ["C"]
                }
            ];
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath, () => outputSettings, null, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(tables)
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);

            Lr2SongDbSyncPreparedDataSurface surface;
            using (LibraryFileMutationLease preparationLease = library.Lr2Synchronization.TryBeginMutation("test_actual_preparation"))
            {
                Assert.IsNotNull(preparationLease);
                Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable playlistLease));
                using IDisposable playlistOperation = playlistLease;
                using LibraryFileMutationCapability playlistCapability = playlist.CreatePlaylistMutationCapability(playlistLease);
                surface = runtime.PrepareGeneratedDataUnderLease(library, "test_actual_preparation", preparationLease, false, playlistCapability: playlistCapability);
            }
            Assert.IsNotNull(surface);
            Assert.IsTrue(surface.Lr2FolderFileDiscoveryComplete);
            CollectionAssert.AreEquivalent(new[]
            {
                Path.Combine(outputSettings.LR2CustomFolderOutputBaseDir, "First", "0000.lr2folder"),
                Path.Combine(outputSettings.LR2CustomFolderOutputBaseDir, "First", "0001.lr2folder"),
                Path.Combine(outputSettings.LR2CustomFolderOutputBaseDir, "Second", "0000.lr2folder")
            }, surface.Lr2FolderFilePaths.ToArray());
            Assert.IsTrue(surface.Lr2FolderFilePaths.All(File.Exists));
            Assert.AreEqual(3, surface.Lr2FolderFileEntries.Count);
            Assert.IsTrue(surface.DirectoryEntries.Count >= 2);
            foreach ((string Stage, int Total) expected in new[] { (Stage: "playlist_projection", Total: 2), (Stage: "playlist_files", Total: 3) })
            {
                Lr2SongDbSyncStatusSnapshot[] counts = stages.Where(value => value.Stage == expected.Stage).ToArray();
                Assert.IsTrue(counts.Length > 0, expected.Stage);
                Assert.IsTrue(counts.All(value => value.StageTotalCount == expected.Total
                    && value.StageProcessedCount >= 0 && value.StageProcessedCount <= expected.Total), expected.Stage);
                Assert.IsTrue(counts.All(value => value.ProcessedCursor == null && value.TotalCount == null), expected.Stage);
                Assert.AreEqual(expected.Total, counts.Last().StageProcessedCount, expected.Stage);
            }
            foreach (string stage in new[] { "playlist_output_discovery", "playlist_state_saving", "playlist_result_preparation" })
            {
                Assert.IsTrue(stages.Any(value => value.Stage == stage && value.StageTotalCount == 0), stage);
            }
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM playlist_custom_folder_output_status;"));
                Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count());
                LR2SongDB.folder sentinel = verify.Find<LR2SongDB.folder>("sentinel");
                Assert.AreEqual("Preserved", sentinel.title);
                Assert.AreEqual(123, sentinel.adddate);
            }
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation("test_actual_preparation_released");
            Assert.IsNotNull(fresh);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>空の表一覧でも準備を正常終端し、表・ファイル処理を報告せず次のlibrary予約を受理します。空入力での予約漏れを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputAllCustomFoldersForLr2SongDbSync_EmptyTablesReleasePreparationReservation()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new WpfUiScheduler(() => null), settings: testSettings);
            var stages = new List<Lr2SongDbSyncStatusSnapshot>();
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStatusVersion))
                {
                    stages.Add(library.GetLr2SongDbSyncStatusSnapshot());
                }
            };
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath, getOutputSettings, null, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);

            Lr2SongDbSyncPreparedDataSurface? surface = null;
            using (LibraryFileMutationLease preparationLease = library.Lr2Synchronization.TryBeginMutation("test_empty_snapshot"))
            {
                Assert.IsNotNull(preparationLease);
                Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable playlistLease));
                using IDisposable playlistOperation = playlistLease;
                using LibraryFileMutationCapability playlistCapability = playlist.CreatePlaylistMutationCapability(playlistLease);
                surface = runtime.PrepareGeneratedDataUnderLease(library, "test_empty_snapshot", preparationLease, false, playlistCapability: playlistCapability);
            }

            Assert.IsTrue(stages.Count > 0);
            Assert.IsFalse(stages.Any(stage => stage.Stage == "playlist_projection" || stage.Stage == "playlist_files"));
            Assert.IsNotNull(surface);
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation(
                "test_empty_snapshot_after_release");
            Assert.IsNotNull(fresh);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>全体同期準備は管理先の余分な物理ファイルを整理しても、その外部folder行と各保存列を変更しません。準備中にDB反映まで進める誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2SongDbSyncCustomFolderPreparation_PreservesExtraLr2FolderRowInManagedOutputDirectory()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7604,
                name = "ExternalCoLocated",
                symbol = "EC",
                Output_dir = "ExternalCoLocated",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            Lr2ConfigOwner lr2ConfigProvider = new(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot));
            var library = new TestBmsLibrary(databaseScope, songDbPath, lr2ConfigProvider, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath, () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), lr2ConfigProvider, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_external_colocated");
            string outputDir = Path.Combine(outputBaseDir, "ExternalCoLocated");
            string expectedFile = Path.Combine(outputDir, "0000.lr2folder");
            string extraFile = Path.Combine(outputDir, "external.lr2folder");
            File.WriteAllText(extraFile, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            DateTime extraTimestamp = new(2026, 6, 2, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(extraFile, extraTimestamp);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.Insert(new LR2SongDB.folder
                {
                    path = extraFile,
                    title = "External",
                    type = 2,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir),
                    date = extraTimestamp.ToUnixtime(),
                    adddate = extraTimestamp.ToUnixtime()
                });
            }

            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);
            Lr2SongDbSyncPreparedDataSurface? surface = null;
            using (LibraryFileMutationLease preparationLease = library.Lr2Synchronization.TryBeginMutation("test_external_colocated"))
            {
                Assert.IsNotNull(preparationLease);
                Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable playlistLease));
                using IDisposable playlistOperation = playlistLease;
                using LibraryFileMutationCapability playlistCapability = playlist.CreatePlaylistMutationCapability(playlistLease);
                surface = runtime.PrepareGeneratedDataUnderLease(library, "test_external_colocated", preparationLease, false, playlistCapability: playlistCapability);
            }

            Assert.IsFalse(File.Exists(extraFile));
            CollectionAssert.Contains(surface.Lr2FolderFilePaths.ToList(), expectedFile);
            Assert.AreEqual(0, surface.Lr2FolderScopeDirectories.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder extraFolder = verify.Find<LR2SongDB.folder>(extraFile);
            Assert.IsNotNull(extraFolder);
            Assert.AreEqual("External", extraFolder.title);
            Assert.AreEqual(2, extraFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir), extraFolder.parent);
            Assert.AreEqual(extraTimestamp.ToUnixtime(), extraFolder.date);
            Assert.AreEqual(extraTimestamp.ToUnixtime(), extraFolder.adddate);
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation(
                "test_external_colocated_after_release");
            Assert.IsNotNull(fresh);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 旧版の出力名 <c>AB</c> と <c>A\B</c> が、単一名化後に同じ出力先 <c>AB</c> になる保存済みの二表（T1・T2）と、無関係な表（T3）を、共有先の旧ファイル・DB行・ROOT登録とともに用意します。
    /// 共有先の旧ファイルはどちらの表の生成物とも言えないため、表の操作が触れたかを内容・更新時刻・DB行で判定できるようにします。
    /// </summary>
    private static async Task<(BMSTable T1, BMSTable T2, BMSTable T3, string SharedDirectory, string SharedFile, DateTime SharedTimestamp)> CreateCollidingLayoutAsync(
        PlaylistRestoreOutputFixture fixture, bool root)
    {
        BMSTable t1 = PlaylistRestoreOutputFixture.Table(8101, "AB", "Folder 1", root);
        BMSTable t2 = PlaylistRestoreOutputFixture.Table(8102, "Other", "Folder 2", root);
        t2.output_dir = @"A\B";
        BMSTable t3 = PlaylistRestoreOutputFixture.Table(8103, "Third", "Folder 3");
        Assert.AreEqual(t1.Output_dir, t2.Output_dir, "旧値の区切りを除いた単一名が同一の出力先になります。");
        fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([t1, t2, t3]);
        fixture.Playlist.CommitBMSTablesWithEntriesToDB([t1, t2, t3]);
        string shared = fixture.OutputDirectory(t1);
        string sharedFile = Path.Combine(shared, "0000.lr2folder");
        Directory.CreateDirectory(shared);
        File.WriteAllText(sharedFile, "shared preserved output");
        using (LR2SongDBExtended db = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb))
        {
            db.CreateTable<LR2SongDB.folder>();
            db.InsertOrReplace(new LR2SongDB.folder { path = sharedFile, title = "shared old file", type = 2 });
            db.InsertOrReplace(new LR2SongDB.folder { path = Lr2FolderPath.ToFolderPath(shared), title = "shared old directory", type = 1 });
        }
        if (root) { fixture.Config.SaveRoots(new Lr2RootChange([], [shared])); }
        await Task.CompletedTask;
        return (t1, t2, t3, shared, sharedFile, File.GetLastWriteTimeUtc(sharedFile));
    }

    private static void AssertSharedOutputUntouched(PlaylistRestoreOutputFixture fixture, string sharedFile, DateTime sharedTimestamp)
    {
        Assert.AreEqual("shared preserved output", File.ReadAllText(sharedFile), "衝突した表の共有先の本文は変更しません。");
        Assert.AreEqual(sharedTimestamp, File.GetLastWriteTimeUtc(sharedFile));
        using LR2SongDBExtended verify = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb);
        Assert.AreEqual("shared old file", verify.Table<LR2SongDB.folder>().Single(row => row.path == sharedFile).title, "衝突した表の共有先のDB行は整理しません。");
    }

    /// <summary>
    /// 条件：保存済みの出力先が衝突している二表（T1・T2）と無関係な表（T3）に対し、利用者の明示操作（出力先の解消、無関係な保存、配置以外の保存、項目の追加、表の削除）を行う。
    /// 期待結果：衝突を解消する保存（T2の出力先変更）は受理して新しい出力先だけを生成し、共有先は変えない（相手のT1は次の必要出力で生成する）。T3の保存・削除は通常どおり成立する。
    /// 衝突したままのT1の配置以外の保存はDBへ保存して出力を止め、警告を一件だけ案内する。項目の追加に伴う出力は警告せず診断ログだけに残す。衝突した表の削除は出力状態だけを消し、共有先のファイル・DB行・ROOT登録は残す（両方を削除しても残す）。
    /// 検出する誤り：保存済みの衝突が無関係な操作を止める誤り、解消する保存の拒否、衝突した表の共有先を書換え・削除・整理して相手の出力を巻き込む誤り、衝突した表の削除でROOT登録を除く誤り、
    /// 項目の追加のような明示でない出力で警告を出す誤り、明示の保存で警告を出さない誤り。
    /// </summary>
    [TestMethod]
    [DataRow("resolve")]
    [DataRow("unrelatedSave")]
    [DataRow("nonPlacementSave")]
    [DataRow("entryAdd")]
    [DataRow("deleteColliding")]
    [DataRow("deleteUnrelated")]
    [DataRow("deleteBoth")]
    [TestCategory("Playlist")]
    public async Task SavedCollidingOutput_ExplicitOperationsFreezeOnlyCollidingTables(string operation)
    {
        await using var fixture = new PlaylistRestoreOutputFixture();
        bool root = operation is "deleteColliding" or "deleteBoth";
        (BMSTable t1, BMSTable t2, BMSTable t3, string shared, string sharedFile, DateTime sharedTimestamp) = await CreateCollidingLayoutAsync(fixture, root);
        var service = new PlaylistPropertySaveService(() => fixture.Playlist, () => fixture.Library, () => fixture.Config, () => fixture.OutputSettings);
        service.PlaylistPropertyCatalogChanged += (_, _) => { };
        service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
        string t3Directory = fixture.OutputDirectory(t3);
        if (operation == "deleteUnrelated") { fixture.Playlist.ReOutputCustomFolder(t3); Assert.IsTrue(Directory.Exists(t3Directory)); }
        using PlaylistOperationNotificationOwner.OperationNotificationSession notices = fixture.Playlist.OperationNotificationOwner.BeginSession();
        bool sharedKept = true;

        async Task SaveAsync(BMSTable table, Action<PlaylistPropertyValues> edit)
        {
            using PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("編集セッションを作成できません。");
            edit(session.Values);
            (PlaylistPropertySaveCommit? commit, PlaylistPropertyValidationError? error) = await service.TrySaveAsync(session, session.Values);
            Assert.IsNotNull(commit, "保存は受理されます。検証エラー=" + error);
            Assert.IsNull(commit.Exception);
        }
        async Task RemoveAsync(params BMSTable[] tables)
        {
            var dialogs = new BlockingConfirmationDialogService();
            dialogs.ReleaseConfirmation();
            var removal = new PlaylistRemovalWorkflowOwner(dialogs, () => fixture.Playlist, () => fixture.Library, () => fixture.Config, () => fixture.OutputSettings);
            await removal.RemoveSummaryRowsAsync([.. tables.Select(table => new PlaylistSummaryRow { TableRef = table })]);
        }

        switch (operation)
        {
            case "resolve":
                await SaveAsync(t2, values => values.OutputDirectory = "Moved");
                Assert.AreEqual("Moved", t2.Output_dir);
                Assert.IsTrue(Directory.GetFiles(fixture.OutputDirectory(t2), "*.lr2folder", SearchOption.AllDirectories).Length > 0, "解消した表は新しい出力先へ生成します。");
                AssertSharedOutputUntouched(fixture, sharedFile, sharedTimestamp);
                // 衝突が解消した相手の出力は、次の必要出力の判定（明示の再読込み）で生成します。
                await fixture.Playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
                Assert.AreNotEqual("shared preserved output", File.ReadAllText(sharedFile));
                sharedKept = false;
                break;
            case "unrelatedSave":
                await SaveAsync(t3, values => values.Symbol = "Z");
                Assert.AreEqual("Z", t3.symbol);
                Assert.AreEqual(0, notices.TakeReceipt().Notifications.Count, "衝突した表を操作していない保存は警告しません。");
                break;
            case "nonPlacementSave":
                await SaveAsync(t1, values => values.Symbol = "Z");
                Assert.AreEqual("Z", t1.symbol);
                PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = notices.TakeReceipt();
                PlaylistOperationNotificationOwner.OperationNotification warning = receipt.Notifications.Single();
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, warning.Severity);
                Assert.AreEqual(Resources.Warning, warning.FormatCaption());
                Assert.IsTrue(warning.IsStoppedOutputsWarning, "本番の停止警告は、まとめない通知の区別を持ちます。");
                StringAssert.Contains(warning.FormatMessage(), Resources.Warn_PlaylistOutputOverlapStopped);
                StringAssert.Contains(warning.FormatMessage(), t1.name + ": " + shared);
                break;
            case "entryAdd":
                Assert.IsTrue(fixture.Playlist.AddPlaylistEntriesToFolderBMSTable([PlaylistTestFixture.CreateEntry(new string('d', 32), "Folder 1")], t1, "Folder 1"));
                Assert.AreEqual(2, t1.entries.Count());
                Assert.AreEqual(0, notices.TakeReceipt().Notifications.Count, "項目の追加に伴う出力は警告せず、診断ログだけに残します。");
                break;
            case "deleteColliding":
                await RemoveAsync(t2);
                Assert.IsFalse(fixture.Playlist.BMSTables.Contains(t2));
                break;
            case "deleteUnrelated":
                await RemoveAsync(t3);
                Assert.IsFalse(fixture.Playlist.BMSTables.Contains(t3));
                Assert.IsFalse(Directory.Exists(t3Directory), "衝突していない表は通常どおり出力先を整理します。");
                break;
            default:
                await RemoveAsync(t1, t2);
                Assert.AreEqual(1, fixture.Playlist.BMSTables.Count);
                break;
        }

        if (sharedKept) { AssertSharedOutputUntouched(fixture, sharedFile, sharedTimestamp); }
        Assert.IsTrue(Directory.Exists(shared), "衝突した表の共有先ディレクトリは削除しません。");
        if (root)
        {
            Assert.IsTrue(fixture.Config.CaptureSaved().RegisteredRoots.Any(registered => string.Equals(Path.TrimEndingDirectorySeparator(registered), shared, StringComparison.OrdinalIgnoreCase)), "衝突した表の削除ではROOT登録を除きません。");
        }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(operation switch { "deleteColliding" or "deleteUnrelated" => 2, "deleteBoth" => 1, _ => 3 }, db.Table<BMSTable>().Count());
    }

    /// <summary>
    /// 条件：保存済みの出力先が衝突している二表と無関係な表を持つ状態で、表の明示の再読込み（再読込み）とSQL復元を行う。
    /// 期待結果：例外にならず、衝突した表の共有先は変更せず、無関係な表は生成する。衝突した表の表名と出力先を一件の警告にまとめて案内する。SQL復元は復元前に衝突していた旧配置を整理しない。
    /// 検出する誤り：保存済みの衝突で再読込み・復元が失敗する誤り、衝突した表の共有先を書換え・整理する誤り、衝突した表ごとに警告を重ねる誤り。
    /// </summary>
    [TestMethod]
    [DataRow("reload")]
    [DataRow("restore")]
    [TestCategory("Playlist")]
    public async Task CollidingOutput_ReloadAndSqlRestoreContinueWithSingleWarning(string operation)
    {
        await using var fixture = new PlaylistRestoreOutputFixture();
        (BMSTable t1, BMSTable t2, BMSTable t3, string shared, string sharedFile, DateTime sharedTimestamp) = await CreateCollidingLayoutAsync(fixture, root: false);
        string dump = fixture.Playlist.GetPlaylistDump();
        string t3Directory = fixture.OutputDirectory(t3);
        using PlaylistOperationNotificationOwner.OperationNotificationSession notices = fixture.Playlist.OperationNotificationOwner.BeginSession();
        // 明示の操作として、止めた表を警告で案内します（作業領域の入口が収集先へ指定する）。
        notices.WarnStoppedOutputs();

        if (operation == "reload") { await fixture.Playlist.ReloadTablesAsync(exportBeatorajaBmt: false); }
        else { await fixture.Playlist.RestorePlaylistDumpAsync(dump); }

        PlaylistOperationNotificationOwner.OperationNotification warning = notices.TakeReceipt().Notifications.Single();
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, warning.Severity);
        string text = warning.FormatMessage();
        StringAssert.Contains(text, t1.name + ": " + shared);
        StringAssert.Contains(text, t2.name + ": " + shared);
        Assert.IsFalse(text.Contains(t3.name, StringComparison.Ordinal), "衝突していない表は警告に含めません。");
        Assert.IsTrue(Directory.GetFiles(t3Directory, "*.lr2folder", SearchOption.AllDirectories).Length > 0, "衝突していない表は生成します。");
        AssertSharedOutputUntouched(fixture, sharedFile, sharedTimestamp);
        Assert.IsTrue(Directory.Exists(shared));
    }

    /// <summary>実詳細編集のDB保存、LR2出力、BMT準備・取消を分け、DB前失敗だけ旧levelへ戻します。空receiptのconsumer故障と実LR2警告の表示・診断故障を区別し、元結果を保って配送終端まで受付と編集状態を保持し、次の明示編集を保存します。各実Task終端後の開始gate解除も確認し、終了取消後の新保存成功は要求しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    // 空receiptでは表示を試みずconsumerだけを故障させる。実表示故障はLR2書込み失敗から生成されたWarningへ重ねる。
    [DataRow("none")]
    [DataRow("preparation")]
    [DataRow("preparation_projection")]
    [DataRow("output")]
    [DataRow("empty_receipt_consumer")]
    [DataRow("output_notification")]
    [DataRow("bmt_preparation")]
    [DataRow("bmt_cancel")]
    [DataRow("bmt_cancel_empty_receipt_consumer")]
    [DataRow("database_empty_receipt_consumer")]
    public async Task DetailEdit_ReoutputsLr2FolderRowsForLevelProjection(string failureStage)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool failRequiredProjection = failureStage == "preparation_projection";
        if (failRequiredProjection) { failureStage = "preparation"; }
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        FileStream? outputBlocker = null;
        Task? edit = null;
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var receiptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryPresentationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntryPresentation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            bool injectFailure = false;
            double? levelBeforeBmtCancellation = null;
            var preparationFailure = new InvalidOperationException("detail output settings failed after DB commit");
            var bmtPreparationFailure = new InvalidOperationException("detail BMT settings failed after DB commit");
            var presentationFailure = new InvalidOperationException("detail warning presentation failed");
            var consumerFailure = new InvalidOperationException("detail empty receipt consumer failed");
            var diagnosticFailure = new InvalidOperationException("detail receipt diagnostic failed");
            var requiredProjectionFailure = new InvalidOperationException("detail required projection failed");
            bool failPresentation = failureStage == "output_notification";
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                if (injectFailure && failureStage == "preparation") { throw preparationFailure; }
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSTableEntry entry = CreateEntryWithLevel("cccccccccccccccccccccccccccccccc", 1);
            entry.playlist_id = 7303;
            var table = new BMSTable
            {
                playlist_id = 7303,
                name = "LevelTable",
                symbol = "LT",
                Output_dir = "LevelTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder,
                entries = [entry]
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }, settings: testSettings);
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath, getOutputSettings, beatorajaBmtOptionsProvider: () =>
                {
                    if (injectFailure && failureStage == "bmt_preparation") { throw bmtPreparationFailure; }
                    if (injectFailure && failureStage is "bmt_cancel" or "bmt_cancel_empty_receipt_consumer")
                    {
                        // 実DB確定とLR2終端の後、BMT workerのshutdown判定で取消にします。
                        using (LR2SongDBExtended committedDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                        {
                            levelBeforeBmtCancellation = committedDb.Table<BMSTableEntry>().Single().level;
                        }
                        (playlist ?? throw new InvalidOperationException("The playlist was not initialized before BMT cancellation."))
                            .RequestShutdown("detail-post-commit-bmt-test");
                    }
                    return new BeatorajaBmtOptionsSnapshot();
                }, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            var diagnostics = new List<(Exception Exception, string Diagnostic)>();
            // 通知は受付の解放後に依頼されるため、依頼の時点でPを取得できることを窓口が記録します。表示の失敗は窓口のTaskで表します。
            bool? admittedAtRequest = null;
            var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService
            {
                MessageTaskFactory = async _ =>
                {
                    bool admitted = playlist!.TryEnterPlaylistMutation(out IDisposable probe);
                    probe?.Dispose();
                    admittedAtRequest ??= admitted;
                    if (failPresentation)
                    {
                        receiptEntered.TrySetResult();
                        await releaseReceipt.Task;
                        throw presentationFailure;
                    }
                    return UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK);
                },
            };
            workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
                reloadFailureLog: (exception, diagnostic) => diagnostics.Add((exception, diagnostic)),
                playlistWorkspaceDialogService: dialogs,
                playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                restoreUiApplyScheduler: async action =>
                {
                    if (injectFailure && failureStage is "none" or "preparation")
                    {
                        entryPresentationEntered.TrySetResult();
                        await releaseEntryPresentation.Task;
                    }
                    action();
                    if (injectFailure && failRequiredProjection) { throw requiredProjectionFailure; }
                });
            var entryDeliveries = new List<string>();
            workspace.DetailWorkObserver = kind => entryDeliveries.Add(kind);
            var source = new PlaylistDetailSourceRow(entry, resolvedChart: null);
            workspace.DetailViewState.Source.Rows = [source];
            var context = new MainChartListCellEditContext(source.CreateViewRow(), nameof(PlaylistDetailRow.Level),
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            workspace.BeginDetailEdit(context);
            if (failureStage is "output" or "output_notification")
            {
                string outputFile = Directory.GetFiles(Path.Combine(providerOutputBaseDir, table.Output_dir), "*.lr2folder", SearchOption.AllDirectories)
                    .First(path => ReadShiftJisText(path).Contains("LEVEL 1", StringComparison.Ordinal));
                outputBlocker = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            if (failureStage == "database_empty_receipt_consumer")
            {
                using LR2SongDBExtended failDatabase = databaseScope.OpenSongWrite(songDbPath);
                failDatabase.Execute("CREATE TRIGGER fail_detail_save BEFORE INSERT ON playlist_entry BEGIN SELECT RAISE(FAIL, 'forced detail DB failure'); END;");
            }
            injectFailure = true;
            edit = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "12", commit: true));
            if (failureStage is "none" or "preparation")
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(entryPresentationEntered.Task, edit, "detail-edit.required-projection");
                Assert.IsFalse(edit.IsCompleted);
                Assert.IsTrue(workspace.DetailViewState.Source.IsPlaylistCellEditing);
                Assert.AreEqual(12, entry.level, "必要表示の配送待ちでも正本の保存は確定しています。");
                bool admitted = playlist.TryEnterPlaylistMutation(out IDisposable blocked);
                blocked?.Dispose();
                Assert.IsFalse(admitted, "正常と確定後出力失敗の双方で、必要表示より先にPを解放しません。");
                releaseEntryPresentation.TrySetResult();
            }
            Task? mutationIdle = null;
            Exception? observedFailure = null;
            try { await edit; }
            catch (Exception exception) { observedFailure = exception; }
            if (failureStage == "output_notification")
            {
                // 表示が未完了（その後に失敗する）でも、保存の終端は表示を待たず、依頼の時点でPは解放済みです。
                Assert.IsTrue(receiptEntered.Task.IsCompleted, "完了の時点で通知の依頼が窓口へ届いています。");
                Assert.AreEqual(true, admittedAtRequest, "通知の依頼の時点でPを取得できます。");
                Assert.IsFalse(releaseReceipt.Task.IsCompleted);
                releaseReceipt.TrySetResult();
            }
            await (mutationIdle ?? playlist.WaitForPlaylistMutationIdleAsync());
            Assert.IsFalse(playlist.HasShutdownBlockingWork, "成功・実失敗・shutdown取消の全てで受理済み処理を解放します。");
            if (failureStage is "none" or "empty_receipt_consumer")
            {
                Assert.IsNull(observedFailure);
                Assert.IsTrue(edit.IsCompletedSuccessfully);
            }
            else if (failureStage == "database_empty_receipt_consumer")
            {
                Assert.IsNotNull(observedFailure);
                Assert.IsFalse(observedFailure is PlaylistMutationPostCommitException);
                StringAssert.Contains(observedFailure.ToString(), "forced detail DB failure");
            }
            else
            {
                Assert.IsInstanceOfType<PlaylistMutationPostCommitException>(observedFailure);
                var failure = (PlaylistMutationPostCommitException)observedFailure;
                if (failureStage == "preparation") { Assert.AreSame(preparationFailure, failure.InnerException); }
                else if (failureStage is "output" or "output_notification")
                {
                    Assert.IsInstanceOfType<IOException>(failure.InnerException);
                    Assert.IsFalse(failure.ToString().Contains(presentationFailure.Message, StringComparison.Ordinal));
                    Assert.IsFalse(failure.ToString().Contains(diagnosticFailure.Message, StringComparison.Ordinal));
                }
                else if (failureStage == "bmt_preparation") { Assert.AreSame(bmtPreparationFailure, failure.InnerException); }
                else
                {
                    Assert.IsInstanceOfType<OperationCanceledException>(failure.InnerException);
                    Assert.IsTrue(playlist.IsShutdownRequested);
                    Assert.AreEqual<double?>(12d, levelBeforeBmtCancellation, "BMT取消を要求した時点で実DBは確定済みです。");
                    StringAssert.Contains(failure.InnerException.Message, "Playlist BMT output");
                }
            }
            if (failRequiredProjection)
            {
                Assert.AreEqual(1, diagnostics.Count);
                Assert.AreSame(requiredProjectionFailure, diagnostics[0].Exception);
            }
            else { Assert.AreEqual(0, diagnostics.Count, "表示の失敗は元の操作結果へ合成せず、必須の診断先へも加えません。"); }
            if (failureStage is "output" or "output_notification")
            {
                UiMessageRequest warning = dialogs.MessageRequests.Single();
                Assert.AreEqual(System.Windows.MessageBoxImage.Exclamation, warning.Icon);
                Assert.AreEqual(Resources.MessageBoxTitle_Warning, warning.Caption);
                Assert.AreEqual(string.Format(Resources.Warn_CustomFolderOutputFailed, table.name,
                    Path.Combine(providerOutputBaseDir, table.Output_dir)), warning.MessageBoxText);
            }
            else
            {
                Assert.AreEqual(0, dialogs.MessageRequests.Count, "空の領収書では通知を依頼しません。");
            }
            if (observedFailure != null) { Assert.IsTrue(edit.IsFaulted); }

            int expectedLevel = failureStage == "database_empty_receipt_consumer" ? 1 : 12;
            Assert.AreEqual(expectedLevel, entry.level);
            Assert.AreEqual(expectedLevel.ToString(), ((PlaylistDetailRow)context.Row).Level);
            Assert.AreEqual("1", source.CreateViewRow().Level, "過去の不変入力は確定後も変更しません。");
            Assert.AreEqual(expectedLevel.ToString(), workspace.DetailViewState.Source.Rows.Single().CreateViewRow().Level);
            Assert.AreEqual(failureStage == "database_empty_receipt_consumer" ? 0 : 1,
                entryDeliveries.Count(kind => kind == "playlist_entry_commit_applied"),
                "DB確定後は出力・取消・通知の失敗でも同じ差分が表示へ届きます。");
            Assert.IsFalse(workspace.DetailViewState.Source.IsPlaylistCellEditing);
            Assert.IsTrue(workspace.CanBeginDetailEdit(context), "成功・保存前/後失敗・取消のTask終端で保存gateを解除します。終了後の保存成功までは要求しません。");

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(expectedLevel, verify.Table<BMSTableEntry>().Single(row =>
                    row.playlist_id == table.playlist_id && row.md5 == entry.md5).level);
                if (failureStage is "none" or "empty_receipt_consumer")
                {
                    var folders = verify.Table<LR2SongDB.folder>().ToList();
                    Assert.IsTrue(Directory.Exists(Path.Combine(providerOutputBaseDir, table.Output_dir)));
                    Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
                    Assert.AreEqual(0, folders.Count(row => row.title == "LEVEL 1"));
                    LR2SongDB.folder levelFolder = folders.Single(row => row.title == "LEVEL 12");
                    Assert.AreEqual(2, levelFolder.type);
                    StringAssert.Contains(levelFolder.command, "playlist_entry");
                    Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(providerOutputBaseDir, table.Output_dir),
                        "*.lr2folder", SearchOption.AllDirectories).Any(path => ReadShiftJisText(path).Contains("LEVEL 12", StringComparison.Ordinal)));
                }
            }
            if (failureStage == "database_empty_receipt_consumer")
            {
                using (LR2SongDBExtended database = databaseScope.OpenSongWrite(songDbPath)) { database.Execute("DROP TRIGGER fail_detail_save;"); }
                failPresentation = false;
                workspace.BeginDetailEdit(context);
                edit = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "12", commit: true));
                await edit;
                Assert.AreEqual(12, entry.level);
                Assert.AreEqual("12", ((PlaylistDetailRow)context.Row).Level);
                Assert.AreEqual("12", workspace.DetailViewState.Source.Rows.Single().CreateViewRow().Level);
                Assert.AreEqual(0, dialogs.MessageRequests.Count, "空の領収書では通知を依頼しません。");
                using LR2SongDBExtended verifyRetry = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(12, verifyRetry.Table<BMSTableEntry>().Single().level, "同じ入力の再保存も実DBへ反映します。");
            }
            else if (failureStage is "empty_receipt_consumer" or "output_notification" || failRequiredProjection)
            {
                if (failRequiredProjection) { injectFailure = false; }
                failPresentation = false;
                outputBlocker?.Dispose();
                outputBlocker = null;
                var nextContext = new MainChartListCellEditContext(source.CreateViewRow(), nameof(PlaylistDetailRow.Level),
                    ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                workspace.BeginDetailEdit(nextContext);
                edit = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(nextContext, "13", commit: true));
                await edit;
                Assert.AreEqual(13, entry.level);
                Assert.AreEqual("13", ((PlaylistDetailRow)nextContext.Row).Level);
                Assert.AreEqual("13", workspace.DetailViewState.Source.Rows.Single().CreateViewRow().Level);
                Assert.AreEqual(failureStage == "output_notification" ? 1 : 0, dialogs.MessageRequests.Count, "次の空の領収書で通知を依頼しません。");
                Assert.AreEqual(failRequiredProjection ? 1 : 0, diagnostics.Count, "次の明示保存へ以前の診断失敗を持ち越しません。");
                Assert.IsFalse(workspace.DetailViewState.Source.IsPlaylistCellEditing);
                using LR2SongDBExtended verifyNext = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(13, verifyNext.Table<BMSTableEntry>().Single().level, "次の編集値を新しい実DB値として保存します。");
                var nextFolders = verifyNext.Table<LR2SongDB.folder>().ToList();
                Assert.AreEqual(0, nextFolders.Count(row => row.title is "LEVEL 1" or "LEVEL 12"));
                Assert.AreEqual(2, nextFolders.Single(row => row.title == "LEVEL 13").type);
                Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(providerOutputBaseDir, table.Output_dir),
                    "*.lr2folder", SearchOption.AllDirectories).Any(path => ReadShiftJisText(path).Contains("LEVEL 13", StringComparison.Ordinal)));
            }
        }
        finally
        {
            releaseReceipt.TrySetResult();
            releaseEntryPresentation.TrySetResult();
            if (edit != null) { try { await edit; } catch (Exception) { } }
            if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            outputBlocker?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private static string CreateBaseScoreDbPath(ApplicationDatabaseScope databaseScope, string tempDirectory)
    {
        string scoreDbPath = Path.Combine(tempDirectory, "score.db");
        using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        return scoreDbPath;
    }

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private static string CreateInstalledPlayHistoryScoreDbPath(ApplicationDatabaseScope databaseScope, string tempDirectory)
    {
        string scoreDbPath = CreateBaseScoreDbPath(databaseScope, tempDirectory);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService(databaseScope).InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.Status);
        return scoreDbPath;
    }

    private static string ReadShiftJisText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string BuildLongDirectoryPath(string root, string leaf)
    {
        string path = root;
        while (Path.Combine(path, leaf).Length <= 270)
        {
            path = Path.Combine(path, "segment-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }
        return Path.Combine(path, leaf);
    }

    /// <summary>呼出し元と同じDB所有者で保存状態の期待結果を確認します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private static void AssertClearFolderCommandMatchesAssistAndEasyRows(ApplicationDatabaseScope databaseScope, string songDbPath, string assistFolderText, string easyFolderText)
    {
        const string AssistOnlyHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string AssistEasyHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string AssistNoEasyHash = "cccccccccccccccccccccccccccccccc";
        const string AssistNullHistoryHash = "dddddddddddddddddddddddddddddddd";
        string assistCommand = ReadCustomFolderCommand(assistFolderText);
        string easyCommand = ReadCustomFolderCommand(easyFolderText);

        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
        db.Execute("CREATE TABLE IF NOT EXISTS song(hash TEXT PRIMARY KEY, title TEXT);");
        db.Execute("CREATE TABLE IF NOT EXISTS score(hash TEXT PRIMARY KEY, clear INTEGER, rank INTEGER, op_history INTEGER, minbp INTEGER);");
        db.Execute("DELETE FROM playlist_entry WHERE playlist_id = 7305 AND md5 IN (?, ?, ?, ?);", AssistOnlyHash, AssistEasyHash, AssistNoEasyHash, AssistNullHistoryHash);
        InsertCustomFolderClassificationRow(db, AssistOnlyHash, clear: 2, rank: 8, opHistory: ClearTypeStorageConverter.OptionHistoryAssist);
        InsertCustomFolderClassificationRow(db, AssistEasyHash, clear: 2, rank: 0, opHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy);
        InsertCustomFolderClassificationRow(db, AssistNoEasyHash, clear: 2, rank: 0, opHistory: 0);
        InsertCustomFolderClassificationRow(db, AssistNullHistoryHash, clear: 2, rank: 0, opHistory: null);

        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistOnlyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, assistCommand, AssistEasyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistNoEasyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistNullHistoryHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistOnlyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, easyCommand, AssistEasyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistNoEasyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistNullHistoryHash));
    }

    private static void InsertCustomFolderClassificationRow(LR2SongDBExtended db, string hash, int clear, int rank, int? opHistory)
    {
        db.Execute("INSERT OR REPLACE INTO song(hash, title, path) VALUES (?, ?, ?);", hash, hash, hash + ".bms");
        db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (7305, ?, ?, 0);", hash, hash);
        db.Execute("INSERT OR REPLACE INTO score(hash, clear, rank, op_history, minbp) VALUES (?, ?, ?, ?, 0);", hash, clear, rank, opHistory);
    }

    private static long CountCustomFolderCommandMatches(LR2SongDBExtended db, string command, string hash)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM song LEFT JOIN score ON song.hash = score.hash WHERE song.hash = ? AND " + command, hash);
    }

    private static void AssertCustomFolderCommandMatches(LR2SongDBExtended db, string command, int[] expectedNumbers)
    {
        CollectionAssert.AreEquivalent(expectedNumbers.Select(number => number.ToString("x32")).ToArray(),
            db.Query<LR2SongDB.song>("SELECT song.* FROM song LEFT JOIN score ON song.hash = score.hash WHERE " + command)
                .Select(song => song.hash).ToArray(), command);
    }

    private static void AssertCustomFolderCommandHashOrder(LR2SongDBExtended db, string command, int[] expectedNumbers)
    {
        CollectionAssert.AreEqual(expectedNumbers.Select(number => number.ToString("x32")).ToArray(),
            db.Query<LR2SongDB.song>("SELECT song.* FROM song LEFT JOIN score ON song.hash = score.hash WHERE " + command)
                .Select(song => song.hash).ToArray(), command);
    }

    private static string ReadCustomFolderCommand(string folderText)
    {
        string? line = folderText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .FirstOrDefault(value => value.StartsWith("#COMMAND ", StringComparison.Ordinal));
        Assert.IsFalse(string.IsNullOrWhiteSpace(line));
        return line!.Substring("#COMMAND ".Length);
    }

}
