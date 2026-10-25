using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>実プレイリスト保存に伴うLR2ファイル・DB同期と、出力失敗後の明示保存継続を確認します。</summary>
/// <remarks>SQL本文、実ファイルの整理、SQLite確定は下位の自己転記では代替できません。画面なしの保存serviceを主に使い、共有Dispatcherを設定する代表だけ非並列で接続を確認します。</remarks>
[TestClass]
public sealed class BmsPlaylistCustomFolderOutputTests
{
    /// <summary>既存DBの未登録名は通常出力だけを止めて対象名を知らせ、正常な兄弟表とROOT出力を継続します。プロパティの未変更保存でも名を保ち、通常先の再選択で解消します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnregisteredOutputBase_PreservesNameAndStopsOnlyNormalOutputUntilReselected(bool useSummaryFlags)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsPlaylist? playlist = null;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string normal = Path.Combine(directory, "Normal"), root = Path.Combine(directory, "Root");
            var settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = normal,
                LR2CustomFolderOutputBaseDirRootType = root,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var config = new Lr2ConfigOwner(CreateLr2Config(directory, normal));
            config.SetPlacement(settings);
            var library = new TestBmsLibrary(songPath, config, null, null, () => BmsLibraryOptionsSnapshot.CreateCurrent(testSettings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => settings, config,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), settings: testSettings);
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
            playlist.BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(songPath).LoadPlaylistHeaders());
            BMSTable unknown = playlist.BMSTables.Single(table => table.playlist_id == 7901);
            using (PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession())
            {
                playlist.RegenerateCustomFolderOutputs([], settings);
                PlaylistOperationNotificationOwner.OperationNotification warning = notifications.TakeReceipt().Notifications.Single();
                StringAssert.Contains(warning.Message, "Unknown"); StringAssert.Contains(warning.Message, "Missing");
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
            using LR2SongDBExtended read = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
            Assert.IsNull(read.Table<BMSTable>().Single(table => table.playlist_id == 7901).custom_folder_output_base_name);
        }
        finally
        {
            dialog?.Dispose(); workspace?.Dispose(); playlist?.RequestShutdown("unknown-output-fixture");
            Directory.Delete(directory, true);
        }
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>利用者フォルダを実出力し、Shift_JIS本文と対応するLR2の種別・カテゴリ・SQL・日時を同じ生成結果から保存します。ファイルだけ更新してDB行を残す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsLr2FolderRowsFromGeneratedText()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            string outputPath = Path.Combine(outputBaseDir, "FolderTable", "0001.lr2folder");
            var physicalSurface = CustomFolderOutputPhysicalSurface.FromEntries(
                [new RootFileEnumerationEntry(outputPath, DateTime.UtcNow)],
                discoveryComplete: true);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, physicalSurface);
            var playlist = new TestBmsPlaylist(songDbPath, synchronization, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsTrue(File.Exists(outputPath));
            string text = File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(text, "#TITLE Folder A");
            StringAssert.Contains(text, "#CATEGORY FolderTable");
            StringAssert.Contains(text, "#COMMAND song.hash");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("Folder A", folder.title);
            Assert.AreEqual("FolderTable", folder.category);
            StringAssert.Contains(folder.command, "playlist_entry");
            Assert.AreEqual(0, folder.max);
            Assert.IsTrue(folder.date.HasValue);
            Assert.IsTrue(folder.adddate.HasValue);
        }
        finally
        {
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
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
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            var expectedFailure = new InvalidOperationException("forced custom-folder sync failure");
            synchronization.Failure = expectedFailure;
            var playlist = new TestBmsPlaylist(songDbPath, synchronization, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            Assert.AreSame(
                expectedFailure,
                Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table)));

            using (PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession())
            {
                Assert.AreSame(
                    expectedFailure,
                    Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table)));
                PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
                Assert.AreEqual(1, receipt.Notifications.Count);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, receipt.Notifications[0].Severity);
                StringAssert.Contains(receipt.Notifications[0].Message, table.name);
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var table = new BMSTable
            {
                playlist_id = 7303,
                is_root_folder = true,
                Output_dir = "InjectedRootOutput"
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings, settings: testSettings)
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = BuildLongDirectoryPath(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(outputBaseDir, "LongFolderTable", "0001.lr2folder");
            Assert.IsTrue(LongPathFileSystem.FileExists(outputPath));
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE Folder B");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDirectory = Path.Combine(additionalOutputBaseDir, "AdditionalTable");
            string outputPath = Path.Combine(outputDirectory, "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>編集中に適用配置を切り替えてルート型・追加先と除外maskを保存し、実出力、config、DBへ現在の配置を反映します。編集開始時の配置を再利用して旧先を整理する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public void PlaylistPropertyDialogApplyPostSaveUpdates_UsesCurrentAppliedCustomFolderSettings()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Task operation = PlaylistPropertyDialogApplyPostSaveUpdatesCoreAsync();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                operation,
                nameof(PlaylistPropertyDialogApplyPostSaveUpdates_UsesCurrentAppliedCustomFolderSettings));
        });
    }

    /// <summary>
    /// 同期元と出力先A→Bの同時保存を実JSON・DBで確定した後のDB失敗でも、同じ編集sessionの再保存で旧A整理・LR2登録・新項目BMTを完了します。
    /// 実CollectionChangedの置換確定後だけ対象B配下のfolder INSERTを拒否し、取得や正本置換自体の失敗と分けます。通常移行・config失敗は既存継続ケースへ分担します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_ExternalReloadCommitRetainsOutputMigrationAfterLaterDatabaseFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsPlaylist? playlist = null;
        Task<PlaylistPropertyDialogOperationResult>? saving = null;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler? afterReplace = null;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string root = Path.Combine(directory, "root-output");
            string a = Path.Combine(root, "A"), b = Path.Combine(root, "B");
            string bmt = Path.Combine(directory, "bmt");
            string oldHeader = Path.Combine(directory, "old-header.json"), oldData = Path.Combine(directory, "old-data.json");
            string newHeader = Path.Combine(directory, "new-header.json"), newData = Path.Combine(directory, "new-data.json");
            File.WriteAllText(oldHeader, "{\"name\":\"External migration\",\"symbol\":\"X\",\"data_url\":\"old-data.json\"}");
            File.WriteAllText(oldData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            File.WriteAllText(newHeader, "{\"name\":\"External migration\",\"symbol\":\"X\",\"data_url\":\"new-data.json\"}");
            File.WriteAllText(newData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            LR2Config config = CreateLr2Config(directory, a);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
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
            var library = new TestBmsLibrary(songPath, configOwner, null, null, () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => output, configOwner,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt }, settings: settings);
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
                using var db = new LR2SongDBExtended(songPath);
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
            using (var db = new LR2SongDBExtended(songPath)) { db.Execute("DROP TRIGGER fail_later_folder;"); }
            File.Delete(newHeader);
            File.Delete(newData);
            saving = dialog.SaveAndApplyAsync();
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await saving);
            Assert.AreSame(replacement, playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id));
            Assert.IsFalse(Directory.Exists(a));
            Assert.IsTrue(Directory.GetFiles(b, "*.lr2folder").Length > 0);
            LR2Config disk = new(Path.Combine(directory, "LR2files", "Config", "config.xml"));
            CollectionAssert.DoesNotContain(disk.GetBMSSearchDirectoriesForChangeTracking(), a);
            CollectionAssert.Contains(disk.GetBMSSearchDirectoriesForChangeTracking(), b);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
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
    /// migration/config行の待合せは空の通知結果でも到達する任意の購読処理、bmt行は必要なBMT出力を保持します。
    /// 購読処理の故障を実出力・保存の失敗へ加えず、下位移行の管理領域・モード網羅は既存移行テスト、画面の所有と表示はWPF代表へ分担します。
    /// </summary>
    [DataTestMethod]
    // DB=B確定後、migrationは新先Bのディレクトリ生成、configは登録保存、bmtは台帳保存を失敗させる。bmt行だけ追加編集Cを行う。
    [DataRow("migration", false)]
    [DataRow("config", false)]
    [DataRow("bmt", true)]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_UnfinishedOutputsCompleteBeforeSavingFurtherEdits(string failurePoint, bool editAgain)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
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
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
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
            LR2Config config = CreateLr2Config(directory, Path.Combine(directory, "manual-root"), a);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = normalBase,
                LR2CustomFolderOutputBaseDirRootType = rootBase,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = directory;
                values.BMSRootPath = Path.Combine(directory, "manual-root");
                values.LR2CustomFolderOutputBaseDir = normalBase;
                values.LR2CustomFolderOutputBaseDirRootType = rootBase;
                values.EnablePlaylistUrlCompletion = false;
            });
            library = new TestBmsLibrary(songPath, configOwner, null, null,
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath,
                () => outputSettings, configOwner,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () =>
                {
                    if (holdNotification && failurePoint == "bmt") { entered.TrySetResult(); release.Wait(); }
                    return new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt };
                },
                settings: settings);
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
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) =>
            {
                if (holdNotification && failurePoint != "bmt") { entered.TrySetResult(); release.Wait(); }
                throw new IOException("optional continuation notification failed");
            };
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
                await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(async () => await dialog.SaveAndApplyAsync());
            }
            else { await Assert.ThrowsExceptionAsync<IOException>(async () => await dialog.SaveAndApplyAsync()); }
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
            }
            if (failurePoint == "migration") { Assert.IsTrue(Directory.Exists(a)); }
            if (failurePoint == "config")
            {
                Assert.IsFalse(Directory.Exists(a));
                Assert.IsTrue(Directory.GetFiles(b, "*.lr2folder").Length > 0);
                LR2Config disk = new(configPath);
                CollectionAssert.Contains(disk.GetBMSSearchDirectoriesForChangeTracking(), a);
                CollectionAssert.DoesNotContain(disk.GetBMSSearchDirectoriesForChangeTracking(), b);
            }
            if (editAgain) { dialog.output_dir = "C"; }
            holdNotification = true;
            saving = dialog.SaveAndApplyAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, saving, "property-save.pending-output-failure");
            Assert.IsFalse(saving.IsCompleted);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out IDisposable rejected));
            rejected?.Dispose();
            Assert.IsFalse(Directory.Exists(c));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
            }
            release.Set();
            if (failurePoint == "bmt")
            {
                await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(async () => await saving);
            }
            else { await Assert.ThrowsExceptionAsync<IOException>(async () => await saving); }
            Assert.AreEqual(editAgain ? "C" : "B", dialog.output_dir);
            blocker?.Dispose();
            blocker = null;
            if (failurePoint == "migration") { File.Delete(b); }
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release.Reset();
            saving = dialog.SaveAndApplyAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, saving, "property-save.pending-output-success");
            Assert.IsFalse(saving.IsCompleted);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out rejected));
            rejected?.Dispose();
            Assert.IsFalse(Directory.Exists(c));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("B", db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).Output_dir);
            }
            holdNotification = false;
            release.Set();
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await saving);
            string finalDirectory = editAgain ? c : b;
            Assert.IsFalse(Directory.Exists(a));
            if (editAgain) { Assert.IsFalse(Directory.Exists(b)); }
            Assert.IsTrue(Directory.GetFiles(finalDirectory, "*.lr2folder").Length > 0);
            LR2Config finalConfig = new(configPath);
            CollectionAssert.DoesNotContain(finalConfig.GetBMSSearchDirectoriesForChangeTracking(), a);
            CollectionAssert.Contains(finalConfig.GetBMSSearchDirectoriesForChangeTracking(), finalDirectory);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
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

    /// <summary>DB確定後の必須header公開で故障し、同じ原因を返してDB・live・参照・dialog resetをNEWへ保ちます。未変更の次保存で旧公開を再実行しません。故障内でDB NEWも確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PostSaveFailureReconcilesDialogWithDurableActiveState()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        TestBmsPlaylist? playlistForCleanup = null;
        TestBmsLibrary? libraryForCleanup = null;
        BMSTable? subscribedTable = null;
        PropertyChangedEventHandler? headerPublication = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7313,
                name = "PostSaveFailure",
                symbol = "OLD",
                Output_dir = "PostSaveFailure",
                entries = [CreateEntry("ffffffffffffffffffffffffffffffff", "Folder")]
            };
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            libraryForCleanup = library;
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(), settings: testSettings)
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
                using LR2SongDBExtended committed = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

            service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                symbol = "NEW"
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreSame(expectedFailure, failure);
            Assert.AreEqual(1, headerPublicationCount);
            Assert.AreEqual("NEW", table.symbol);
            using (LR2SongDBExtended durable = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
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
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                compat_prefix = "★"
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test remap presentation failure", failure.Message);
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(1, remapRequestCount);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                seed.InsertOrReplace(prefixedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => new TestBmsLibrary(songDbPath, settings: testSettings),
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
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                compat_prefix = "★"
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test prefix rewrite notification failure", failure.Message);
            Assert.AreEqual("★Alpha", table.entries.Single(candidate => candidate.md5 == entry.md5).folder);
            Assert.AreEqual("★★Alpha", table.entries.Single(candidate => candidate.md5 == prefixedEntry.md5).folder);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual("★Alpha", table.entries.Single(candidate => candidate.md5 == entry.md5).folder);
            Assert.AreEqual("★★Alpha", table.entries.Single(candidate => candidate.md5 == prefixedEntry.md5).folder);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => new TestBmsLibrary(songDbPath, settings: testSettings),
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
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                compat_prefix = "★"
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());
            table.symbol = "CONCURRENT";

            InvalidOperationException stale = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
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
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalRetry\",\r\n\"symbol\":\"ER\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd\",\"title\":\"External Retry Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 7315;
            table.Page_url = new Uri(headerJsonPath);
            table.DisableExternalSync();
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
            }
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            bool failConfirmation = true;
            service.ExternalSyncConfirmationRequested += (_, request) =>
            {
                if (failConfirmation) { throw new IOException("confirmation unavailable"); }
                request.Confirmed = true;
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
            service.PlaylistPropertyExternalSyncFailed += (_, _) => { };
            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            service.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            int summaryRefreshCount = 0;
            service.PlaylistPropertySummaryDataRefreshRequested += (_, request) =>
            {
                if (string.Equals(request.Reason, "playlist_property_resync", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref summaryRefreshCount);
                }
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session);
            Assert.ThrowsException<IOException>(() => dialog.is_external_sync = true);
            Assert.IsFalse(dialog.is_external_sync);
            Assert.IsFalse(table.is_external_sync);
            failConfirmation = false;
            dialog.is_external_sync = true;

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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
    /// 単表保存の未分類取得失敗でも属性DB保存と必要BMT出力を終え、保存済み値をdialogVMへ採用してから同期未完了を返します。
    /// Resetと次の明示保存は保存済み基準を使い、一般同期から取得失敗を外す変更でこの失敗を成功へ消す誤りを検出します。
    /// 実Windowのfalse結果と寿命は既存terminal操作の接続・回帰に分担し、ここでは実SQLite・出力・service・dialogVMを観測します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_ExternalSourceFailureAdoptsSavedValuesAndCompletesRequiredOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistCustomFolderOutputTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            string bmtDirectory = Path.Combine(directory, "bmt");
            File.WriteAllText(header, "{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var output = new CustomFolderOutputSettingsSnapshot();
            var playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmtDirectory },
                () => output, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty), settings: settings);
            var library = new TestBmsLibrary(songPath, settings: settings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(new Uri(header).AbsoluteUri));
            table.playlist_id = 7316;
            table.DisableExternalSync();
            table.is_bmt_output = true;
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);
            string oldHash = table.data_sha256;
            File.WriteAllText(header, "{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"bad%00-data.json\"}");
            var service = new PlaylistPropertySaveService(() => playlist, () => library, () => null, () => output);
            service.ExternalSyncConfirmationRequested += (_, request) => request.Confirmed = true;
            service.PlaylistPropertySyncStarted += (_, _) => { };
            service.PlaylistPropertySyncProgressChanged += (_, _) => { };
            service.PlaylistPropertySyncFinished += (_, _) => { };

            service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
            service.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            PlaylistPropertyExternalSyncFailedEventArgs? reportedFailure = null;
            string? symbolAtFailure = null;
            bool enabledAtFailure = false;
            service.PlaylistPropertyExternalSyncFailed += (_, failure) =>
            {
                reportedFailure = failure;
                symbolAtFailure = dialog?.symbol;
                enabledAtFailure = dialog?.is_external_sync == true;
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session);
            dialog.symbol = "Saved";
            dialog.is_external_sync = true;
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => dialog.SaveAndApplyAsync());
            Assert.IsNotNull(reportedFailure);
            Assert.IsInstanceOfType<ArgumentException>(reportedFailure.Exception);
            Assert.AreEqual("Saved", symbolAtFailure);
            Assert.IsTrue(enabledAtFailure);
            Assert.AreEqual("Saved", session.OriginalValues.Symbol);
            Assert.IsTrue(session.OriginalValues.IsExternalSync);
            Assert.IsNull(session.PendingOutput);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.AreEqual(oldHash, table.data_sha256);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                BMSTable saved = db.Table<BMSTable>().Single();
                Assert.AreEqual("Saved", saved.symbol);
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
            Assert.IsTrue(dialog.is_external_sync);
            // 次保存では保存済みの外部同期有効化を再実行せず、入力が壊れたままでも新しい属性を保存できる。
            dialog.symbol = "Next";
            reportedFailure = null;
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.IsNull(reportedFailure);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
            Assert.AreEqual("Next", verify.Table<BMSTable>().Single().symbol);
        }
        finally
        {
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>ルート型の実出力ファイルを表ディレクトリの子として登録し、その表ディレクトリをLR2ルート直下へ置きます。通常型の基点階層を流用する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsRootOutputRowsUnderTableDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootCustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "RootFolderTable",
                symbol = "RFT",
                Output_dir = "RootFolderTable",
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(rootOutputBaseDir, "RootFolderTable", "0001.lr2folder");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
            AssertClearFolderCommandMatchesAssistAndEasyRows(songDbPath, assistText, easyText);
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
            string bpSortText = ReadShiftJisText(Path.Combine(outputDir, "BP SORT", "0000.lr2folder"));
            StringAssert.Contains(bpSortText, "score.minbp IS NULL ASC");
            string playCountSortText = ReadShiftJisText(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder"));
            StringAssert.Contains(playCountSortText, "score.playcount IS NULL ASC");
            StringAssert.Contains(playCountSortText, "score.playcount DESC");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder
                | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder
                | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder;
            using (var db = new LR2SongDBExtended(songDbPath))
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
            BMSTable table = new PlaylistPersistenceRepository(songDbPath).LoadPlaylistHeaders().Single();
            Assert.IsFalse(table.ArePlaylistEntriesLoaded);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
            using var verify = new LR2SongDBExtended(songDbPath);
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

    /// <summary>導入済み履歴表を参照する最終プレイ降順と欠損末尾のSQLを実ファイル・DBへ出力し、プレイ回数sortを代用しません。異なる日時・回数への置換を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesLastPlaySortFolderWhenSchemaInstalled()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            var playlist = new TestBmsPlaylist(songDbPath, scoreDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            var playlist = new TestBmsPlaylist(songDbPath, scoreDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_last_play_schema_missing");

            string outputDir = Path.Combine(outputBaseDir, "MissingLastPlaySchema");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            var playlist = new TestBmsPlaylist(songDbPath, scoreDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
            using (var seed = new LR2SongDBExtended(songDbPath))
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LegacyDisabled");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BP SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string expectedPath = Path.Combine(outputDir, "0001.lr2folder");
            Assert.IsTrue(File.Exists(expectedPath));
            Assert.IsFalse(File.Exists(stalePath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "HierarchyPrune");
            string clearDir = Path.Combine(outputDir, "CLEAR FOLDER");
            string noPlayDir = Path.Combine(clearDir, "0 NO PLAY");
            string noPlayFile = Path.Combine(noPlayDir, "0000.lr2folder");
            using (LR2SongDBExtended verifyInitial = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            var playlist = new TestBmsPlaylist(
                songDbPath,
                new Lr2ConfigOwner(CreateLr2Config(lr2RootPath, bmsRoot)),
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_bulk_rewrite");

            string text = ReadShiftJisText(outputFile);
            Assert.AreNotEqual(oldTimestamp, File.GetLastWriteTimeUtc(outputFile));
            Assert.IsTrue(text.IndexOf("#TITLE stale", StringComparison.Ordinal) < 0);
            StringAssert.Contains(text, "#TITLE BulkRewrite ALL");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>実カスタムフォルダ同期の進捗に専用ラベルを届け、全体song DB同期の表示を流用しません。処理内容を誤って案内する接続を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_ProgressUsesFolderTableSyncLabel()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            BMSPlaylist playlist = CreatePlaylist(songDbPath, synchronization, new Lr2ConfigOwner(CreateLr2Config(lr2RootPath, bmsRoot)), settings: testSettings);
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            var progressLabels = new List<string>();

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB(
                [table],
                "test_progress_label",
                (_, _, label) => progressLabels.Add(label));

            CollectionAssert.Contains(progressLabels, Resources.Custom_folder_db_sync_progress_single_label);
            Assert.IsFalse(progressLabels.Contains(Resources.Lr2_song_db_sync_status_running));
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    entry.playlist_id = table.playlist_id;
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput"),
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                 () => new BeatorajaBmtOptionsSnapshot(),
                 () => outputSettings,
                 synchronization, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_reload_root_status_current");
            string outputDirectory = Path.Combine(providerRootOutputBaseDir, "ReloadRootStatusCurrent");
            string outputFile = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime outputFileTimestamp = File.GetLastWriteTimeUtc(outputFile);
            string outputDirectoryRowPath = Lr2FolderPath.ToFolderPath(outputDirectory);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("DELETE FROM folder WHERE path = ?;", outputDirectoryRowPath);
            }
            config.SetBMSSearchDirectories([bmsRoot]);
            config.Save(config.ConfigFilePath);
            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);

            var persistedConfig = new LR2Config(config.ConfigFilePath);
            CollectionAssert.Contains(persistedConfig.GetBMSSearchDirectories(), outputDirectory);
            CollectionAssert.DoesNotContain(persistedConfig.GetBMSSearchDirectories(), settingsRootOutputBaseDir);
            Assert.AreEqual(outputFileTimestamp, File.GetLastWriteTimeUtc(outputFile));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>実生成済み表の出力を削除すると保存した出力状態も除去します。次の修復判断が消した出力を現行扱いする誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_ClearsCustomFolderOutputStatus()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    1L,
                    before.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                        table.playlist_id.Value));
            }

            playlist.RemoveCustomFolder(table);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            var playlist = new TestBmsPlaylist(songDbPath, new Lr2ConfigOwner(CreateLr2Config(lr2RootPath, bmsRoot)), CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_output");
            string outputDir = Path.Combine(outputBaseDir, "DisableAllOutput");
            Assert.IsTrue(Directory.Exists(outputDir));
            Assert.IsTrue(Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using (LR2SongDBExtended verifySeed = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_output");

            Assert.IsFalse(Directory.Exists(outputDir)
                && Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>ルート型の物理出力を先に失った状態から全種類を除外し、相対パスで残るDB階層を整理します。物理不在時の旧DB行放置を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesDbOnlyRootRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string rootOutputBaseDir = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_root_output");
            string outputDir = Path.Combine(rootOutputBaseDir, "DisableAllRootOutput");
            string relativeOutputDir = Path.Combine("LR2files", "CustomFolder", "DisableAllRootOutput");
            using (LR2SongDBExtended verifySeed = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
            }
            Directory.Delete(outputDir, recursive: true);

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_root_output");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            var playlist = new TestBmsPlaylist(songDbPath, new Lr2ConfigOwner(CreateLr2Config(lr2RootPath, bmsRoot)), CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_commit_failure");
            using (LR2SongDBExtended verifySeed = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = "sentinel", title = "Preserved", type = 2, adddate = 123 });
            }
            // Dispatcherを持たない既存スケジューラーで生産値を観測する。全中間値のUI描画は要求しない。
            var library = new TestBmsLibrary(songDbPath, null, null, null, null, new WpfUiScheduler(() => null), settings: testSettings);
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
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => outputSettings, null, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(tables)
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);

            Lr2SongDbSyncPreparedDataSurface surface;
            using (LibraryFileMutationLease preparationLease = library.Lr2Synchronization.TryBeginMutation("test_actual_preparation", showMessage: false))
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
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM playlist_custom_folder_output_status;"));
                Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count());
                LR2SongDB.folder sentinel = verify.Find<LR2SongDB.folder>("sentinel");
                Assert.AreEqual("Preserved", sentinel.title);
                Assert.AreEqual(123, sentinel.adddate);
            }
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation("test_actual_preparation_released", showMessage: false);
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var library = new TestBmsLibrary(songDbPath, null, null, null, null, new WpfUiScheduler(() => null), settings: testSettings);
            var stages = new List<Lr2SongDbSyncStatusSnapshot>();
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStatusVersion))
                {
                    stages.Add(library.GetLr2SongDbSyncStatusSnapshot());
                }
            };
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                getOutputSettings, null, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);

            Lr2SongDbSyncPreparedDataSurface? surface = null;
            using (LibraryFileMutationLease preparationLease = library.Lr2Synchronization.TryBeginMutation("test_empty_snapshot", showMessage: false))
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
                "test_empty_snapshot_after_release",
                showMessage: false);
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
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
            Lr2ConfigOwner lr2ConfigProvider = new(CreateLr2Config(lr2RootPath, bmsRoot));
            var library = new TestBmsLibrary(songDbPath, lr2ConfigProvider, settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), lr2ConfigProvider, settings: testSettings)
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
            using (var db = new LR2SongDBExtended(songDbPath))
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
            using (LibraryFileMutationLease preparationLease = library.Lr2Synchronization.TryBeginMutation("test_external_colocated", showMessage: false))
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder extraFolder = verify.Find<LR2SongDB.folder>(extraFile);
            Assert.IsNotNull(extraFolder);
            Assert.AreEqual("External", extraFolder.title);
            Assert.AreEqual(2, extraFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir), extraFolder.parent);
            Assert.AreEqual(extraTimestamp.ToUnixtime(), extraFolder.date);
            Assert.AreEqual(extraTimestamp.ToUnixtime(), extraFolder.adddate);
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation(
                "test_external_colocated_after_release",
                showMessage: false);
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

    /// <summary>旧保存値に包含がある場合、単独・一括出力、出力削除、表削除、検索root準備を副作用前に拒否し、正本と旧ファイル・DB行を保ちます。</summary>
    [DataTestMethod]
    [DataRow("single")]
    [DataRow("bulk")]
    [DataRow("remove")]
    [DataRow("table")]
    [DataRow("roots")]
    [TestCategory("Playlist")]
    public async Task SavedNestedOutput_RejectsOutputAndRemovalBeforeMutation(string operation)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var parentTable = new BMSTable
            {
                playlist_id = 7607,
                name = "NestedParent",
                symbol = "NP",
                Output_dir = "NestedParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7608,
                name = "NestedChild",
                symbol = "NC",
                Output_dir = Path.Combine("NestedParent", "NestedChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")
                ],
                Folder_order = ["Child Folder"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, new Lr2ConfigOwner(CreateLr2Config(lr2RootPath, bmsRoot)), CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { parentTable, childTable })
            };
            // 旧保存値を直接準備し、不正な配置の生成成功を前提にしません。
            parentTable.is_root_folder = childTable.is_root_folder = operation == "roots";
            testSettings.LR2CustomFolderOutputBaseDirRootType = outputBaseDir;
            playlist.CommitBMSTableHeadersToDB([parentTable, childTable]);
            string childOutputDir = Path.Combine(outputBaseDir, "NestedParent", "NestedChild");
            string childOutputFile = Path.Combine(childOutputDir, "0000.lr2folder");
            Directory.CreateDirectory(childOutputDir);
            File.WriteAllText(childOutputFile, "preserved old output");
            DateTime childTimestamp = File.GetLastWriteTimeUtc(childOutputFile);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Insert(new LR2SongDB.folder { path = childOutputFile, title = "old child", type = 2 });
                db.Insert(new LR2SongDB.folder { path = Lr2FolderPath.ToFolderPath(childOutputDir), title = "old directory", type = 1 });
            }
            int notices = 0;
            parentTable.PropertyChanged += (_, _) => notices++;
            switch (operation)
            {
                case "single": Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFolderAndCommitToDB(parentTable)); break;
                case "bulk": Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFoldersAndCommitHeadersToDB([parentTable, childTable], "invalid_saved_layout")); break;
                case "remove": Assert.ThrowsException<InvalidOperationException>(() => playlist.RemoveCustomFolder(parentTable)); break;
                case "roots": Assert.ThrowsException<InvalidOperationException>(() => playlist.PrepareCustomFolderOutputSearchRoots(outputBaseDir, [], CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings))); break;
                case "table":
                    var dialogs = new BlockingConfirmationDialogService();
                    dialogs.ReleaseConfirmation();
                    var removal = new PlaylistRemovalWorkflowOwner(dialogs, () => playlist, () => null!, () => null!,
                        () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings));
                    removal.OperationNotificationPresentationRequested += (_, _) => { };
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => removal.RemoveSummaryRowsAsync([new() { TableRef = parentTable }]));
                    break;
            }
            Assert.AreEqual(0, notices);
            Assert.AreEqual(2, playlist.BMSTables.Count);
            Assert.AreEqual("preserved old output", File.ReadAllText(childOutputFile));
            Assert.AreEqual(childTimestamp, File.GetLastWriteTimeUtc(childOutputFile));
            Assert.AreEqual(1, Directory.GetFiles(outputBaseDir, "*", SearchOption.AllDirectories).Length);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2, verify.Table<BMSTable>().Count());
            Assert.AreEqual("old child", verify.Table<LR2SongDB.folder>().Single(row => row.path == childOutputFile).title);
            Assert.AreEqual(2, verify.Table<LR2SongDB.folder>().Count());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実詳細編集のDB保存、LR2出力、BMT準備・取消を分け、DB前失敗だけ旧levelへ戻します。空receiptのconsumer故障と実LR2警告の表示・診断故障を区別し、元結果を保って配送終端まで受付と編集状態を保持し、次の明示編集を保存します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    // 空receiptでは表示を試みずconsumerだけを故障させる。実表示故障はLR2書込み失敗から生成されたWarningへ重ねる。
    [DataRow("none")]
    [DataRow("preparation")]
    [DataRow("output")]
    [DataRow("empty_receipt_consumer")]
    [DataRow("output_notification")]
    [DataRow("bmt_preparation")]
    [DataRow("bmt_cancel")]
    [DataRow("bmt_cancel_empty_receipt_consumer")]
    [DataRow("database_empty_receipt_consumer")]
    public async Task DetailEdit_ReoutputsLr2FolderRowsForLevelProjection(string failureStage)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        FileStream? outputBlocker = null;
        Task? edit = null;
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var receiptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            bool failEmptyConsumer = failureStage.EndsWith("empty_receipt_consumer", StringComparison.Ordinal);
            bool failPresentation = failureStage == "output_notification";
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                if (injectFailure && failureStage == "preparation") { throw preparationFailure; }
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
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
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(songDbPath, null, null, null,
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }, settings: testSettings);
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                getOutputSettings, beatorajaBmtOptionsProvider: () =>
                {
                    if (injectFailure && failureStage == "bmt_preparation") { throw bmtPreparationFailure; }
                    if (injectFailure && failureStage is "bmt_cancel" or "bmt_cancel_empty_receipt_consumer")
                    {
                        // 実DB確定とLR2終端の後、BMT workerのshutdown判定で取消にします。
                        using (LR2SongDBExtended committedDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                        {
                            levelBeforeBmtCancellation = committedDb.Table<BMSTableEntry>().Single().level;
                        }
                        (playlist ?? throw new InvalidOperationException("The playlist was not initialized before BMT cancellation."))
                            .RequestShutdown("detail-post-commit-bmt-test");
                    }
                    return new BeatorajaBmtOptionsSnapshot();
                }, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            var diagnostics = new List<(Exception Exception, string Diagnostic)>();
            workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
                reloadFailureLog: (exception, diagnostic) =>
                {
                    diagnostics.Add((exception, diagnostic));
                    if (failureStage is "empty_receipt_consumer" or "output_notification") { throw diagnosticFailure; }
                },
                playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            var entryDeliveries = new List<string>();
            workspace.DetailWorkObserver = kind => entryDeliveries.Add(kind);
            var source = new PlaylistDetailSourceRow(entry, resolvedChart: null);
            workspace.DetailViewState.Source.Rows = [source];
            var context = new MainChartListCellEditContext(source.CreateViewRow(), nameof(PlaylistDetailRow.Level),
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            var receipts = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            var displayAttempts = new List<PlaylistOperationNotificationOwner.OperationNotification>();
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                receipts.Add(request);
                // 実MainWindowと同じく要素だけを表示対象にし、空eventを表示失敗の根拠にしません。
                foreach (PlaylistOperationNotificationOwner.OperationNotification notification in request.Receipt.Notifications)
                {
                    displayAttempts.Add(notification);
                    if (failPresentation)
                    {
                        receiptEntered.TrySetResult();
                        releaseReceipt.Task.GetAwaiter().GetResult();
                        throw presentationFailure;
                    }
                }
                if (failEmptyConsumer)
                {
                    if (failureStage == "empty_receipt_consumer")
                    {
                        receiptEntered.TrySetResult();
                        releaseReceipt.Task.GetAwaiter().GetResult();
                    }
                    throw consumerFailure;
                }
            };
            workspace.BeginDetailEdit(context);
            if (failureStage is "output" or "output_notification")
            {
                string outputFile = Directory.GetFiles(Path.Combine(providerOutputBaseDir, table.Output_dir), "*.lr2folder", SearchOption.AllDirectories)
                    .First(path => ReadShiftJisText(path).Contains("LEVEL 1", StringComparison.Ordinal));
                outputBlocker = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            if (failureStage == "database_empty_receipt_consumer")
            {
                using var failDatabase = new LR2SongDBExtended(songDbPath);
                failDatabase.Execute("CREATE TRIGGER fail_detail_save BEFORE INSERT ON playlist_entry BEGIN SELECT RAISE(FAIL, 'forced detail DB failure'); END;");
            }
            injectFailure = true;
            edit = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "12", commit: true));
            Task? mutationIdle = null;
            if (failureStage is "empty_receipt_consumer" or "output_notification")
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(receiptEntered.Task, edit, "detail-edit.receipt-presentation");
                Assert.IsFalse(edit.IsCompleted, "consumerまたは実表示の終端前に受理Taskを完了させません。");
                using (LR2SongDBExtended committedDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(12, committedDb.Table<BMSTableEntry>().Single().level);
                }
                Assert.IsTrue(workspace.DetailViewState.Source.IsPlaylistCellEditing);
                bool acceptedWhilePresenting = playlist.TryEnterPlaylistMutation(out IDisposable rejected);
                rejected?.Dispose();
                Assert.IsFalse(acceptedWhilePresenting);
                mutationIdle = playlist.WaitForPlaylistMutationIdleAsync();
                Assert.IsFalse(mutationIdle.IsCompleted, "通知配送中は受付とcapabilityを保持します。");
                releaseReceipt.TrySetResult();
            }
            Exception? observedFailure = null;
            try { await edit; }
            catch (Exception exception) { observedFailure = exception; }
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
            if (failEmptyConsumer || failPresentation)
            {
                Assert.AreEqual(1, diagnostics.Count);
                Assert.AreSame(failPresentation ? presentationFailure : consumerFailure, diagnostics[0].Exception,
                    "表示または空consumerの原因は元の操作結果へ合成せず局所診断します。");
                StringAssert.Contains(diagnostics[0].Diagnostic, "playlist detail cell edit notification");
            }
            else { Assert.AreEqual(0, diagnostics.Count); }
            Assert.AreEqual(1, receipts.Count);
            Assert.AreEqual("playlist detail cell edit notification", receipts[0].RouteName);
            if (failureStage is "output" or "output_notification")
            {
                Assert.IsFalse(receipts[0].Receipt.IsEmpty);
                PlaylistOperationNotificationOwner.OperationNotification warning = receipts[0].Receipt.Notifications.Single();
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, warning.Severity);
                Assert.AreEqual(Resources.MessageBoxTitle_Warning, warning.Caption);
                Assert.AreEqual(string.Format(Resources.Warn_CustomFolderOutputFailed, table.name,
                    Path.Combine(providerOutputBaseDir, table.Output_dir)), warning.Message);
                Assert.AreSame(warning, displayAttempts.Single());
            }
            else
            {
                Assert.IsTrue(receipts[0].Receipt.IsEmpty, "空receipt consumerの故障を存在しないdialogの表示故障にしません。");
                Assert.AreEqual(0, displayAttempts.Count);
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

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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
                using (var database = new LR2SongDBExtended(songDbPath)) { database.Execute("DROP TRIGGER fail_detail_save;"); }
                failEmptyConsumer = false;
                failPresentation = false;
                workspace.BeginDetailEdit(context);
                edit = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "12", commit: true));
                await edit;
                Assert.AreEqual(12, entry.level);
                Assert.AreEqual("12", ((PlaylistDetailRow)context.Row).Level);
                Assert.AreEqual("12", workspace.DetailViewState.Source.Rows.Single().CreateViewRow().Level);
                Assert.AreEqual(2, receipts.Count);
                Assert.IsTrue(receipts[1].Receipt.IsEmpty);
                using LR2SongDBExtended verifyRetry = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(12, verifyRetry.Table<BMSTableEntry>().Single().level, "同じ入力の再保存も実DBへ反映します。");
            }
            else if (failureStage is "empty_receipt_consumer" or "output_notification")
            {
                failEmptyConsumer = false;
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
                Assert.AreEqual(2, receipts.Count);
                Assert.IsTrue(receipts[1].Receipt.IsEmpty);
                Assert.AreEqual(failureStage == "output_notification" ? 1 : 0, displayAttempts.Count, "次の空receiptで表示を試みません。");
                Assert.AreEqual(1, diagnostics.Count, "次の明示保存へ以前の表示・診断失敗を持ち越しません。");
                Assert.IsFalse(workspace.DetailViewState.Source.IsPlaylistCellEditing);
                using LR2SongDBExtended verifyNext = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    private static string CreateBaseScoreDbPath(string tempDirectory)
    {
        string scoreDbPath = Path.Combine(tempDirectory, "score.db");
        using (var db = new LR2ScoreDBExtended(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        return scoreDbPath;
    }

    private static string CreateInstalledPlayHistoryScoreDbPath(string tempDirectory)
    {
        string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
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

    private static void AssertClearFolderCommandMatchesAssistAndEasyRows(string songDbPath, string assistFolderText, string easyFolderText)
    {
        const string AssistOnlyHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string AssistEasyHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string AssistNoEasyHash = "cccccccccccccccccccccccccccccccc";
        const string AssistNullHistoryHash = "dddddddddddddddddddddddddddddddd";
        string assistCommand = ReadCustomFolderCommand(assistFolderText);
        string easyCommand = ReadCustomFolderCommand(easyFolderText);

        using var db = new LR2SongDBExtended(songDbPath);
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

    private async Task PlaylistPropertyDialogApplyPostSaveUpdatesCoreAsync()
    {
        Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
            string initialNormalOutputBaseDir = Path.Combine(tempDirectory, "InitialNormalCustomFolder");
            string initialRootOutputBaseDir = Path.Combine(tempDirectory, "InitialRootCustomFolder");
            string changedNormalOutputBaseDir = Path.Combine(tempDirectory, "ChangedNormalCustomFolder");
            string changedRootOutputBaseDir = Path.Combine(tempDirectory, "ChangedRootCustomFolder");
            string additionalOutputBaseDir = Path.Combine(tempDirectory, "AdditionalCustomFolder");
            string additionalOutputBaseName = Path.GetFileName(additionalOutputBaseDir);
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "GlobalNormalCustomFolder");
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot initialSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = initialNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = initialRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            CustomFolderOutputSettingsSnapshot changedSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = changedNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = changedRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            CustomFolderOutputSettingsSnapshot additionalSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = changedNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = changedRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs =
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBaseDir])
            };
            CustomFolderOutputSettingsSnapshot currentSettings = initialSettings;
            int viewModelProviderCallCount = 0;
            int playlistProviderCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getViewModelSettings = () =>
            {
                viewModelProviderCallCount++;
                return currentSettings;
            };
            Func<CustomFolderOutputSettingsSnapshot> getPlaylistSettings = () =>
            {
                playlistProviderCallCount++;
                return currentSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            BMSTableEntry extraEntry = CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder E");
            extraEntry.folder = "Folder E";
            var table = new BMSTable
            {
                playlist_id = 7312,
                name = "DialogSnapshotSettings",
                symbol = "DSS",
                Output_dir = "DialogSnapshotSettings",
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder D"),
                    extraEntry
                ],
                Folder_order = ["Folder D"]
            };
            string oldOutputPath = Path.Combine(initialNormalOutputBaseDir, table.Output_dir, "0000.lr2folder");
            Directory.CreateDirectory(Path.GetDirectoryName(oldOutputPath)!);
            File.WriteAllText(oldOutputPath, "#TITLE stale normal output", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getPlaylistSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(
                playlist,
                library,
                getViewModelSettings,
                () => configOwner);

            dialog = await workspace.OpenPropertyDialogAsync(table);
            Assert.IsFalse(playlist.IsWriteLockHeldBMSTables);
            CollectionAssert.AreEquivalent(
                new[] { "Folder D", "Folder E" },
                dialog.folder_order.ToArray());
            currentSettings = changedSettings;
            dialog.is_root_folder = true;
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            PlaylistPropertyDialogViewModel savedDialog = dialog;
            savedDialog.Dispose();
            Assert.IsFalse(playlist.IsWriteLockHeldBMSTables);
            dialog = null;

            string newOutputDirectory = Path.Combine(changedRootOutputBaseDir, table.Output_dir);
            Assert.IsTrue(viewModelProviderCallCount > 0);
            Assert.IsTrue(playlistProviderCallCount > 0);
            Assert.IsTrue(table.is_root_folder);
            Assert.AreEqual("#TITLE stale normal output", File.ReadAllText(oldOutputPath, Encoding.GetEncoding("shift_jis")),
                "受付時の適用配置へ接続し、古い編集時の基点のcleanupを再開しません。");
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDirectory, "0001.lr2folder")));
            Assert.IsFalse(Directory.Exists(Path.Combine(initialRootOutputBaseDir, table.Output_dir)));
            var reloadedConfig = new LR2Config(Path.Combine(tempDirectory, "LR2files", "Config", "config.xml"));
            CollectionAssert.Contains(reloadedConfig.GetBMSSearchDirectoriesForChangeTracking(), newOutputDirectory);

            currentSettings = additionalSettings;
            dialog = await workspace.OpenPropertyDialogAsync(table);
            PlaylistCustomFolderOutputBaseOption additionalBaseOption = dialog.OutputBaseOptions
                .Single(option => string.Equals(option.BaseName, additionalOutputBaseName, StringComparison.OrdinalIgnoreCase));
            dialog.is_root_folder = false;
            dialog.custom_folder_output_base_option = additionalBaseOption;
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            dialog.Dispose();
            dialog = null;

            string additionalOutputDirectory = Path.Combine(
                additionalOutputBaseDir,
                table.Output_dir ?? BMSTable.CreateDefaultOutputDirectoryName(table.name));
            Assert.IsTrue(Directory.Exists(additionalOutputDirectory));
            Assert.IsTrue(Directory.GetFiles(additionalOutputDirectory, "*.lr2folder", SearchOption.AllDirectories).Length > 0);
            using (LR2SongDBExtended verifyAdditional = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDBExtended.playlist persistedAdditional = verifyAdditional.Table<LR2SongDBExtended.playlist>()
                    .Single(row => row.playlist_id == table.playlist_id);
                Assert.AreEqual(additionalOutputBaseName, persistedAdditional.custom_folder_output_base_name);
                Assert.IsTrue(verifyAdditional.Table<LR2SongDB.folder>().Any(row =>
                    row.path.StartsWith(additionalOutputDirectory, StringComparison.OrdinalIgnoreCase)));
            }

            currentSettings = initialSettings;
            bool inlineSaved = (await workspace.CompleteSummaryPropertyEditAsync(
                new PlaylistSummaryRow { TableRef = table },
                nameof(PlaylistSummaryRow.Symbol),
                "DSS2",
                commit: true)).IsApplied;
            Assert.IsTrue(inlineSaved);
            Assert.AreEqual("DSS2", table.symbol);
            using (LR2SongDBExtended verifyInline = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    "DSS2",
                    verifyInline.Table<LR2SongDBExtended.playlist>()
                        .Single(row => row.playlist_id == table.playlist_id)
                        .symbol);
            }

            currentSettings = additionalSettings;
            dialog = await workspace.OpenPropertyDialogAsync(table);
            dialog.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            dialog.symbol = "DSS3";
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            dialog.Dispose();
            dialog = null;
            Assert.AreEqual(
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                table.ignore_folder_output);
            using (LR2SongDBExtended verifyMask = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    (int)LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    verifyMask.ExecuteScalar<int>(
                        "SELECT ignore_folder_output FROM playlist WHERE playlist_id = ?;",
                        table.playlist_id));
            }
        }
        finally
        {
            dialog?.Dispose();
            DispatcherHelper.UIDispatcher = previousDispatcher;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

}
