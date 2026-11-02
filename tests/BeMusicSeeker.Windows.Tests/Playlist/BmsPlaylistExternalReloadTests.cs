using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>実外部再取得のDB確定、UI置換、取消と必要出力の終端を確認します。</summary>
/// <remarks>実JSON・SQLiteと必要なHTTP本文を使い、UI配送はWindowを作らず共有schedulerで保持します。メニューや実Windowの接続は上位が担い、失敗した正本・公開・出力を別々に観測します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class BmsPlaylistExternalReloadTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 手動・任意同期でA成功とC取得失敗を確定し、Bの実HTTP本文待ち中に実Dispatcher上のsummaryへ
    /// 現参照・更新日時・STATUSを採用します。全件終了までPとTaskを保持し、重い詳細更新は終端に一度だけ要求します。
    /// summary要求の必須購読先が失敗する行でも他対象を完了させ、終端で元例外を返して次要求へ持ち越しません。
    /// A/C完了進捗からsummary buildを回収するため、終端一括採用への退行は待機継続ではなく旧表示の表明で検出します。
    /// 自動・手動とも0件は空名、成功Aと失敗Cの2/3終了では保持中Bを終了名にせず、B解放後に3/3を通知します。
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceReload_PublishesEachSummaryBeforeOtherTargetsComplete(bool automatic, bool failSummaryRequest)
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        var httpRequests = new List<Task>();
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); return ownedServer; }

        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SingleRequestHttpServer? server = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task? operation = null;
        TestBmsPlaylist? httpPlaylist = null;
        Exception? primaryFailure = null;
        try
        {
            Settings httpSettings = TestSettingsFactory.OpenSettings(Path.Combine(directory, "user.config"));
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string goodHeader = Path.Combine(directory, "good-header.json");
            string goodData = Path.Combine(directory, "good-data.json");
            string blockedHeader = Path.Combine(directory, "blocked-header.json");
            string blockedData = Path.Combine(directory, "blocked-data.json");
            string badHeader = Path.Combine(directory, "bad-header.json");
            const string oldData = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]";
            File.WriteAllText(goodHeader, "{\"name\":\"A success\",\"symbol\":\"A\",\"data_url\":\"good-data.json\"}");
            File.WriteAllText(blockedHeader, "{\"name\":\"B blocked\",\"symbol\":\"B\",\"data_url\":\"blocked-data.json\"}");
            File.WriteAllText(badHeader, "{\"name\":\"C failed\",\"symbol\":\"C\",\"data_url\":\"blocked-data.json\"}");
            File.WriteAllText(goodData, oldData);
            File.WriteAllText(blockedData, oldData);
            var library = new TestBmsLibrary(databaseScope, songPath, settings: httpSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath,
                () => new CustomFolderOutputSettingsSnapshot(), settings: httpSettings);
            httpPlaylist = playlist;
            BMSTable good = await SingleRequestHttpServer.AwaitOperationAsync(playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(goodHeader), cancellationToken: httpOperation.Token), httpRequests, httpOperation.Token);
            BMSTable blocked = await SingleRequestHttpServer.AwaitOperationAsync(playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(blockedHeader), cancellationToken: httpOperation.Token), httpRequests, httpOperation.Token);
            BMSTable bad = await SingleRequestHttpServer.AwaitOperationAsync(playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(badHeader), cancellationToken: httpOperation.Token), httpRequests, httpOperation.Token);
            server = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes(
                "{\"name\":\"B blocked\",\"symbol\":\"B\",\"data_url\":\"" + new Uri(blockedData).AbsoluteUri + "\"}"), holdBody: true));
            blocked.Page_url = server.Address;
            blocked.Header_url = server.Address;
            good.playlist_id = 7531;
            blocked.playlist_id = 7532;
            bad.playlist_id = 7533;
            DateTime oldUpdate = new(2001, 2, 3, 4, 5, 6);
            foreach (BMSTable table in new[] { good, blocked, bad })
            {
                table.last_update = oldUpdate;
                table.is_bmt_output = false;
                Assert.IsTrue(table.EnableExternalSync());
            }
            bad.entries[0].memo = "keep-failed-local";
            playlist.BMSTables = new ObservableCollection<BMSTable>([good, blocked, bad]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            var completedTargets = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            var summaryAdopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summaryFailure = new InvalidOperationException("incremental summary request failed");
            int summaryRequestCount = 0;
            int detailRefreshCount = 0;
            int completionCount = 0;
            string refreshReason = automatic ? "external_playlist_sync" : "manual_playlist_resync";
            TestUiDispatcherHost.Invoke(() =>
            {
                workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
                    playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                    dispatchPresentation: TestUiDispatcherHost.Invoke);
                workspace.RefreshPlaylistTreeTables(playlist, library);
                workspace.RequestDetailSelection(good, PlaylistFolderNode.CreateFolder("1"));
                workspace.SetPlaylistSummaryMode(true);
                workspace.RebuildPlaylistSummaryView(runAsync: false);
            });
            PlaylistWorkspaceViewModel currentWorkspace = workspace
                ?? throw new AssertFailedException("Workspace was not created.");
            currentWorkspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            currentWorkspace.PlaylistDetailReloadRefreshRequested += (_, _) => Interlocked.Increment(ref detailRefreshCount);
            currentWorkspace.PlaylistExternalSyncCompleted += (_, _) => Interlocked.Increment(ref completionCount);
            currentWorkspace.PlaylistSyncProgressChanged += (_, change) =>
            {
                progress.Enqueue(change.Snapshot);
                if (change.Snapshot.IsActive && change.Snapshot.CompletedTableCount == 2)
                {
                    completedTargets.TrySetResult();
                }
            };
            currentWorkspace.PlaylistPresentationRefreshRequested += (_, request) =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.SummaryData && request.Reason == refreshReason
                    && Interlocked.Increment(ref summaryRequestCount) == 1 && failSummaryRequest)
                {
                    throw summaryFailure;
                }
            };
            TestUiDispatcherHost.Invoke(() => currentWorkspace.PlaylistSummaryView.CollectionChanged += (_, _) =>
            {
                PlaylistSummaryRow? goodRow = currentWorkspace.PlaylistSummaryView.SingleOrDefault(row => row.PlaylistId == good.playlist_id);
                PlaylistSummaryRow? badRow = currentWorkspace.PlaylistSummaryView.SingleOrDefault(row => row.PlaylistId == bad.playlist_id);
                if (goodRow?.Status == Resources.Playlist_sync_status_updated && badRow?.HasFailureStatus == true)
                {
                    summaryAdopted.TrySetResult();
                }
            });
            File.WriteAllText(goodData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"After\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            File.WriteAllText(badHeader, "{ invalid json }");
            operation = Task.Run(() => automatic
                ? currentWorkspace.RunExternalPlaylistSyncAsync("incremental-summary", true, true, 1)
                : currentWorkspace.ResyncPlaylistsAsync([good, blocked, bad]));
            await server.WaitForPhaseAsync(server.HeadersSent.Task, "incremental-summary.blocked-http", operation);
            await SingleRequestHttpServer.AwaitOperationAsync(TestUiDispatcherHost.AwaitNotificationAsync(completedTargets.Task, operation, "incremental-summary.completed-targets"), httpRequests, httpOperation.Token);
            await SingleRequestHttpServer.AwaitOperationAsync(currentWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync(), httpRequests, httpOperation.Token);
            Assert.IsTrue(summaryAdopted.Task.IsCompletedSuccessfully, "全件完了前に、成功と取得失敗を実summaryへ採用します。");
            await (summaryAdopted.Task).WaitAsync(httpOperation.Token);
            BMSTable confirmed = playlist.BMSTables.Single(table => table.playlist_id == good.playlist_id);
            Assert.AreNotSame(good, confirmed);
            Assert.AreSame(confirmed, currentWorkspace.CapturePlaylistDetailSelection().Table);
            Assert.AreNotEqual(oldUpdate, confirmed.last_update);
            TestUiDispatcherHost.Invoke(() =>
            {
                PlaylistSummaryRow goodRow = currentWorkspace.PlaylistSummaryView.Single(row => row.PlaylistId == good.playlist_id);
                Assert.AreSame(confirmed, goodRow.TableRef);
                Assert.AreEqual(confirmed.last_update, goodRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_updated, goodRow.Status);
                Assert.IsFalse(goodRow.HasFailureStatus);
                PlaylistSummaryRow badRow = currentWorkspace.PlaylistSummaryView.Single(row => row.PlaylistId == bad.playlist_id);
                Assert.AreSame(bad, badRow.TableRef);
                Assert.AreEqual(oldUpdate, badRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_header, badRow.Status);
                Assert.IsTrue(badRow.HasFailureStatus);
            });
            PlaylistSyncRuntimeStatus failedStatus = currentWorkspace.CapturePlaylistSyncStatusSnapshot()["id:7533"];
            Assert.AreEqual(string.Join(Environment.NewLine, failedStatus.StatusText,
                failedStatus.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), new Uri(badHeader).ToString()), failedStatus.Detail);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(confirmed.last_update, db.Table<BMSTable>().Single(table => table.playlist_id == good.playlist_id).last_update);
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == good.playlist_id));
                Assert.AreEqual(oldUpdate, db.Table<BMSTable>().Single(table => table.playlist_id == bad.playlist_id).last_update);
                Assert.AreEqual("keep-failed-local", db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == bad.playlist_id).memo);
            }
            Assert.AreSame(bad, playlist.BMSTables.Single(table => table.playlist_id == bad.playlist_id));
            Assert.AreSame(blocked, playlist.BMSTables.Single(table => table.playlist_id == blocked.playlist_id));
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(playlist.IsPlaylistUpdating);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            Assert.AreEqual(0, Volatile.Read(ref completionCount));
            Assert.AreEqual(0, Volatile.Read(ref detailRefreshCount));
            Assert.AreEqual(2, Volatile.Read(ref summaryRequestCount));
            PlaylistSyncProgressSnapshot[] partialProgress = progress.ToArray();
            Assert.IsTrue(partialProgress.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 0));
            Assert.IsTrue(partialProgress.Where(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 0)
                .All(snapshot => string.IsNullOrEmpty(snapshot.CurrentTableName) && snapshot.CurrentUri == null));
            PlaylistSyncProgressSnapshot lastFinished = partialProgress.Last(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 2);
            Assert.AreEqual(3, lastFinished.TotalTableCount);
            Assert.IsTrue(lastFinished.CurrentTableName is "A success" or "C failed");
            Assert.AreNotEqual(server.Address, lastFinished.CurrentUri, "通信保持中のBを終了対象として表示しません。");
            server.ReleaseBody.TrySetResult();
            if (failSummaryRequest)
            {
                Exception actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token));
                Assert.AreSame(summaryFailure, actual, "モデルの任意callback境界で必須採用失敗を飲み込みません。");
            }
            else { await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token); }
            PlaylistSyncProgressSnapshot allFinished = progress.Last(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 3);
            Assert.AreEqual(3, allFinished.TotalTableCount);
            Assert.AreEqual("B blocked", allFinished.CurrentTableName);
            Assert.AreEqual(server.Address, allFinished.CurrentUri);
            Assert.AreEqual(automatic ? 1 : 0, Volatile.Read(ref completionCount));
            Assert.AreEqual(1, Volatile.Read(ref detailRefreshCount));
            Assert.AreEqual(3, Volatile.Read(ref summaryRequestCount), "終端で対象のSTATUS・参照・summary要求を二重採用しません。");
            Assert.AreEqual(failedStatus.CheckedAt, currentWorkspace.CapturePlaylistSyncStatusSnapshot()["id:7533"].CheckedAt);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable admission));
            admission.Dispose();
            await SingleRequestHttpServer.AwaitOperationAsync(currentWorkspace.ResyncPlaylistsAsync([confirmed]), httpRequests, httpOperation.Token);
            Assert.AreEqual(PlaylistSyncStatusKind.Ok, currentWorkspace.CapturePlaylistSyncStatusSnapshot()["id:7531"].Kind);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpPlaylist?.RequestShutdown("http-fixture-cleanup");
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, primaryFailure, httpRequests.Concat(new Task?[] { operation }.OfType<Task>().Where(task => !task.IsCompleted)), httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {

                if (operation != null)
                {
                    try { await (operation).WaitAsync(httpCleanup.Token); }
                    catch when (primaryFailure != null || failSummaryRequest) { }
                }

                if (workspace != null)
                {
                    await (workspace.WaitForDetailBuildIdleAsync()).WaitAsync(httpCleanup.Token);
                    await (workspace.WaitForPlaylistSummaryDataBuildIdleAsync()).WaitAsync(httpCleanup.Token);
                    await (workspace.WaitForPlaylistReloadCleanupIdleAsync()).WaitAsync(httpCleanup.Token);
                }
                httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(directory, recursive: true); migrationWorkspace.Dispose();

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (primaryFailure != null) { throw new AggregateException(primaryFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    /// <summary>実HTTP本文の待機中は削除を拒否し、取得完了だけでは拒否した削除を再実行しません。後続の明示削除でDBとモデルを除去し、通信中の予約漏れと自動再実行を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_CommunicationKeepsPAndRejectsRemovalWithoutReplay()
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        var httpRequests = new List<Task>();
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); return ownedServer; }

        Exception? httpPrimaryFailure = null;
        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        SingleRequestHttpServer? server = null;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? reloadTask = null;
        try
        {
            Settings httpSettings = TestSettingsFactory.OpenSettings(Path.Combine(tempDirectory, "user.config"));
            httpSettings.EnablePlaylistUrlCompletion = false;
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"RemovedTarget\",\r\n\"symbol\":\"R\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: httpSettings);
            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));
            table.playlist_id = 9021;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            server = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes(
                "{\"name\":\"RemovedTarget\",\"symbol\":\"R\",\"data_url\":\"" + new Uri(scoreJsonPath).AbsoluteUri + "\",\"level_order\":[1]}"), holdBody: true));
            table.Page_url = server.Address;
            int syncResultCount = 0;
            reloadTask = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                _ => syncResultCount++,
                reason: "test_removed_target",
                requireCurrentTargetForApply: true, cancellationToken: httpOperation.Token);
            await server.WaitForPhaseAsync(server.HeadersSent.Task, "reload HTTP headers", reloadTask);
            Assert.IsFalse(reloadTask.IsCompleted);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => playlist.RemoveBMSTable(table));
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1L, before.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = 9021;"));
                Assert.AreEqual(1L, before.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 9021;"));
            }
            server.ReleaseBody.TrySetResult();

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await SingleRequestHttpServer.AwaitOperationAsync(reloadTask, httpRequests, httpOperation.Token);

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.AreEqual(2, results[0].ResultTable.entries.Count);
            Assert.AreEqual(1, playlist.BMSTables.Count, "拒否した削除を後で自動実行しません。");
            playlist.RemoveBMSTable(results[0].ResultTable);
            Assert.AreEqual(1, syncResultCount);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = 9021;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 9021;"));
        }
        catch (Exception httpPrimary) { httpPrimaryFailure = httpPrimary; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, httpPrimaryFailure, httpRequests.Concat(new Task?[] { reloadTask }.OfType<Task>().Where(task => !task.IsCompleted)), httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {

                if (reloadTask != null && !reloadTask.IsCompleted)
                {
                    try
                    {
                        await (reloadTask).WaitAsync(httpCleanup.Token);
                    }
                    catch
                    {
                    }
                }

                if (Directory.Exists(tempDirectory))
                {
                    httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(tempDirectory, recursive: true); migrationWorkspace.Dispose();
                }

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (httpPrimaryFailure != null) { throw new AggregateException(httpPrimaryFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    private TestWorkspace? ownedCaseWorkspace;

    /// <summary>返却されたfixtureの終端まで、ケースが専有媒体の親領域を保持します。</summary>
    private TestWorkspace CaseWorkspace => ownedCaseWorkspace ?? throw new InvalidOperationException("ケースの媒体は初期化後に使用します。");

    /// <summary>ケース専有領域を開始し、既存の相対pathの存在条件を維持します。</summary>
    [TestInitialize]
    public void InitializeCaseWorkspace() => ownedCaseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース本文のfinallyとfixtureの終端後に外側の媒体を回収します。</summary>
    [TestCleanup]
    public void CleanupCaseWorkspace() => ownedCaseWorkspace?.Dispose();


    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>
    /// Aの実JSON・DB・正本置換を確定した後、Bの実HTTP取得を操作tokenで取り消します。
    /// schedulerの完了だけを保持して全開始Taskの終端までPと進捗が続くこと、解放後にAのreceiptと全体取消原因が届きBのDBを変更しないことを確認します。
    /// workspaceの選択もAの確定表へ移します。廃止したsort再通知の故障注入は除き、通常対象失敗・出力失敗は既存ケースへ分担します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_PartialCancellationRetainsConfirmedReferenceUntilAllTasksEnd()
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        var httpRequests = new List<Task>();
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); return ownedServer; }

        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        string directory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Task<(string TempDirectory, TestBmsPlaylist Playlist, BMSTable Table, ControlledUiScheduler Scheduler)>? preparation = null;
        TestBmsPlaylist? playlist = null;
        ControlledUiScheduler? scheduler = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(httpOperation.Token);
        SingleRequestHttpServer? server = null;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? operation = null;
        Exception? primaryFailure = null;
        PlaylistWorkspaceViewModel? workspace = null;
        var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
        var cancelledTargetEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Directory.CreateDirectory(directory);
            Settings httpSettings = TestSettingsFactory.OpenSettings(Path.Combine(directory, "user.config"));
            // 元準備Taskとdirectoryは待機wrapperの取消より前からcallerが所有します。
            preparation = CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending,
                temporaryDirectory: directory, cancellationToken: httpOperation.Token, settings: httpSettings);
            (string TempDirectory, TestBmsPlaylist Playlist, BMSTable Table, ControlledUiScheduler Scheduler) prepared = await SingleRequestHttpServer.AwaitOperationAsync(preparation, httpRequests, httpOperation.Token);
            playlist = prepared.Playlist;
            scheduler = prepared.Scheduler;
            BMSTable original = prepared.Table;
            string songPath = Path.Combine(directory, "song.db");
            server = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, CreateUtf8BomBytes(
                "{\"name\":\"Cancelled\",\"symbol\":\"C\",\"data_url\":\"" + new Uri(Path.Combine(directory, "score.json")).AbsoluteUri + "\"}"), holdBody: true));
            var cancelledTable = new BMSTable
            {
                playlist_id = 9052,
                name = "Cancelled original",
                Page_url = server.Address,
                entries = [new BMSTableEntry { md5 = "cccccccccccccccccccccccccccccccc", title = "Unchanged", memo = "keep-cancelled" }]
            };
            original.entries[0].memo = "keep-confirmed";
            playlist.BMSTables = new ObservableCollection<BMSTable>([original, cancelledTable]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            {
                var library = new TestBmsLibrary(databaseScope, songPath, settings: httpSettings);
                workspace = CreatePlaylistWorkspace(playlist, library);
                workspace.RequestDetailSelection(original, PlaylistFolderNode.CreateFolder("1"));

            }
            Uri cancelledUri = server.Address;
            operation = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original, cancelledTable],
                progressCallback: snapshot =>
                {
                    progress.Enqueue(snapshot);
                    if (snapshot.CompletedTableCount > 0 && snapshot.CurrentUri == cancelledUri)
                    {
                        cancelledTargetEnded.TrySetResult();
                    }
                }, cancellationToken: cancellation.Token, reason: "partial-cancellation", publishReferenceReceipts: true);
            await server.WaitForPhaseAsync(server.HeadersSent.Task, "partial reload HTTP headers", operation);
            Assert.AreSame(scheduler.Scheduled, await (Task.WhenAny(scheduler.Scheduled, operation)).WaitAsync(httpOperation.Token));
            await SingleRequestHttpServer.AwaitOperationAsync(scheduler.Scheduled, httpRequests, httpOperation.Token);
            await SingleRequestHttpServer.AwaitOperationAsync(scheduler.ApplyPendingWithoutCompletionAsync(), httpRequests, httpOperation.Token);
            BMSTable confirmed = playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id);
            Assert.AreNotSame(original, confirmed);
            Assert.AreEqual(2, confirmed.entries.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == original.playlist_id));
            }
            cancellation.Cancel();
            Assert.AreSame(cancelledTargetEnded.Task, await (Task.WhenAny(cancelledTargetEnded.Task, operation)).WaitAsync(httpOperation.Token));
            await (cancelledTargetEnded.Task).WaitAsync(httpOperation.Token);
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(playlist.IsPlaylistUpdating);
            Assert.IsTrue(progress.Last().IsActive);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            scheduler.CompletePending();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token);
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult successful = result.Single(target => ReferenceEquals(target.SourceTable, original));
            Assert.AreSame(confirmed, successful.ResultTable);
            Assert.IsTrue(successful.Updated);
            Assert.IsTrue(successful.StatePersisted);
            Assert.IsNull(successful.Exception);
            Assert.AreSame(confirmed, workspace?.CapturePlaylistDetailSelection().Table);
            Assert.IsNotNull(successful.UpdateReceipt);
            Assert.AreSame(confirmed, successful.UpdateReceipt.NewTable);
            Assert.AreEqual("keep-confirmed", confirmed.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").memo);
            Assert.IsNotNull(result.OperationException);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            // 実HTTP本文はlinked tokenを使うため、元取消例外のtoken同一性ではなく操作の取消しと原因を確認する。
            Assert.IsTrue(new AggregateException(result.OperationException).Flatten().InnerExceptions
                .Any(failure => failure is OperationCanceledException));
            Assert.AreSame(cancelledTable, playlist.BMSTables.Single(table => table.playlist_id == cancelledTable.playlist_id));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("Cancelled original", db.Table<BMSTable>().Single(table => table.playlist_id == cancelledTable.playlist_id).name);
                LR2SongDBExtended.playlist_entry unchanged = db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == cancelledTable.playlist_id);
                Assert.AreEqual("Unchanged", unchanged.title);
                Assert.AreEqual("keep-cancelled", unchanged.memo);
                Assert.AreEqual("cccccccccccccccccccccccccccccccc", unchanged.md5);
            }
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsFalse(progress.Last().IsActive);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult next = await SingleRequestHttpServer.AwaitOperationAsync(playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([confirmed], cancellationToken: httpOperation.Token), httpRequests, httpOperation.Token);
            next.ThrowIfFailed();
            Assert.AreSame(confirmed, next.Single().ResultTable);
            Assert.IsFalse(next.Single().Updated);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            scheduler?.CompletePending();
            cancellation.Cancel();
            playlist?.RequestShutdown("http-fixture-cleanup");
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, primaryFailure, httpRequests.Concat(new Task?[] { operation }.OfType<Task>().Where(task => !task.IsCompleted)), httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            try
            {
                // 操作待機で受け取れなかった遅い成功結果も、実Taskの終端後に同じcallerが回収します。
                if (preparation is { IsCompletedSuccessfully: true })
                {
                    (string TempDirectory, TestBmsPlaylist Playlist, BMSTable Table, ControlledUiScheduler Scheduler) prepared = await preparation;
                    playlist ??= prepared.Playlist;
                    scheduler ??= prepared.Scheduler;
                    scheduler.CompletePending();
                    playlist.RequestShutdown("http-fixture-cleanup");
                }
            }
            catch (Exception recoveryFailure)
            {
                httpJoinFailure = httpJoinFailure != null ? new AggregateException(httpJoinFailure, recoveryFailure)
                    : primaryFailure != null ? new AggregateException(primaryFailure, recoveryFailure) : recoveryFailure;
            }
            if (httpJoinFailure != null && httpCleanup.IsCancellationRequested) { throw httpJoinFailure; }
            try
            {
                cancellation.Cancel();

                if (operation != null)
                {
                    try { await (operation).WaitAsync(httpCleanup.Token); }
                    catch when (primaryFailure != null) { }
                }

                if (workspace != null)
                {
                    await (workspace.WaitForDetailBuildIdleAsync()).WaitAsync(httpCleanup.Token);
                    await (workspace.WaitForPlaylistSummaryDataBuildIdleAsync()).WaitAsync(httpCleanup.Token);
                    await (workspace.WaitForPlaylistReloadCleanupIdleAsync()).WaitAsync(httpCleanup.Token);
                }
                httpCleanup.Token.ThrowIfCancellationRequested();
                if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
                migrationWorkspace.Dispose();

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (primaryFailure != null) { throw new AggregateException(primaryFailure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    /// <summary>実コレクションの代入後に購読者が失敗しても、新参照と元例外を返しDB確定と次操作を保ちます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_CollectionNotificationFailureRetainsActuallyAdoptedReference()
    {
        (string directory, TestBmsPlaylist playlist, BMSTable original, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending);
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? operation = null;
        var expected = new InvalidOperationException("replacement subscription failed");
        System.Collections.Specialized.NotifyCollectionChangedEventHandler fail = (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace) { throw expected; }
        };
        playlist.BMSTables.CollectionChanged += fail;
        try
        {
            operation = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original]);
            await TestUiDispatcherHost.AwaitNotificationAsync(scheduler.Scheduled, operation, "reload.collection-notification");
            scheduler.ReleasePending();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await operation;
            Assert.AreSame(expected, result.Single().Exception);
            Assert.IsNotNull(result.Single().UpdateReceipt);
            Assert.AreNotSame(original, result.Single().ResultTable);
            Assert.AreSame(result.Single().ResultTable, playlist.BMSTables.Single());
            BMSTable persisted = new PlaylistPersistenceRepository(databaseScope, Path.Combine(directory, "song.db")).LoadPlaylistHeaders().Single();
            Assert.AreEqual(result.Single().ResultTable.data_sha256, persisted.data_sha256);
            playlist.BMSTables.CollectionChanged -= fail;
            playlist.RemoveBMSTable(result.Single().ResultTable);
            Assert.AreEqual(0, new PlaylistPersistenceRepository(databaseScope, Path.Combine(directory, "song.db")).LoadPlaylistHeaders().Count);
        }
        finally
        {
            scheduler.ReleasePending();
            if (operation != null) { await operation; }
            playlist.BMSTables.CollectionChanged -= fail;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>通常表と推薦表のUI置換を保留し、完了前は旧参照と無通知を保ち、競合する復元・削除を拒否します。UI確定前の成功通知と予約の早期解放を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_AwaitsUiReplacementCompletionBeforeApplying(bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending, recommendation);
        testSettings.EnablePlaylistUrlCompletion = false;
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? reloadTask = null;
        Task? competingRestore = null;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
        try
        {
            reloadTask =
                playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_ui_replacement_completion");

            Task firstTerminal = await Task.WhenAny(scheduler.Scheduled, reloadTask);
            Assert.AreSame(scheduler.Scheduled, firstTerminal);
            await scheduler.Scheduled;
            Assert.IsFalse(reloadTask.IsCompleted);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.AreEqual(0, scheduler.ExecutionCount);
            Assert.IsTrue(notifications.TakeReceipt().IsEmpty);
            // U2-R2: 個別 reload の実 pending publication がある間、復元は DB 更新前に拒否する。
            competingRestore = playlist.RestorePlaylistDumpAsync(
                PlaylistWorkspaceFixtureData.CreatePlaylistRestoreDump(77, "Rejected restore", "X"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                competingRestore);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(Path.Combine(tempDirectory, "song.db")))
            {
                Assert.AreEqual(table.playlist_id, db.Table<BMSTable>().Single().playlist_id);
            }

            string[] persistedEntries;
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(tempDirectory, "song.db")).OpenSongDbReadOnly())
            {
                persistedEntries = before.Table<LR2SongDBExtended.playlist_entry>().Select(entry => entry.md5 + ":" + entry.title).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Task.Run(() => playlist.RemoveBMSTable(table)));
            using (LR2SongDBExtended afterRejection = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(tempDirectory, "song.db")).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, afterRejection.Table<BMSTable>().Count());
                CollectionAssert.AreEqual(persistedEntries,
                    afterRejection.Table<LR2SongDBExtended.playlist_entry>().Select(entry => entry.md5 + ":" + entry.title).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            }
            scheduler.ReleasePending();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.AreEqual(1, scheduler.ExecutionCount);
            Assert.AreSame(results[0].ResultTable, playlist.BMSTables.Single());
            Assert.AreNotSame(table, results[0].ResultTable);
            Assert.AreEqual(recommendation ? 1 : 0, notifications.TakeReceipt().Notifications.Count);
            if (recommendation)
            {
                Assert.IsFalse(results[0].Updated);
                Assert.AreEqual(table.last_update, results[0].ResultTable.last_update);
            }
            playlist.RemoveBMSTable(results[0].ResultTable);
            using (LR2SongDBExtended after = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(tempDirectory, "song.db")).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, after.Table<BMSTable>().Count());
                Assert.AreEqual(0, after.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
        }
        finally
        {
            if (reloadTask != null && !reloadTask.IsCompleted)
            {
                try
                {
                    scheduler.ReleasePending();
                    await reloadTask;
                }
                catch
                {
                }
            }
            if (competingRestore != null)
            {
                try { await competingRestore; }
                catch (InvalidOperationException) { }
            }
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    // U2-R4/R2: 既存 operation scheduler で UI rejection / cancel / fault を区別し、DB 保持と予約解放を確認する。
    /// <summary>復元後のUI配送が拒否・取消・失敗しても確定DBを保持し、旧表示のまま失敗を返して次の明示復元を受理します。表示失敗でDBを巻き戻す誤りと予約漏れを検出します。</summary>
    [TestMethod]
    [DataRow("Rejected")]
    [DataRow("Aborted")]
    [DataRow("Faulted")]
    [TestCategory("Playlist")]
    public async Task PlaylistRestore_UiTerminalFailureKeepsDatabaseAndReleasesReservation(string outcomeName)
    {
        UiScheduleOutcome outcome = Enum.Parse<UiScheduleOutcome>(outcomeName);
        (string directory, TestBmsPlaylist playlist, BMSTable oldTable, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(outcome);
        testSettings.EnablePlaylistUrlCompletion = false;
        testSettings.OperationModeLR2DB = false;
        playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
        try
        {
            Exception? failure = null;
            try
            {
                await playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceFixtureData.CreatePlaylistRestoreDump(77, "Committed despite UI failure", "R"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.IsNotNull(failure);
            Assert.AreSame(oldTable, playlist.BMSTables.Single());
            Assert.AreEqual(0, scheduler.ExecutionCount);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(directory, "song.db")).OpenSongDbReadOnly())
            {
                Assert.AreEqual(77, verify.Table<BMSTable>().Single().playlist_id);
            }
            // 独立した次の復元を受理する。失敗操作自身の repost / retry ではない。
            await playlist.RestorePlaylistDumpAsync(
                PlaylistWorkspaceFixtureData.CreatePlaylistRestoreDump(88, "Next independent restore", "N"));
            Assert.AreEqual(88, playlist.BMSTables.Single().playlist_id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>通常表と推薦表のUI配送拒否を再取得適用失敗として返し、旧モデル・保存値を保持して成功通知を出しません。配送できない置換を成功扱いする誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsRejectedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Rejected,
            typeof(InvalidOperationException),
            "rejected", recommendation);

    /// <summary>通常表と推薦表のUI配送取消を元取消の分類付きで返し、旧モデル・保存値と無通知を保持します。取消を適用済み扱いする誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsAbortedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Aborted,
            typeof(OperationCanceledException),
            "aborted", recommendation);

    /// <summary>通常表と推薦表のUI配送例外を適用失敗の内部例外に保持し、旧モデル・保存値を変えません。配送失敗を隠して成功通知する誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsFaultedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Faulted,
            typeof(InvalidOperationException),
            "faulted", recommendation);

    private async Task AssertUiReplacementFailureAsync(
        UiScheduleOutcome outcome,
        Type expectedInnerExceptionType,
        string reason, bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(outcome, recommendation);
        testSettings.EnablePlaylistUrlCompletion = false;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
        try
        {
            Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult> reloadTask =
                playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_ui_replacement_" + reason);

            await TestUiDispatcherHost.AwaitNotificationAsync(scheduler.Scheduled, reloadTask,
                "reload.ui-replacement." + reason + (recommendation ? ".recommendation" : ".external"));
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Succeeded);
            Assert.AreSame(table, results[0].ResultTable);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Exception failure = results[0].Exception
                ?? throw new AssertFailedException("The UI replacement failure was not reported.");
            Assert.IsInstanceOfType(failure, typeof(PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException));
            Assert.IsNotNull(failure.InnerException);
            Assert.IsInstanceOfType(failure.InnerException, expectedInnerExceptionType);
            StringAssert.Contains(failure.InnerException!.Message, reason);
            Assert.AreEqual(0, scheduler.ExecutionCount);
            Assert.IsTrue(notifications.TakeReceipt().IsEmpty);
            BMSTable persisted = new PlaylistPersistenceRepository(databaseScope, Path.Combine(tempDirectory, "song.db"))
                .LoadPlaylistHeaders().Single();
            Assert.AreEqual(table.name, persisted.name);
            Assert.AreEqual(table.org_name, persisted.org_name);
            Assert.AreEqual(table.header_sha256, persisted.header_sha256);
            Assert.AreEqual(table.data_sha256, persisted.data_sha256);
            Assert.AreEqual(table.last_update, persisted.last_update);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>準備の実Taskが結果を返すまで所有し、HTTP caller指定のdirectoryはcallerの同一cleanup期限へ残します。</summary>
    /// <param name="outcome">準備後のUI置換で使う固定結果。</param>
    /// <param name="recommendation">推薦表を準備する場合はtrue。</param>
    /// <param name="temporaryDirectory">HTTP callerが開始前から所有するdirectory。省略時は従来どおり準備側で作成失敗も回収します。</param>
    /// <param name="cancellationToken">準備の実取得にも渡す操作token。非HTTPの既存callerは省略します。</param>
    /// <param name="settings">HTTP callerがdirectoryと同じ寿命で所有する設定。省略時は非HTTPの既存case設定を維持します。</param>
    private async Task<(string TempDirectory, TestBmsPlaylist Playlist, BMSTable Table, ControlledUiScheduler Scheduler)> CreateUiReplacementReloadFixtureAsync(
        UiScheduleOutcome outcome, bool recommendation = false, string? temporaryDirectory = null, CancellationToken cancellationToken = default, Settings? settings = null)
    {
        string tempDirectory = temporaryDirectory ?? Path.Combine(CaseWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerJsonPath,
                CreateUtf8BomBytes(
                    "{\r\n\"name\":\"AsyncReplacementTable\",\r\n\"symbol\":\"A\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes(
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var scheduler = new ControlledUiScheduler(TestUiDispatcherHost.Dispatcher);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = true },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty),
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")), uiScheduler: scheduler, settings: settings ?? testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(
                recommendation ? new Uri("bmseeker:table.recommended") : new Uri(headerJsonPath), cancellationToken: cancellationToken);
            if (recommendation)
            {
                table.name = "Manual recommendation ★999";
                table.org_name = "旧方針 ★3.87";
                table.header_sha256 = null;
                table.data_sha256 = null;
                table.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            }
            table.playlist_id = 9051;
            table.DisableExternalSync();
            using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(songDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries ?? [])
                    {
                        setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                });
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes(
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            scheduler.Arm(outcome);
            return (tempDirectory, playlist, table, scheduler);
        }
        catch
        {
            if (temporaryDirectory == null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            throw;
        }
    }

    private enum UiScheduleOutcome
    {
        AcceptedPending,
        Rejected,
        Aborted,
        Faulted
    }

    private sealed class ControlledUiScheduler : IUiScheduler
    {
        private readonly TestUiScheduler inner;

        private readonly object synchronization = new();

        private readonly TaskCompletionSource<IUiScheduledOperation> scheduled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private UiScheduleOutcome? armedOutcome;

        private PendingUiScheduledOperation? pendingOperation;

        internal ControlledUiScheduler(Dispatcher dispatcher)
        {
            inner = new TestUiScheduler(() => dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));
        }

        internal Task<IUiScheduledOperation> Scheduled => scheduled.Task;

        internal int ExecutionCount
        {
            get
            {
                lock (synchronization)
                {
                    return pendingOperation?.ExecutionCount ?? 0;
                }
            }
        }

        internal void Arm(UiScheduleOutcome outcome)
        {
            lock (synchronization)
            {
                if (armedOutcome.HasValue)
                {
                    throw new InvalidOperationException("The controlled UI scheduler is already armed.");
                }
                armedOutcome = outcome;
            }
        }

        internal void ReleasePending()
        {
            PendingUiScheduledOperation operation;
            lock (synchronization)
            {
                operation = pendingOperation
                    ?? throw new InvalidOperationException("No pending UI operation is available.");
            }

            IUiScheduledOperation dispatch = inner.Schedule(operation.Run);
            if (dispatch == null || !dispatch.IsAccepted)
            {
                operation.Fail(new InvalidOperationException(
                    "The controlled UI scheduler could not release the pending operation."));
            }
        }

        /// <summary>実置換actionをUI上で完了し、scheduler完了Taskだけは解放せず保持します。</summary>
        internal Task ApplyPendingWithoutCompletionAsync()
        {
            PendingUiScheduledOperation operation;
            lock (synchronization)
            {
                operation = pendingOperation
                    ?? throw new InvalidOperationException("No pending UI operation is available.");
            }
            return inner.InvokeAsync(operation.RunWithoutCompletion);
        }

        /// <summary>保持したscheduler完了Taskを解放します。未到達のcleanupでは何もしません。</summary>
        internal void CompletePending()
        {
            lock (synchronization) { pendingOperation?.Complete(); }
        }

        public bool IsAvailable => inner.IsAvailable;

        public bool CanExecuteInline => inner.CanExecuteInline;

        public bool CheckAccess() => inner.CheckAccess();

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            UiScheduleOutcome? outcome;
            lock (synchronization)
            {
                outcome = armedOutcome;
                armedOutcome = null;
            }
            if (!outcome.HasValue)
            {
                return inner.Schedule(action, priority);
            }

            IUiScheduledOperation operation = outcome.Value switch
            {
                UiScheduleOutcome.AcceptedPending => CreatePendingOperation(action),
                UiScheduleOutcome.Rejected => new CompletedUiScheduledOperation(
                    accepted: false,
                    aborted: true,
                    completion: Task.CompletedTask,
                    rejectionReason: "controlled scheduler rejection"),
                UiScheduleOutcome.Aborted => new CompletedUiScheduledOperation(
                    accepted: true,
                    aborted: true,
                    completion: Task.FromCanceled(new CancellationToken(canceled: true)),
                    rejectionReason: "controlled scheduler abort"),
                UiScheduleOutcome.Faulted => new CompletedUiScheduledOperation(
                    accepted: true,
                    aborted: false,
                    completion: Task.FromException(new InvalidOperationException("controlled scheduler faulted")),
                    rejectionReason: null),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
            scheduled.TrySetResult(operation);
            return operation;
        }

        private IUiScheduledOperation CreatePendingOperation(Action action)
        {
            var operation = new PendingUiScheduledOperation(action);
            lock (synchronization)
            {
                pendingOperation = operation;
            }
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.Invoke(action, priority);

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.Invoke(action, priority);

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.InvokeAsync(action, priority);

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.InvokeAsync(action, priority);
    }

    private sealed class PendingUiScheduledOperation : IUiScheduledOperation
    {
        private readonly Action action;

        private readonly TaskCompletionSource<object?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int executionCount;

        private int aborted;

        internal PendingUiScheduledOperation(Action action)
        {
            this.action = action ?? throw new ArgumentNullException(nameof(action));
        }

        internal int ExecutionCount => Volatile.Read(ref executionCount);

        public bool IsAccepted => true;

        public bool IsCompleted => completion.Task.IsCompleted;

        public bool IsAborted => Volatile.Read(ref aborted) != 0;

        public string? RejectionReason => null;

        public Task Completion => completion.Task;

        public void Abort()
        {
            if (Interlocked.Exchange(ref aborted, 1) == 0)
            {
                completion.TrySetCanceled();
            }
        }

        internal void Run() => Run(complete: true);

        /// <summary>実actionの実行だけを完了し、完了Taskの解放を呼出元へ残します。</summary>
        internal void RunWithoutCompletion() => Run(complete: false);

        /// <summary>実action後に保持された完了Taskを解放します。</summary>
        internal void Complete() => completion.TrySetResult(null);

        private void Run(bool complete)
        {
            if (IsAborted || Interlocked.Increment(ref executionCount) != 1)
            {
                return;
            }
            try
            {
                action();
                if (complete) { Complete(); }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        internal void Fail(Exception exception)
        {
            completion.TrySetException(exception ?? throw new ArgumentNullException(nameof(exception)));
        }
    }

    private sealed class CompletedUiScheduledOperation : IUiScheduledOperation
    {
        private readonly bool accepted;

        private readonly bool aborted;

        internal CompletedUiScheduledOperation(
            bool accepted,
            bool aborted,
            Task completion,
            string? rejectionReason)
        {
            this.accepted = accepted;
            this.aborted = aborted;
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
            RejectionReason = rejectionReason;
        }

        public bool IsAccepted => accepted;

        public bool IsCompleted => Completion.IsCompleted;

        public bool IsAborted => aborted;

        public string? RejectionReason { get; }

        public Task Completion { get; }

        public void Abort()
        {
        }
    }

}
