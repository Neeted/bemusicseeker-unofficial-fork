using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>保存済み表の出力先移行、実登録公開とBMT終端の確定状態を確認します。</summary>
/// <remarks>削除範囲と旧配置、SQL保存順は実ファイル・SQLiteで観測します。Windowを作らず実ownerまで接続し、描画・ダイアログ所有は上位の代表へ分担します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class BmsPlaylistMigrationAndRegistrationTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>実登録の一覧追加通知内でDB確定を読戻し、通知中の復元は拒否します。永続化前の公開と公開終端前の予約解放を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ExternalTableRegistration_PersistsBeforeVisibleCollectionNotification()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope,
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSTable table = new()
            {
                playlist_id = 7811,
                name = "Durable before visible",
                symbol = "DBV",
                Output_dir = "DurableBeforeVisible",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder")]
            };
            Task? rejectedRestore = null;
            bool visibleNotificationObserved = false;
            bool durableAtVisibleNotification = false;
            playlist.BMSTables.CollectionChanged += (_, change) =>
            {
                if (change.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                {
                    return;
                }

                visibleNotificationObserved = true;
                // 実 registration publication 内の復元は DB 前に拒否する。
                rejectedRestore = playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceFixtureData.CreatePlaylistRestoreDump(77, "Rejected registration restore", "X"));
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedRestore, "restore rejection during publication");
                }
                catch (InvalidOperationException)
                {
                    // 予約による明示失敗は event 終了後に対象 Task で検証する。
                }
                using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                durableAtVisibleNotification = verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist WHERE playlist_id = ?;",
                    table.playlist_id) == 1;
            };

            await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(
                table,
                renameDuplicateName: false,
                reason: "durable_before_visible");

            Assert.IsNotNull(rejectedRestore);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rejectedRestore!);
            Assert.IsTrue(visibleNotificationObserved);
            Assert.IsTrue(durableAtVisibleNotification);
            Assert.IsTrue(playlist.BMSTables.Contains(table));
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
