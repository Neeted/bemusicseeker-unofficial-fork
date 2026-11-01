using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>実SQLiteの保存・復元と、それを所有するplaylist/workspaceの通知・受付・必須準備を検証します。</summary>
/// <remarks>DBと出力の確定順は実SQLiteと必要なファイルで観測し、treeの実VM接続だけ共有UI構成を使います。画面操作は上位WPFケースが分担します。</remarks>
[TestClass]
public sealed class BmsPlaylistPersistenceLifecycleTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>実VMへ同じcollectionの更新とcollection自体の置換を配送し、tree参照・プロパティ通知・表示更新がそれぞれ追従することを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public void PlaylistTreeTables_TrackStoreReplacementAndPreserveCollectionIdentity()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([new BMSTable { name = "Initial" }])
            };
            using SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
            List<string> workspacePropertyNames = [];
            int playlistTablesPresentationChangedCount = 0;
            viewModel.PlaylistWorkspace.PropertyChanged += (_, e) => workspacePropertyNames.Add(e.PropertyName!);
            viewModel.PlaylistWorkspace.PlaylistTablesPresentationChanged += (_, _) => playlistTablesPresentationChangedCount++;

            Assert.AreEqual(0, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);

            viewModel.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);

            Assert.AreSame(playlist.BMSTables, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            Assert.AreEqual(1, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);
            CollectionAssert.AreEqual(
                new[] { nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables) },
                workspacePropertyNames);

            playlist.BMSTables.Add(new BMSTable { name = "Added" });
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreSame(playlist.BMSTables, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            Assert.AreEqual(2, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);
            Assert.AreEqual(1, playlistTablesPresentationChangedCount);
            ObservableCollection<BMSTable> replacement = new(
                [new BMSTable { name = "Replacement" }]);
            playlist.BMSTables = replacement;
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreSame(replacement, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            CollectionAssert.AreEqual(
                new[]
                {
                    nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables),
                    nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables),
                    nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables)
                },
                workspacePropertyNames);
            Assert.AreEqual(2, playlistTablesPresentationChangedCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実DBのヘッダー・行を親要求で読み直し、公開中の再入復元を拒否して別hydration予約を作らず必須行読込の終端まで待つことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_ReloadsHeadersWithoutScoreInitialization()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        testSettings.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var persistedTable = new BMSTable
            {
                playlist_id = 7001,
                name = "ReloadedTable",
                symbol = "R",
                Output_dir = "ReloadedTable"
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
            }
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[]
                    {
                        new BMSTable
                        {
                            playlist_id = 1,
                            name = "OldTable",
                            symbol = "O",
                            Output_dir = "OldTable"
                        }
                    })
            };
            bool hydrationQueued = false;
            playlist.StartupBackgroundTaskScheduler = (taskName, _, _, _) =>
            {
                hydrationQueued |= taskName == "playlist_entries_hydration";
                return true;
            };

            Task? rejectedRestore = null;
            playlist.BMSTables.CollectionChanged += (_, _) =>
            {
                // 実collection公開中の再入操作は、新しいDB復元を開始しません。
                rejectedRestore ??= playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceFixtureData.CreatePlaylistRestoreDump(77, "Rejected reload restore", "X"));
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedRestore, "restore rejection during publication");
                }
                catch (InvalidOperationException)
                {
                    // Busyによる明示失敗をevent終了後に対象Taskで検証します。
                }
            };
            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
            Assert.IsNotNull(rejectedRestore);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rejectedRestore!);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(7001, verify.Table<BMSTable>().Single().playlist_id);
            }

            Assert.IsFalse(hydrationQueued, "必須hydrationは親Taskが直接待ち、別予約を作りません。");
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationRequestedVersion);
            Assert.AreEqual(1, playlist.BMSTables.Count);
            Assert.AreEqual("ReloadedTable", playlist.BMSTables[0].name);
            Assert.IsTrue(playlist.BMSTables[0].ArePlaylistEntriesLoaded);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

}
