using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using Livet;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>実プレイリスト保存に伴うLR2ファイル・DB同期と、出力失敗後の明示保存継続を確認します。</summary>
/// <remarks>SQL本文、実ファイルの整理、SQLite確定は下位の自己転記では代替できません。画面なしの保存serviceを主に使い、共有Dispatcherを設定する代表だけ非並列で接続を確認します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class BmsPlaylistCustomFolderOutputTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

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

    private async Task PlaylistPropertyDialogApplyPostSaveUpdatesCoreAsync()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
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
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
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
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var playlist = new TestBmsPlaylist(databaseScope,
                songDbPath,
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getPlaylistSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            var library = new TestBmsLibrary(databaseScope, songDbPath, settings: testSettings);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(
                playlist,
                library,
                getViewModelSettings,
                () => configOwner,
                playlistWorkspaceDialogService: new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService());

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
            LR2Config reloadedConfig = TestLr2ConfigFactory.Load(Path.Combine(tempDirectory, "LR2files", "Config", "config.xml"));
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
            using (LR2SongDBExtended verifyAdditional = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
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
            using (LR2SongDBExtended verifyInline = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
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
            using (LR2SongDBExtended verifyMask = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
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
