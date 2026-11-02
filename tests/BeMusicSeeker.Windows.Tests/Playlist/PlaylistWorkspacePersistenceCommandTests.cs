using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistWorkspaceFixtureData;
using static BeMusicSeeker.TestSupport.Components.SharedPlaylistWorkspaceFixture;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;


/// <summary>backup・復元・export・BMT順序・dropの実確定と、summary buildの世代・取消・終端を検証します。</summary>
/// <remarks>実SQLite、出力、configの確定順と失敗後の事実は実資源を用い、UIは既存schedulerの受理・実行・完了を制御します。画面の所有modalと入力操作はWPF代表が分担し、process DB lock競争だけ非並列です。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class PlaylistWorkspacePersistenceCommandTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();
    /// <summary>実workerのrawからDispatcher適用へ接続し、通常・表示失敗・交換・停止でもAction後のscheduler実Taskより先にbuild idleへ抜けません。entry集計はworker、通知はUIに属します。</summary>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("failure")]
    [DataRow("hidden")]
    [DataRow("stop")]
    [DoNotParallelize]
    public async Task SummaryBuild_OwnsDispatcherApplicationThroughSchedulerCompletion(string outcome)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? apply = null;
        bool scheduledFromWorker = false, publishedOnUi = true;
        async Task Apply(Action action)
        {
            await execute.Task;
            await TestUiDispatcherHost.Dispatcher.InvokeAsync(action).Task;
            executed.TrySetResult();
            await complete.Task;
            if (outcome == "failure") { throw new InvalidOperationException("summary UI completion failure"); }
        }
        using PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => fixture.Playlist,
            playlistLibraryProvider: () => fixture.Library, presentationRefreshDeferredProvider: _ => true,
            restoreUiApplyScheduler: action =>
            {
                scheduledFromWorker = !TestUiDispatcherHost.Dispatcher.CheckAccess();
                apply = Apply(action);
                entered.TrySetResult();
                return apply;
            }, restoreUiThreadCheck: () => TestUiDispatcherHost.Dispatcher.CheckAccess());
        workspace.PlaylistSummaryView.CollectionChanged += (_, _) => publishedOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess();
        TestUiDispatcherHost.Invoke(() => workspace.RequestPlaylistSummaryMode(true));
        workspace.RebuildPlaylistSummaryView();
        Task idle = workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, idle, "summary-build-ui-scheduled");
            Assert.IsNotNull(apply);
            if (outcome == "hidden") { TestUiDispatcherHost.Invoke(() => workspace.RequestPlaylistSummaryMode(false)); }
            if (outcome == "stop") { workspace.StopPlaylistSummaryDataBuild(); }
            execute.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(executed.Task, apply, "summary-build-ui-executed");
            Assert.IsTrue(scheduledFromWorker);
            Assert.IsTrue(publishedOnUi);
            Assert.IsFalse(idle.IsCompleted);
            Assert.IsFalse(workspace.IsPlaylistSummaryDataBuildIdle);
            if (outcome is "normal" or "failure") { Assert.AreEqual("Prefix", workspace.PlaylistSummaryView.Single().Name); }
            else { Assert.AreEqual(0, workspace.PlaylistSummaryView.Count); }
            complete.TrySetResult();
            if (outcome == "failure")
            { Assert.AreEqual("summary UI completion failure", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => apply)).Message); }
            else { await apply; }
            await idle;
        }
        finally
        {
            execute.TrySetResult(); complete.TrySetResult();
            try { if (apply != null) { await apply; } }
            catch (InvalidOperationException failure) when (outcome == "failure" && failure.Message == "summary UI completion failure") { }
            await idle;
        }
    }

    /// <summary>実SQL復元を正本・本番ツリー受信・必須hydration・LR2/BMT出力へ通し、新readerが出力を補正する前に観測します。続く新正本の明示編集保存と新readonly読取り、別表・song・利用者表・管理外ファイルの保全を確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? property = null;
        Task? restore = null;
        var auxiliary = new ConcurrentQueue<Task>();
        Exception? primaryFailure = null;
        try
        {
            string songDbPath = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.Execute("INSERT INTO playlist (playlist_id, name, symbol) VALUES (1, 'Before restore', 'before');");
                    db.Execute("INSERT INTO song(path, hash, title) VALUES ('unrelated.bms', 'cccccccccccccccccccccccccccccccc', 'kept song');");
                    db.Execute("CREATE TABLE user_data(key TEXT PRIMARY KEY, value TEXT);");
                    db.Execute("INSERT INTO user_data VALUES ('custom', 'kept user value');");
                });
            }
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(tempDirectory, "LR2");
            LR2Config config = TestLr2ConfigFactory.CreateLr2Config(lr2Root, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            string outputBase = Path.Combine(tempDirectory, "custom");
            string bmtRoot = Path.Combine(tempDirectory, "beatoraja");
            string bmtOutput = Path.Combine(bmtRoot, "table");
            Directory.CreateDirectory(bmtOutput);
            File.WriteAllBytes(Path.Combine(bmtRoot, "beatoraja.jar"), []);
            File.WriteAllText(Path.Combine(bmtRoot, BeatorajaConfigService.ConfigFileName), "{\"tablepath\":\"table\",\"other\":42,\"tableURL\":[\"https://example.com/unmanaged\"]}");
            string unmanaged = Path.Combine(bmtOutput, "unmanaged.bmt");
            File.WriteAllBytes(unmanaged, [4, 2, 8]);
            string unrelated = Path.Combine(tempDirectory, "unrelated.txt");
            File.WriteAllBytes(unrelated, [9, 6, 1]);
            var outputSettings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2Root,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "root-output")
            };
            var bmtSettings = new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = true,
                BeatorajaRootPath = bmtRoot,
                RegisterBeatorajaBmtUrls = true
            };
            playlist = new TestBmsPlaylist(databaseScope, songDbPath, configOwner, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => bmtSettings, () => outputSettings,
                PlaylistTestFixture.CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = new ObservableCollection<BMSTable>([new BMSTable { playlist_id = 1, name = "Before restore", symbol = "before" }]) };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { auxiliary.Enqueue(Task.Run(work)); return true; };
            library = new TestBmsLibrary(databaseScope, songDbPath);
            TestBmsPlaylist accepted = playlist;
            var propertyService = new PlaylistPropertySaveService(() => accepted, () => library, () => null, () => outputSettings);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => accepted, playlistLibraryProvider: () => library,
                propertySaveService: propertyService,
                referenceApplyScheduler: (_, work) => { auxiliary.Enqueue(Task.Run(work)); return true; },
                dispatchPresentation: action => TestUiDispatcherHost.Invoke(action),
                catalogNotificationQueue: action => TestUiDispatcherHost.Invoke(action),
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => accepted, () => accepted.BMSTables));

            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
            workspace.RebuildPlaylistSummaryView(runAsync: false);
            var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(workspace.PlaylistTreeTables)
                    && workspace.PlaylistTreeTables.Any(table => table.playlist_id == 2)) { replaced.TrySetResult(); }
            };
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            string backupPath = Path.Combine(tempDirectory, "restore.sql");
            int flags = (int)(LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder);
            string[] commands =
            [
                CreatePlaylistRestoreDump(2, "After restore", "after").Split('\v')[0],
                CreatePlaylistRestoreDump(3, "Other restored", "other").Split('\v')[0],
                "UPDATE playlist SET output_dir = 'RestoredA', ignore_folder_output = " + flags + ", is_bmt_output = 1, folder_order = '[\"Folder A\"]', bmt_sort = 0 WHERE playlist_id = 2;",
                "UPDATE playlist SET output_dir = 'RestoredB', ignore_folder_output = " + flags + ", is_bmt_output = 1, folder_order = '[\"Folder B\"]', bmt_sort = 1 WHERE playlist_id = 3;",
                "INSERT INTO playlist_entry(playlist_id, md5, title, artist, folder) VALUES (2, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Restored song A', 'Artist A', 'Folder A');",
                "INSERT INTO playlist_entry(playlist_id, md5, title, artist, folder) VALUES (3, 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'Restored song B', 'Artist B', 'Folder B');"
            ];
            File.WriteAllText(backupPath, string.Join("\v" + Environment.NewLine, commands), Encoding.UTF8);
            Assert.IsFalse(Directory.Exists(outputBase));
            restore = workspace.RestorePlaylistBackupAsync(backupPath);
            await restore;
            await TestUiDispatcherHost.AwaitNotificationAsync(replaced.Task, restore, "restored tree replacement");
            await playlist.StartupReadiness.WaitForRequiredPlaylistReadinessAsync();
            await playlist.WaitForPlaylistMutationIdleAsync();
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            while (auxiliary.TryDequeue(out Task? work)) { await work; }
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);

            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual("playlist restore notification", notifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                notifications[0].Receipt.Notifications.Single().Severity);
            CollectionAssert.AreEquivalent(new[] { "After restore", "Other restored" }, workspace.PlaylistTreeTables.Select(table => table.name).ToArray());
            CollectionAssert.AreEquivalent(new[] { 2, 3 }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId ?? 0).ToArray());
            foreach ((int id, string directory, string folderName, string hash, string title) in new[]
            {
                (2, "RestoredA", "Folder A", new string('a', 32), "Restored song A"),
                (3, "RestoredB", "Folder B", new string('b', 32), "Restored song B")
            })
            {
                BMSTable current = playlist.BMSTables.Single(table => table.playlist_id == id);
                Assert.AreEqual(id, current.entries.Single().playlist_id);
                Assert.AreEqual(hash, current.entries.Single().md5);
                Assert.IsTrue(current.FolderNodes.Any(node => node.FolderName == folderName));
                string lr2 = Path.Combine(outputBase, directory, "0001.lr2folder");
                StringAssert.Contains(File.ReadAllText(lr2, Encoding.GetEncoding("shift_jis")), "#TITLE " + folderName);
                using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                { Assert.AreEqual(folderName, verify.Table<LR2SongDB.folder>().Single(row => row.path == lr2).title); }
                string bmt = Directory.GetFiles(bmtOutput, "*.bmt").Where(path => path != unmanaged).Single(path =>
                {
                    using var compressed = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
                    using var reader = new StreamReader(compressed);
                    return Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).Value<string>("name") == current.name;
                });
                using var content = new GZipStream(File.OpenRead(bmt), CompressionMode.Decompress);
                using var text = new StreamReader(content);
                var document = Newtonsoft.Json.Linq.JObject.Parse(text.ReadToEnd());
                Newtonsoft.Json.Linq.JToken song = document.SelectToken("folder[0].songs[0]") ?? throw new AssertFailedException("restored BMT entry missing");
                Assert.AreEqual(hash, song.Value<string>("md5"));
                Assert.AreEqual(title, song.Value<string>("title"));
            }
            // ここまでの出力を観測してから新readerを作るため、初期化による修復で復元不良を隠さない。
            BMSTable restoredA = playlist.BMSTables.Single(table => table.playlist_id == 2);
            property = await workspace.OpenPropertyDialogAsync(restoredA) ?? throw new AssertFailedException("restored property dialog unavailable");
            property.symbol = "edited-after-restore";
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await property.SaveAndApplyAsync());
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                BMSTable restored = verify.Table<BMSTable>().Single(table => table.playlist_id == 2);
                Assert.AreEqual("After restore", restored.name);
                Assert.AreEqual("edited-after-restore", restored.symbol);
                Assert.AreEqual("other", verify.Table<BMSTable>().Single(table => table.playlist_id == 3).symbol);
                Assert.AreEqual(2, verify.Table<BMSTableEntry>().Single(entry => entry.md5 == new string('a', 32)).playlist_id);
                Assert.AreEqual("kept song", verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = 'unrelated.bms';"));
                Assert.AreEqual("kept user value", verify.ExecuteScalar<string>("SELECT value FROM user_data WHERE key = 'custom';"));
            }
            CollectionAssert.AreEqual(new byte[] { 4, 2, 8 }, File.ReadAllBytes(unmanaged));
            CollectionAssert.AreEqual(new byte[] { 9, 6, 1 }, File.ReadAllBytes(unrelated));
            Assert.IsFalse(databaseScope.IsSongGateEnteredByCurrentThread);
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (restore != null) { await CleanupAsync(() => restore); }
            if (playlist != null) { await CleanupAsync(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null) { await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync); }
            while (auxiliary.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { workspace?.RefreshPlaylistTreeTables(null); return Task.CompletedTask; });
            while (auxiliary.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { property?.Dispose(); return Task.CompletedTask; });
            await CleanupAsync(() => { playlist?.RequestShutdown("normal-restore-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { library?.RequestShutdown("normal-restore-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); } return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("normal restore cleanup", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    // DB Monitor の再取得は process-wide な所有確認のため、他 fixture の DB 保持から隔離する。
    /// <summary>実DB復元確定、UI集合適用、UI operation完了を別gateで観測し、待機中のDB lock解放とP保持、競合変更拒否、完了後の次保存を確認します。任意URL補完の予約が失敗しても必須復元の成功を取り消しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_AwaitsUiCompletionAndRejectsCompetingWrites(bool failOptionalScheduling)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, "playlist-restore-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? restore = null;
        int scheduleCount = 0;
        var scheduledApplies = new List<Task>();
        Task? registration = null;
        Task? competingRestore = null;
        bool assertionsCompleted = false;

        async Task ApplyAsync(Action action)
        {
            await execute.Task;
            action();
            executed.TrySetResult();
            await complete.Task;
        }
        try
        {
            string dbPath = Path.Combine(directory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(dbPath)) { }
            var oldTable = new BMSTable { playlist_id = 11, name = "Old live", symbol = "O" };
            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(databaseScope,
                dbPath, [oldTable], out BMSPlaylist playlist, out List<PlaylistOperationNotificationPresentationRequestedEventArgs>? notifications,
                action =>
                {
                    Interlocked.Increment(ref scheduleCount);
                    Task task = ApplyAsync(action);
                    lock (scheduledApplies) { scheduledApplies.Add(task); }
                    accepted.TrySetResult();
                    return task;
                });
            int optionalSchedulingCalls = 0;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref optionalSchedulingCalls);
                if (failOptionalScheduling) { throw new IOException("restore optional URL completion scheduling failed"); }
                return false;
            };
            bool notificationAfterAdmissionRelease = false;
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) =>
            {
                notificationAfterAdmissionRelease = playlist.TryEnterPlaylistMutation(out IDisposable notificationLease);
                notificationLease?.Dispose();
            };
            bool collectionOnUi = true;
            playlist.BMSTables.CollectionChanged += (_, _) =>
                collectionOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess();
            string file = Path.Combine(directory, "restore.sql");
            File.WriteAllText(file, CreatePlaylistRestoreDump(22, "Restored live", "R"), Encoding.UTF8);
            restore = workspace.RestorePlaylistBackupAsync(file);
            await TestUiDispatcherHost.AwaitNotificationAsync(accepted.Task, restore, "playlist.restore-ui-accepted");
            Assert.IsFalse(restore.IsCompleted);
            Assert.AreSame(oldTable, playlist.BMSTables.Single());
            Assert.AreEqual(0, notifications.Count);
            bool lockAvailable = await Task.Run(() =>
            {
                using IDisposable? songLease = databaseScope.TryEnterSong(TimeSpan.FromSeconds(1));
                return songLease != null;
            });
            Assert.IsTrue(lockAvailable, "UI completion 待機へ DB Monitor を持ち越さない。");
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(dbPath))
            {
                Assert.AreEqual(22, db.Table<BMSTable>().Single().playlist_id);
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => playlist.CommitBMSTableHeaderToDB(oldTable));
            Assert.ThrowsExactly<InvalidOperationException>(() => playlist.RemoveBMSTable(oldTable));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => playlist.ReloadTablesAsync());
            registration = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(
                new BMSTable { name = "Rejected registration", Output_dir = "RejectedRegistration" },
                renameDuplicateName: false, reason: "restore_exclusion");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                registration);
            competingRestore = playlist.RestorePlaylistDumpAsync(CreatePlaylistRestoreDump(33, "Rejected", "X"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                competingRestore);
            execute.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(executed.Task, restore, "playlist.restore-ui-executed");
            Assert.AreEqual("Restored live", playlist.BMSTables.Single().name);
            Assert.IsTrue(collectionOnUi);
            Assert.IsFalse(restore.IsCompleted);
            Assert.AreEqual(0, notifications.Count);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single()));
            complete.TrySetResult();
            Assert.IsTrue(await restore);
            Assert.IsTrue(notificationAfterAdmissionRelease, "結果通知へPを持ち越さない。");
            Assert.AreEqual(1, scheduleCount);
            Assert.AreEqual(1, optionalSchedulingCalls, "任意予約を再試行しない。");
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                notifications.Single().Receipt.Notifications.Single().Severity);
            playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single());
            assertionsCompleted = true;
        }
        finally
        {
            execute.TrySetResult();
            complete.TrySetResult();
            // 誤実装が競合 request を受理しても、先に全 gate を開き、その operation と callback を回収する。
            foreach (Task task in new[] { restore, registration, competingRestore }.OfType<Task>())
            {
                try { await task; }
                catch (InvalidOperationException) when (task == registration || task == competingRestore) { }
                catch when (!assertionsCompleted) { }
            }
            Task[] applies;
            lock (scheduledApplies) { applies = scheduledApplies.ToArray(); }
            foreach (Task apply in applies)
            {
                try { await apply; }
                catch when (!assertionsCompleted) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    // 実process lockの所有workerをfinallyで解放し、時間指定は停止監視にのみ使います。
    /// <summary>別workerの実DB lock待機中にUIから復元を開始して未完了Taskを返し、dispatcherの次処理が実行できてlock解放後に復元を完了します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistRestore_DatabaseLockWaitKeepsDispatcherResponsive()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, "playlist-restore-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var releaseLock = new ManualResetEventSlim();
        var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lockOwner = null;
        Task? restore = null;
        Task? dispatch = null;
        try
        {
            string dbPath = Path.Combine(directory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(dbPath)) { }
            CreateBackupWorkspace(databaseScope, dbPath, [], out BMSPlaylist playlist, out _);
            lockOwner = Task.Run(() =>
            {
                using IDisposable? songLease = databaseScope.TryEnterSong(TimeSpan.FromSeconds(5));
                if (songLease == null) { throw new TimeoutException("Test lock acquisition failed."); }
                lockEntered.TrySetResult();
                releaseLock.Wait();
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(lockEntered.Task, lockOwner, "playlist-restore.lock-owner");
            // 新 async owner API は admission を含む同期 DB 前半を worker へ渡す。
            dispatch = TestUiDispatcherHost.Dispatcher.InvokeAsync(() =>
            {
                restore = playlist.RestorePlaylistDumpAsync(CreatePlaylistRestoreDump(44, "Worker DB", "W"));
                Assert.IsFalse(restore.IsCompleted);
            }).Task;
            await dispatch;
            bool marker = false;
            await TestUiDispatcherHost.Dispatcher.InvokeAsync(() => marker = true).Task;
            Assert.IsTrue(marker);
            Assert.IsFalse(restore!.IsCompleted);
            releaseLock.Set();
            await lockOwner;
            await restore;
            Assert.AreEqual("Worker DB", playlist.BMSTables.Single().name);
        }
        finally
        {
            releaseLock.Set();
            try
            {
                await Task.WhenAll(lockOwner ?? Task.CompletedTask, dispatch ?? Task.CompletedTask, restore ?? Task.CompletedTask);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

}
