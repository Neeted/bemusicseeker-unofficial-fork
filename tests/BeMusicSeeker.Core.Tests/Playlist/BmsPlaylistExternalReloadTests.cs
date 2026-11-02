using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using Newtonsoft.Json.Linq;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>実外部再取得のDB確定、UI置換、取消と必要出力の終端を確認します。</summary>
/// <remarks>実JSON・SQLiteと必要なHTTP本文を使い、UI配送はWindowを作らず共有schedulerで保持します。メニューや実Windowの接続は上位が担い、失敗した正本・公開・出力を別々に観測します。</remarks>
[TestClass]
public sealed class BmsPlaylistExternalReloadTests
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

    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実外部表の数値明示順と所属を保存後、異文化の新しい接続・モデルで読み戻し、地域差で順序や所属を変えないことを確認します。</summary>
    [TestMethod]
    public async Task ExternalNumericFolders_PreserveOrderAcrossCultureAndFreshDatabaseRead()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string header = Path.Combine(directory, "header.json");
            File.WriteAllText(header, "{\"name\":\"Numeric folders\",\"compat_prefix\":\"\",\"level_order\":[2.5,-1.25],\"data_url\":\"data.json\"}");
            File.WriteAllText(Path.Combine(directory, "data.json"), "[{\"title\":\"lower\",\"level\":-1.25},{\"title\":\"upper\",\"org_level\":null,\"folder\":2.5}]");
            string dbPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
            var playlist = new TestBmsPlaylist(databaseScope, dbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, dbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            table.playlist_id = 9019;
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var repository = new PlaylistPersistenceRepository(databaseScope, dbPath);
            BMSTable restored = repository.LoadPlaylistHeaders().Single();
            restored.entries = repository.LoadStartupPlaylistEntries().Entries;
            CollectionAssert.AreEqual(new[] { "2.5", "-1.25" }, restored.folder_list.ToArray());
            Assert.AreEqual("-1.25", restored.entries.Single(e => e.title == "lower").folder);
            Assert.AreEqual(-1.25, restored.entries.Single(e => e.title == "lower").level);
            Assert.AreEqual("2.5", restored.entries.Single(e => e.title == "upper").folder);
            Assert.IsNull(restored.entries.Single(e => e.title == "upper").level);
        }
        finally { CultureInfo.CurrentCulture = previous; Directory.Delete(directory, recursive: true); }
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();


    /// <summary>手動再同期の実DB置換後、header配送と固有summary公開を順に保持します。出力と表示が失敗しても元原因を返し、Pは必要表示の試行まで解放しません。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ManualResync_WaitsForHeaderAndReplacementSummaryPublication(bool failPresentation, bool failOutput)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(enableBmtOutput: true, modelUiScheduler: new TestUiScheduler(() => null));
        fixture.Workspace.Dispose();
        var queued = System.Threading.Channels.Channel.CreateUnbounded<(Action Apply, TaskCompletionSource Completion)>();
        bool hold = false;
        Task Schedule(Action action)
        {
            if (!hold) { action(); return Task.CompletedTask; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(queued.Writer.TryWrite((action, completion)));
            return completion.Task;
        }
        using PlaylistWorkspaceViewModel workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
            playlistStoreProvider: () => fixture.Playlist, playlistLibraryProvider: () => fixture.Library,
            restoreUiApplyScheduler: Schedule, presentationRefreshDeferredProvider: _ => true);
        workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
        fixture.Playlist.BmtOutput.FailureReporter = workspace.ReportBmtOutputFailures;
        workspace.RequestPlaylistSummaryMode(true); workspace.RebuildPlaylistSummaryView(runAsync: false);
        await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        PlaylistSummaryRow oldRow = workspace.PlaylistSummaryView.Single();
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        int builds = 0;
        workspace.PlaylistSummaryBuildStarted += (_, completion) => { builds++; if (hold) { started.TrySetResult(completion); } };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.HeaderPath) ?? throw new AssertFailedException(), "data.json"),
            "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"New A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"New B\",\"level\":\"2\"}]");
        FileStream? blocker = failOutput ? new FileStream(fixture.BmtManifestPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var presentationFailure = new InvalidOperationException("manual reload summary publication failed");
        Task? operation = null;
        Task? summary = null;
        var held = new List<(Action Apply, TaskCompletionSource Completion)>();
        async Task<(Action Apply, TaskCompletionSource Completion)> Next()
        {
            Task<(Action Apply, TaskCompletionSource Completion)> next = queued.Reader.ReadAsync().AsTask();
            await TestUiDispatcherHost.AwaitNotificationAsync(next, operation ?? throw new AssertFailedException(), "manual reload owned presentation");
            (Action Apply, TaskCompletionSource Completion) work = await next; held.Add(work); return work;
        }
        void Apply((Action Apply, TaskCompletionSource Completion) work)
        {
            try { work.Apply(); work.Completion.TrySetResult(); }
            catch (Exception error) { work.Completion.TrySetException(error); }
        }
        try
        {
            hold = true;
            // treeの単表入口とsummaryの行入口はいずれも同じ受理済み手動再同期へ到達します。
            operation = failPresentation ? workspace.ResyncPlaylistsAsync([oldRow]) : workspace.ResyncPlaylistTableAsync(fixture.Table);
            (Action Apply, TaskCompletionSource Completion) first = await Next();
            Assert.AreNotSame(fixture.Table, fixture.Playlist.BMSTables.Single());
            Assert.AreSame(oldRow, workspace.PlaylistSummaryView.Single());
            Assert.IsFalse(operation.IsCompleted); Assert.IsFalse(fixture.Playlist.CanEnterPlaylistMutation);
            using (LR2SongDBExtended read = fixture.DatabaseScope.OpenSongReadOnly(fixture.SongDb))
            {
                var saved = read.Table<BMSTableEntry>().ToList();
                Assert.AreEqual(2, saved.Count(entry => !entry.is_removed));
                Assert.AreEqual(4, saved.Count(entry => entry.is_removed), "再取得は旧所属の削除履歴を保存します。");
            }
            Apply(first);
            while (!started.Task.IsCompleted) { Apply(await Next()); }
            summary = await started.Task;
            (Action Apply, TaskCompletionSource Completion) publication = await Next();
            Assert.IsFalse(summary.IsCompleted); Assert.IsFalse(operation.IsCompleted);
            Assert.IsFalse(fixture.Playlist.CanEnterPlaylistMutation);
            Assert.AreSame(oldRow, workspace.PlaylistSummaryView.Single());
            hold = false;
            if (failPresentation) { publication.Completion.TrySetException(presentationFailure); }
            else { Apply(publication); }
            Exception? failure = null;
            try { await operation; } catch (Exception error) { failure = error; }
            if (failPresentation)
            {
                Assert.IsNotNull(failure);
                IReadOnlyList<Exception> failures = new AggregateException(failure).Flatten().InnerExceptions;
                Assert.IsTrue(failures.Any(error => ReferenceEquals(error, presentationFailure)));
                Assert.AreEqual(failOutput, failures.Any(error => error is PlaylistMutationPostCommitException));
            }
            else { Assert.IsNull(failure); }
            Assert.IsTrue(summary.IsCompleted); Assert.IsTrue(fixture.Playlist.CanEnterPlaylistMutation);
            Assert.AreEqual(1, builds);
            blocker?.Dispose(); blocker = null;
            if (failPresentation) { await workspace.ResyncPlaylistTableAsync(fixture.Playlist.BMSTables.Single()); }
            PlaylistSummaryRow current = workspace.PlaylistSummaryView.Single();
            Assert.AreSame(fixture.Playlist.BMSTables.Single(), current.TableRef); Assert.AreNotSame(oldRow, current);
            Assert.AreEqual(2, current.TotalCharts);
            int beforeDrain = builds;
            workspace.DrainDeferredPlaylistSummaryRefresh(false, false);
            Assert.AreEqual(beforeDrain, builds, "既存shellの遅延callbackで二重構築しません。");
            using LibraryFileMutationLease nextAdmission = fixture.Playlist.AcquirePlaylistMutationLease("manual-reload-next");
        }
        finally
        {
            hold = false;
            foreach ((Action Apply, TaskCompletionSource Completion) work in held.Where(work => !work.Completion.Task.IsCompleted)) { Apply(work); }
            while (queued.Reader.TryRead(out (Action Apply, TaskCompletionSource Completion) work)) { Apply(work); }
            if (operation != null) { try { await operation; } catch (Exception) { } }
            if (summary != null) { try { await summary; } catch (Exception) { } }
            blocker?.Dispose();
            await workspace.WaitForDetailBuildIdleAsync(); await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        }
    }

    /// <summary>絶対Pageから空Headerだけを補完する実reloadを通し、項目へ再保存せずDB・現在表・必要BMTを確定します。</summary>
    [TestMethod]
    public async Task ReloadPlaylistTargetsAsync_CompletesOnlyHeaderWithoutRewritingEntries()
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "header-completion");
        Directory.CreateDirectory(directory);
        string path = CreateTempSongDbPath(databaseScope, directory);
        PlaylistPersistenceRepository.EnsureSchema(databaseScope, path);
        string pagePath = Path.Combine(directory, "index.html"), headerPath = Path.Combine(directory, "header.json"), dataPath = Path.Combine(directory, "data.json");
        Uri pageUri = new(pagePath), headerUri = new(headerPath), dataUri = new(dataPath);
        File.WriteAllText(pagePath, "<html><head><meta name='bmstable' content='" + headerUri.AbsoluteUri + "'></head><body></body></html>");
        File.WriteAllText(headerPath, "{\"name\":\"Header completion\",\"symbol\":\"H\",\"data_url\":\"" + dataUri.AbsoluteUri + "\"}");
        File.WriteAllText(dataPath, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Keep\",\"level\":\"1\"}]");
        string bmtDirectory = Path.Combine(directory, "bmt");
        var tasks = new ConcurrentQueue<Task>();
        var playlist = new TestBmsPlaylist(databaseScope, path, null, null, null,
            () => new PlaylistUrlCompletionOptionsSnapshot(),
            () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmtDirectory },
            () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, uiScheduler: new TestUiScheduler(() => null))
        { BMSTables = [] };
        playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { tasks.Enqueue(Task.Run(work)); return true; };
        try
        {
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(pageUri);
            original.playlist_id = 7921; original.Header_url = null; original.is_bmt_output = true;
            DateTime update = new(2020, 2, 3, 4, 5, 6), added = new(2019, 1, 2, 3, 4, 5);
            original.last_update = update; original.entries.Single().adddate = added; original.entries.Single().memo = "local memo";
            string headerHash = original.header_sha256, dataHash = original.data_sha256;
            Assert.AreEqual(pageUri, original.Page_url); Assert.AreEqual(dataUri, original.Data_url);
            Assert.IsFalse(string.IsNullOrEmpty(headerHash)); Assert.IsFalse(string.IsNullOrEmpty(dataHash));
            playlist.BMSTables.Add(original); playlist.CommitBMSTableWithEntriesToDB(original);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(path))
            {
                foreach (string kind in new[] { "INSERT", "UPDATE", "DELETE" })
                {
                    db.Execute("CREATE TRIGGER header_only_no_" + kind + " BEFORE " + kind + " ON playlist_entry BEGIN SELECT RAISE(ABORT, 'header completion must not save entries'); END;");
                }
            }
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            playlist.BmtOutput.ExportProgressReporter = progress.Enqueue;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original]);
            results.ThrowIfFailed();
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            BMSTable current = playlist.BMSTables.Single();
            Assert.AreSame(current, result.ResultTable); Assert.AreNotSame(original, current);
            Assert.IsFalse(result.Updated); Assert.IsTrue(result.StatePersisted); Assert.IsTrue(result.NeedsBmtExport);
            Assert.AreEqual(headerUri, current.Header_url); Assert.AreEqual(pageUri, current.Page_url); Assert.AreEqual(dataUri, current.Data_url);
            Assert.AreEqual(headerHash, current.header_sha256); Assert.AreEqual(dataHash, current.data_sha256); Assert.AreEqual(update, current.last_update);
            Assert.AreEqual("Header completion", current.name); Assert.AreEqual(added, current.entries.Single().adddate); Assert.AreEqual("local memo", current.entries.Single().memo);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), path).OpenSongDbReadOnly();
            BMSTable saved = verify.Table<BMSTable>().Single(); BMSTableEntry entry = verify.Table<BMSTableEntry>().Single();
            Assert.AreEqual(headerUri, saved.Header_url); Assert.AreEqual(pageUri, saved.Page_url); Assert.AreEqual(dataUri, saved.Data_url);
            Assert.AreEqual(headerHash, saved.header_sha256); Assert.AreEqual(dataHash, saved.data_sha256); Assert.AreEqual(update, saved.last_update);
            Assert.AreEqual("Keep", entry.title); Assert.AreEqual(added, entry.adddate); Assert.AreEqual("local memo", entry.memo);
            Assert.IsTrue(progress.Any(snapshot => snapshot.IsActive));
            Assert.IsTrue(File.Exists(Path.Combine(bmtDirectory, BmtTableExportService.ManifestFileName)));
            Assert.AreEqual(1, Directory.GetFiles(bmtDirectory, "*.bmt").Length);
        }
        finally
        {
            await playlist.WaitForPlaylistMutationIdleAsync();
            while (tasks.TryDequeue(out Task? task)) { await task; }
            playlist.RequestShutdown("header-completion-test");
        }
    }

    /// <summary>不適合日時のhash初期化では日時を保ち、同原文を再保存せず、既知header/data原文差だけ更新日時を進めます。旧adddate/memoは実mergeと新DBで保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_InvalidDatesKeepLocalValuesAndSkipUnchangedWrites()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tasks = new ConcurrentQueue<Task>();
        TestBmsPlaylist? playlist = null;
        Exception? primary = null;
        try
        {
            string path = CreateTempSongDbPath(databaseScope, directory); PlaylistPersistenceRepository.EnsureSchema(databaseScope, path);
            string headerPath = Path.Combine(directory, "header.json"), dataPath = Path.Combine(directory, "data.json");
            string header = "{\"name\":\"Hash dates\",\"last_update\":\"bad A\",\"data_url\":\"data.json\",\"unknown\":19}";
            const string data = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Keep\",\"adddate\":1700000000,\"unknown\":23}]";
            File.WriteAllText(headerPath, header); File.WriteAllText(dataPath, data);
            testSettings.EnablePlaylistUrlCompletion = false; testSettings.OperationModeLR2DB = false;
            playlist = new TestBmsPlaylist(databaseScope, path, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings, uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { tasks.Enqueue(Task.Run(work)); return true; };
            var seed = new BMSTable();
            seed.LoadHeaderJSON(header, PlaylistJsonDatePolicy.Compatible, __header_url: new Uri(headerPath));
            seed.LoadDataJSON(data, PlaylistJsonDatePolicy.Compatible);
            seed.playlist_id = 7920; seed.header_sha256 = null; seed.data_sha256 = null; seed.is_bmt_output = false;
            DateTime originalUpdate = new(2020, 2, 3, 4, 5, 6), originalAdd = new(2019, 1, 2, 3, 4, 5);
            seed.last_update = originalUpdate; seed.entries.Single().adddate = originalAdd; seed.entries.Single().memo = "Keep memo";
            playlist.BMSTables.Add(seed); playlist.CommitBMSTableWithEntriesToDB(seed);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult initialized = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([seed]);
            initialized.ThrowIfFailed();
            Assert.IsFalse(initialized.Single().Updated); Assert.IsTrue(initialized.Single().StatePersisted);
            BMSTable current = playlist.BMSTables.Single();
            Assert.AreEqual(originalUpdate, current.last_update);
            Assert.IsFalse(string.IsNullOrEmpty(current.header_sha256)); Assert.IsFalse(string.IsNullOrEmpty(current.data_sha256));
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(path))
            {
                db.Execute("CREATE TRIGGER date_no_header_write BEFORE INSERT ON playlist WHEN NEW.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged header write'); END;");
                db.Execute("CREATE TRIGGER date_no_entry_write BEFORE INSERT ON playlist_entry WHEN NEW.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged entry write'); END;");
                db.Execute("CREATE TRIGGER date_no_header_update BEFORE UPDATE ON playlist WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged header update'); END;");
                db.Execute("CREATE TRIGGER date_no_entry_update BEFORE UPDATE ON playlist_entry WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged entry update'); END;");
                db.Execute("CREATE TRIGGER date_no_header_delete BEFORE DELETE ON playlist WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged header delete'); END;");
                db.Execute("CREATE TRIGGER date_no_entry_delete BEFORE DELETE ON playlist_entry WHEN OLD.playlist_id = 7920 BEGIN SELECT RAISE(ABORT, 'unchanged entry delete'); END;");
            }
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([current]);
            unchanged.ThrowIfFailed(); Assert.IsFalse(unchanged.Single().StatePersisted); Assert.IsFalse(unchanged.Single().Updated);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(path))
            {
                foreach (string name in new[] { "date_no_header_write", "date_no_entry_write", "date_no_header_update", "date_no_entry_update", "date_no_header_delete", "date_no_entry_delete" }) { db.Execute("DROP TRIGGER " + name); }
            }
            header = header.Replace("bad A", "bad B", StringComparison.Ordinal); File.WriteAllText(headerPath, header);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult changedHeader = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([playlist.BMSTables.Single()]);
            changedHeader.ThrowIfFailed(); Assert.IsTrue(changedHeader.Single().Updated);
            current = playlist.BMSTables.Single(); Assert.IsTrue(current.last_update > originalUpdate);
            // dataの空白も原文hashの差として残します。日時キーや未知キーを削りません。
            File.WriteAllText(dataPath, " \n" + data);
            DateTime beforeDataReload = DateTime.Now;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult changedData = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([current]);
            changedData.ThrowIfFailed(); Assert.IsTrue(changedData.Single().Updated);
            current = playlist.BMSTables.Single(); Assert.IsTrue(current.last_update >= beforeDataReload && current.last_update <= DateTime.Now);
            Assert.AreEqual(originalAdd, current.entries.Single().adddate); Assert.AreEqual("Keep memo", current.entries.Single().memo);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), path).OpenSongDbReadOnly();
            BMSTable persisted = verify.Table<BMSTable>().Single(); BMSTableEntry entry = verify.Table<BMSTableEntry>().Single();
            Assert.AreEqual(current.last_update, persisted.last_update); Assert.AreEqual(originalAdd, entry.adddate); Assert.AreEqual("Keep memo", entry.memo);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<Exception>();
            async Task Clean(Func<Task> work) { try { await work(); } catch (Exception error) { failures.Add(error); } }
            if (playlist != null) { await Clean(playlist.WaitForPlaylistMutationIdleAsync); }
            while (tasks.TryDequeue(out Task? task)) { await Clean(() => task); }
            playlist?.RequestShutdown("date-hash-reload");
            await Clean(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            if (failures.Count > 0) { if (primary == null) { throw new AggregateException(failures); } Console.WriteLine(new AggregateException(failures)); }
        }
    }

    /// <summary>同本文の実data転送A→Bを既存BMT/台帳を残す通常reloadへ通し、相対URL1/2をDBと実gzipへ反映します。次の同入力通常出力は投影/writeを省略します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_RedirectedRelativeUrlsReachExistingBmtAndNextOutputSkips()
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        var httpRequests = new List<Task>();
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); return ownedServer; }

        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        const string dataJson = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Relative song\",\"level\":\"1\",\"url\":\"../packages/song.zip?Token=A\",\"url_diff\":\"patches/fix.zip?Key=B\"}]";

        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(httpOperation.Token);
        TestBmsPlaylist? playlist = null;
        Task? operation = null;
        Exception? failure = null;
        var acceptedWork = new System.Collections.Concurrent.ConcurrentQueue<(string Store, Task Work)>();
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OwnStartupWork(TestBmsPlaylist store, string storeName)
        {
            store.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                // 元のHTTP/DB/BMT観測までは保持し、受理した実delegateを一度呼ぶunwrapped Taskを所有する。
                var task = Task.Run(async () =>
                {
                    await releaseWork.Task.ConfigureAwait(false);
                    await work().ConfigureAwait(false);
                });
                acceptedWork.Enqueue((storeName, task));
                return true;
            };
        }

        try
        {
            SingleRequestHttpServer final = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, Encoding.UTF8.GetBytes(dataJson)));
            Uri finalData = new(final.Address, "published/current/rows.json");
            SingleRequestHttpServer redirect = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, [], responseStatusCode: HttpStatusCode.Redirect,
                additionalResponseHeaders: "Location: " + finalData.AbsoluteUri + "\r\n"));
            string headerJson = "{\"name\":\"Relative context\",\"symbol\":\"R\",\"data_url\":\"" + new Uri(redirect.Address, "data.json").AbsoluteUri + "\"}";
            SingleRequestHttpServer header = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, Encoding.UTF8.GetBytes(headerJson)));
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string bmtDirectory = Path.Combine(directory, "bmt");
            playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = true,
                BeatorajaBmtTablePath = bmtDirectory,
                BeatorajaBmtHashOutputMode = nameof(BeatorajaBmtHashOutputMode.Original)
            }, () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = new ObservableCollection<BMSTable>() };
            OwnStartupWork(playlist, nameof(playlist));
            Uri originalHeader = new(header.Address, "original/header.json");
            var original = new BMSTable(headerJson, null, originalHeader)
            {
                Data_url = new Uri("https://old-data.test/archive/rows.json"),
                name = "Manual relative table",
                last_update = new DateTime(2024, 6, 1, 10, 20, 30)
            };
            // data基準をAへ確定してから同じ本文を解析し、事前gzipの相対URLを実際にAへ解決する。
            original.LoadDataJSON(dataJson);
            original.entries.Single().memo = "local memo";
            original.entries.Single().adddate = new DateTime(2023, 7, 15, 9, 30, 0);
            operation = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(original, false, "relative-bmt-seed", cancellation.Token);
            await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token);
            string bmtPath = Path.Combine(bmtDirectory, BMSTable.ComputeSha256Hex(originalHeader.AbsoluteUri) + ".bmt");
            string manifestPath = Path.Combine(bmtDirectory, BmtTableExportService.ManifestFileName);
            Assert.IsTrue(File.Exists(manifestPath));
            JObject oldSong = ReadRedirectedBmtSong(bmtPath);
            Assert.AreEqual("https://old-data.test/packages/song.zip?Token=A", oldSong.Value<string>("url"));
            Assert.AreEqual("https://old-data.test/archive/patches/fix.zip?Key=B", oldSong.Value<string>("appendurl"));
            DateTime lastUpdate = original.last_update;
            DateTime addDate = original.entries.Single().adddate;
            string headerHash = original.header_sha256, dataHash = original.data_sha256;
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            playlist.BmtOutput.ExportProgressReporter = progress.Enqueue;

            Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult> reload = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [original], reason: "redirect-relative-bmt", cancellationToken: cancellation.Token);
            operation = reload;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await SingleRequestHttpServer.AwaitOperationAsync(reload, httpRequests, httpOperation.Token);
            results.ThrowIfFailed();
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            BMSTable current = playlist.BMSTables.Single();
            Uri expectedUrl = new(final.Address, "published/packages/song.zip?Token=A");
            Uri expectedDiff = new(final.Address, "published/current/patches/fix.zip?Key=B");
            Assert.AreSame(current, result.ResultTable);
            Assert.AreNotSame(original, current);
            Assert.IsFalse(result.Updated);
            Assert.IsTrue(result.StatePersisted);
            Assert.IsTrue(result.NeedsBmtExport);
            Assert.IsNotNull(result.UpdateReceipt);
            Assert.IsTrue(result.UpdateReceipt.ReferenceEntriesChanged);
            Assert.AreEqual(originalHeader, current.Header_url);
            Assert.AreEqual(finalData, current.GetAbsoluteDataUrl());
            Assert.AreEqual(headerHash, current.header_sha256);
            Assert.AreEqual(dataHash, current.data_sha256);
            Assert.AreEqual(lastUpdate, current.last_update);
            Assert.AreEqual(original.playlist_id, current.playlist_id);
            Assert.AreEqual("Manual relative table", current.name);
            Assert.AreEqual("local memo", current.entries.Single().memo);
            Assert.AreEqual(addDate, current.entries.Single().adddate);
            Assert.AreEqual(expectedUrl, current.entries.Single().Url);
            Assert.AreEqual(expectedDiff, current.entries.Single().Url_diff);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                LR2SongDBExtended.playlist_entry persisted = db.Table<LR2SongDBExtended.playlist_entry>().Single();
                Assert.AreEqual(expectedUrl, new Uri(persisted.url));
                Assert.AreEqual(expectedDiff, new Uri(persisted.url_diff));
                Assert.AreEqual(lastUpdate, db.Table<BMSTable>().Single().last_update);
            }
            JObject newSong = ReadRedirectedBmtSong(bmtPath);
            Assert.AreEqual(expectedUrl.AbsoluteUri, newSong.Value<string>("url"));
            Assert.AreEqual(expectedDiff.AbsoluteUri, newSong.Value<string>("appendurl"));
            Assert.IsTrue(progress.Any(snapshot => snapshot.IsActive), "URL解決先の差をmetadataだけで誤skipせず必要投影へ進めます。");
            byte[] updatedBytes = File.ReadAllBytes(bmtPath);
            DateTime updatedTime = File.GetLastWriteTimeUtc(bmtPath);
            progress.Clear();

            // 同じ実file状態を保った次の通常要求。mtime操作・force・追加HTTP取得で省略判定を迂回しない。
            operation = playlist.BmtOutput.ExportTablesAsync([current], "redirect-relative-bmt-repeat");
            await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token);
            Assert.IsFalse(progress.Any(snapshot => snapshot.IsActive));
            Assert.AreEqual(updatedTime, File.GetLastWriteTimeUtc(bmtPath));
            CollectionAssert.AreEqual(updatedBytes, File.ReadAllBytes(bmtPath));
            BmtTableExportService.PlaylistExportMetadata metadata = BmtTableExportService.CreatePlaylistExportMetadata(current);
            Assert.IsFalse(BmtTableExportService.CreateExportPlan(bmtDirectory, [metadata], cleanupStaleManagedFiles: false).RequiresProjection(metadata));
            CollectionAssert.AreEqual(new[] { "/original/header.json" }, header.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/data.json" }, redirect.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/published/current/rows.json" }, final.RequestTargets);
            await (Task.WhenAll(header.Completion, redirect.Completion, final.Completion)).WaitAsync(httpOperation.Token);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, failure, httpRequests.Concat(new Task?[] { operation }.OfType<Task>().Where(task => !task.IsCompleted)), httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            finally { releaseWork.TrySetResult(); }
            try
            {
                var cleanupFailures = new List<Exception>();
                // 主operationとHTTPのproducer終端後に固定し、store別の実work異常も主失敗と区別する。
                foreach (IGrouping<string, (string Store, Task Work)> storeWork in acceptedWork.ToArray().GroupBy(item => item.Store))
                {
                    var workCompletion = Task.WhenAll(storeWork.Select(item => item.Work));
                    try { await (workCompletion).WaitAsync(httpCleanup.Token); }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            "転送テストの任意startup work終了失敗: " + storeWork.Key, workCompletion.Exception ?? ex));
                    }
                }
                if (playlist != null)
                {
                    try
                    {
                        playlist.RequestShutdown("redirect-relative-bmt-complete");
                        await (playlist.WaitForPlaylistMutationIdleAsync()).WaitAsync(httpCleanup.Token);
                        Assert.IsFalse(playlist.HasShutdownBlockingWork);
                    }
                    catch (Exception ex) { cleanupFailures.Add(ex); }
                }
                try { httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(directory, recursive: true); migrationWorkspace.Dispose(); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
                if (cleanupFailures.Count > 0)
                {
                    var cleanupFailure = new AggregateException(cleanupFailures);
                    if (failure == null) { throw cleanupFailure; }
                    System.Diagnostics.Trace.WriteLine("転送BMTのcleanup二次失敗: " + cleanupFailure);
                }

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (failure != null) { throw new AggregateException(failure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    /// <summary>書込み終端後に毎回新しいgzip readerを開き、実媒体に保存された単一曲を返します。</summary>
    private static JObject ReadRedirectedBmtSong(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var table = JObject.Parse(reader.ReadToEnd());
        return (JObject)table.SelectTokens("folder[*].songs[*]").Single();
    }

    /// <summary>同じ本文・全絶対entryの転送先dataだけが移動しても通常reloadで保存・新参照公開し、日時・hash・ローカル属性・元同期元を維持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_RedirectedDataContextPersistsWithoutUpdatedFlag()
    {
        var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var httpServers = new List<SingleRequestHttpServer>();
        var httpRequests = new List<Task>();
        SingleRequestHttpServer OwnHttpServer(SingleRequestHttpServer ownedServer) { httpServers.Add(ownedServer); return ownedServer; }

        using var httpOperation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        httpOperation.CancelAfter(TimeSpan.FromSeconds(30));

        const string dataJson = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Absolute song\",\"level\":\"1\",\"url\":\"https://packages.test/song.zip\",\"url_diff\":\"https://packages.test/fix.zip\"}]";

        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(httpOperation.Token);
        TestBmsPlaylist? playlist = null, reader = null;
        Task? operation = null;
        Exception? failure = null;
        var acceptedWork = new System.Collections.Concurrent.ConcurrentQueue<(string Store, Task Work)>();
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OwnStartupWork(TestBmsPlaylist store, string storeName)
        {
            store.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                // 元のHTTP/DB/BMT観測までは保持し、受理した実delegateを一度呼ぶunwrapped Taskを所有する。
                var task = Task.Run(async () =>
                {
                    await releaseWork.Task.ConfigureAwait(false);
                    await work().ConfigureAwait(false);
                });
                acceptedWork.Enqueue((storeName, task));
                return true;
            };
        }

        try
        {
            SingleRequestHttpServer final = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, Encoding.UTF8.GetBytes(dataJson)));
            Uri finalData = new(final.Address, "published/current/rows.json");
            SingleRequestHttpServer redirect = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, [], responseStatusCode: HttpStatusCode.Redirect,
                additionalResponseHeaders: "Location: " + finalData.AbsoluteUri + "\r\n"));
            string headerJson = "{\"name\":\"Context table\",\"symbol\":\"C\",\"data_url\":\"" + new Uri(redirect.Address, "data.json").AbsoluteUri + "\"}";
            SingleRequestHttpServer header = OwnHttpServer(new SingleRequestHttpServer(httpOperation.Token, Encoding.UTF8.GetBytes(headerJson)));
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = new ObservableCollection<BMSTable>() };
            OwnStartupWork(playlist, nameof(playlist));
            Uri originalHeader = new(header.Address, "original/header.json");
            // 過去の成功時と同じ本文をparserへ通し、移動前の実data基準だけを保存済み状態にする。
            var original = new BMSTable(headerJson, null, originalHeader, dataJson)
            {
                Data_url = new Uri("https://old-data.test/archive/rows.json"),
                name = "Manual table name",
                symbol = "M",
                last_update = new DateTime(2024, 6, 1, 10, 20, 30),
                is_bmt_output = false
            };
            original.EnableExternalSync();
            original.entries.Single().memo = "local memo";
            original.entries.Single().adddate = new DateTime(2023, 7, 15, 9, 30, 0);
            operation = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(original, false, "redirect-context-seed", cancellation.Token);
            await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token);
            DateTime oldLastUpdate = original.last_update;
            DateTime oldAddDate = original.entries.Single().adddate;
            string oldHeaderHash = original.header_sha256;
            string oldDataHash = original.data_sha256;
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt? published = null;
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, args) => published = args.Receipt;

            Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult> reload = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [original], reason: "redirect-context-test", cancellationToken: cancellation.Token, publishReferenceReceipts: true);
            operation = reload;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await SingleRequestHttpServer.AwaitOperationAsync(reload, httpRequests, httpOperation.Token);
            results.ThrowIfFailed();
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            BMSTable current = playlist.BMSTables.Single();
            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(result.Updated);
            Assert.IsTrue(result.StatePersisted);
            Assert.IsTrue(result.NeedsBmtExport);
            Assert.AreNotSame(original, current);
            Assert.AreSame(current, result.ResultTable);
            Assert.IsNotNull(result.UpdateReceipt);
            Assert.AreSame(current, result.UpdateReceipt.NewTable);
            Assert.AreSame(result.UpdateReceipt, published);
            Assert.IsFalse(result.UpdateReceipt.ReferenceEntriesChanged);
            Assert.IsFalse(result.UpdateReceipt.Updated);
            Assert.IsNull(current.Page_url);
            Assert.AreEqual(originalHeader, current.Header_url);
            Assert.AreEqual(finalData, current.GetAbsoluteDataUrl());
            Assert.AreEqual(original.playlist_id, current.playlist_id);
            Assert.AreEqual("Manual table name", current.name);
            Assert.AreEqual("M", current.symbol);
            Assert.AreEqual(original.Output_dir, current.Output_dir);
            Assert.AreEqual(original.ignore_folder_output, current.ignore_folder_output);
            Assert.AreEqual(original.is_bmt_output, current.is_bmt_output);
            Assert.AreEqual(original.bmt_sort, current.bmt_sort);
            Assert.AreEqual(oldLastUpdate, current.last_update);
            Assert.AreEqual(oldHeaderHash, current.header_sha256);
            Assert.AreEqual(oldDataHash, current.data_sha256);
            Assert.AreEqual("local memo", current.entries.Single().memo);
            Assert.AreEqual(oldAddDate, current.entries.Single().adddate);
            Assert.AreEqual(new Uri("https://packages.test/song.zip"), current.entries.Single().Url);
            Assert.AreEqual(new Uri("https://packages.test/fix.zip"), current.entries.Single().Url_diff);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                BMSTable persisted = db.Table<BMSTable>().Single();
                Assert.AreEqual(finalData, persisted.GetAbsoluteDataUrl());
                Assert.AreEqual(originalHeader, persisted.Header_url);
                Assert.AreEqual(oldLastUpdate, persisted.last_update);
                Assert.AreEqual(oldDataHash, persisted.data_sha256);
                LR2SongDBExtended.playlist_entry entry = db.Table<LR2SongDBExtended.playlist_entry>().Single();
                Assert.AreEqual("local memo", entry.memo);
                Assert.AreEqual(oldAddDate, entry.adddate);
            }
            reader = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, uiScheduler: new TestUiScheduler(() => null));
            OwnStartupWork(reader, nameof(reader));
            operation = reader.ReloadTablesAsync(exportBeatorajaBmt: false);
            await SingleRequestHttpServer.AwaitOperationAsync(operation, httpRequests, httpOperation.Token);
            BMSTable hydrated = reader.BMSTables.Single();
            reader.EnsurePlaylistEntriesLoaded(hydrated, "redirect-context-test");
            Assert.AreEqual(finalData, hydrated.GetAbsoluteDataUrl());
            Assert.AreEqual(originalHeader, hydrated.Header_url);
            Assert.AreEqual(oldLastUpdate, hydrated.last_update);
            Assert.AreEqual("local memo", hydrated.entries.Single().memo);
            Assert.AreEqual(oldAddDate, hydrated.entries.Single().adddate);
            Assert.AreEqual(current.entries.Single().Url, hydrated.entries.Single().Url);
            Assert.AreEqual(current.entries.Single().Url_diff, hydrated.entries.Single().Url_diff);
            CollectionAssert.AreEqual(new[] { "/original/header.json" }, header.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/data.json" }, redirect.RequestTargets);
            CollectionAssert.AreEqual(new[] { "/published/current/rows.json" }, final.RequestTargets);
            await (Task.WhenAll(header.Completion, redirect.Completion, final.Completion)).WaitAsync(httpOperation.Token);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            using var httpCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            httpOperation.Cancel();
            Exception? httpJoinFailure = null;
            try { await SingleRequestHttpServer.CompleteCleanupAsync(httpCleanup.Token, failure, httpRequests.Concat(new Task?[] { operation }.OfType<Task>().Where(task => !task.IsCompleted)), httpServers.ToArray()); }
            catch (Exception httpJoinError) { httpJoinFailure = httpJoinError; }
            finally { releaseWork.TrySetResult(); }
            try
            {
                var cleanupFailures = new List<Exception>();
                // 主operationとHTTPのproducer終端後に固定し、store別の実work異常も主失敗と区別する。
                foreach (IGrouping<string, (string Store, Task Work)> storeWork in acceptedWork.ToArray().GroupBy(item => item.Store))
                {
                    var workCompletion = Task.WhenAll(storeWork.Select(item => item.Work));
                    try { await (workCompletion).WaitAsync(httpCleanup.Token); }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            "転送テストの任意startup work終了失敗: " + storeWork.Key, workCompletion.Exception ?? ex));
                    }
                }
                foreach (TestBmsPlaylist? store in new[] { reader, playlist })
                {
                    if (store == null) { continue; }
                    try
                    {
                        store.RequestShutdown("redirect-context-test-complete");
                        await (store.WaitForPlaylistMutationIdleAsync()).WaitAsync(httpCleanup.Token);
                        Assert.IsFalse(store.HasShutdownBlockingWork);
                    }
                    catch (Exception ex) { cleanupFailures.Add(ex); }
                }
                try { httpCleanup.Token.ThrowIfCancellationRequested(); Directory.Delete(directory, recursive: true); migrationWorkspace.Dispose(); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
                if (cleanupFailures.Count > 0)
                {
                    var cleanupFailure = new AggregateException(cleanupFailures);
                    if (failure == null) { throw cleanupFailure; }
                    System.Diagnostics.Trace.WriteLine("転送reloadのcleanup二次失敗: " + cleanupFailure);
                }

            }
            catch (Exception httpSecondaryError)
            {
                if (httpJoinFailure != null) { throw new AggregateException(httpJoinFailure, httpSecondaryError); }
                if (failure != null) { throw new AggregateException(failure, httpSecondaryError); }
                throw;
            }
            if (httpJoinFailure != null) { throw httpJoinFailure; }
        }
    }

    /// <summary>
    /// 単表入口と二表入口で、正本置換後の実BMT失敗が確定参照を隠さず、
    /// 二表の取得失敗とも別に返ることを確認します。解除後の変更なしと次操作まで実DBで観測します。
    /// 置換前の拒否・中断は既存UIスケジューラ検証へ分担します。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_OutputFailureReturnsConfirmedReferencesAndSeparateTargetFailures(bool singleTarget)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        FileStream? blocker = null;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string header = Path.Combine(directory, "header.json");
            string data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Reload\",\"symbol\":\"R\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt }, () => new CustomFolderOutputSettingsSnapshot(), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            original.playlist_id = 7501;
            original.is_bmt_output = true;
            original.Output_dir = "Reload";
            original.entries[0].memo = "local-memo";
            var bad = new BMSTable { playlist_id = 7502, name = "Bad", Page_url = new Uri(Path.Combine(directory, "missing.json")), entries = [], is_bmt_output = false };
            playlist.BMSTables = new ObservableCollection<BMSTable>(singleTarget ? [original] : [original, bad]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"After\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            blocker = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var reported = new ConcurrentQueue<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = singleTarget
                ? await playlist.ExternalSyncOwner.ReloadAndApplySingleTableAsync(original, new Uri(header), "reload-output-failure")
                : await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original, bad], syncResultCallback: reported.Enqueue);
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult confirmed = result.Single(target => ReferenceEquals(target.SourceTable, original));
            Assert.IsNotNull(result.OperationException);
            Assert.IsNotNull(result.Exception, "派生出力の失敗を最終操作結果へ保持します。");
            if (!singleTarget)
            {
                Assert.AreEqual(result.Count, reported.Count);
                Assert.AreSame(confirmed, reported.Single(target => ReferenceEquals(target.SourceTable, original)));
            }
            Assert.IsTrue(confirmed.Succeeded, "対象確定時の成功を後続BMT失敗で書き換えません。");
            Assert.IsNotNull(confirmed.UpdateReceipt);
            Assert.IsTrue(confirmed.StatePersisted);
            Assert.IsNull(confirmed.Exception);
            Assert.AreNotSame(original, confirmed.ResultTable);
            Assert.AreSame(confirmed.ResultTable, playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id));
            Assert.AreEqual("local-memo", confirmed.ResultTable.entries.Single(entry => entry.md5 == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").memo);
            if (!singleTarget)
            {
                PlaylistExternalSyncOwner.PlaylistReloadTargetResult failed = result.Single(target => ReferenceEquals(target.SourceTable, bad));
                Assert.AreSame(failed, reported.Single(target => ReferenceEquals(target.SourceTable, bad)));
                Assert.IsTrue(failed.IsHandledExternalLoadFailure);
                Assert.IsNotNull(failed.Exception);
                Assert.IsNull(failed.UpdateReceipt);
                Assert.AreSame(bad, failed.ResultTable);
            }
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(singleTarget ? 1 : 2, db.Table<BMSTable>().Count());
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == original.playlist_id));
            }
            blocker.Dispose();
            blocker = null;
            DateTime updated = confirmed.ResultTable.last_update;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([confirmed.ResultTable]);
            unchanged.ThrowIfFailed();
            Assert.IsFalse(unchanged.Single().StatePersisted);
            Assert.AreSame(confirmed.ResultTable, unchanged.Single().ResultTable);
            Assert.AreEqual(updated, unchanged.Single().ResultTable.last_update);
            playlist.RemoveBMSTable(unchanged.Single().ResultTable);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly();
            Assert.AreEqual(singleTarget ? 0 : 1, verify.Table<BMSTable>().Count());
        }
        finally
        {
            blocker?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 手動単表と任意複数表の実再取得で、実BMT失敗と確定参照採用後の詳細通知失敗を重ね、両原因・DB・P/Taskと進捗終端・次要求を保ちます。
    /// 実BMT開始通知を保持してN/NでもHub行と実Taskが存続することを確認し、次に詳細表示を保持します。実保存・BMTは置換しません。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceResync_PostAdoptionPresentationFailureRetainsOutputFailureAndConfirmedState(bool optionalBatch)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBmt = new ManualResetEventSlim();
        var bmtEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel? workspace = null;
        Task? operation = null;
        FileStream? blocker = null;
        bool failPresentation = true;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Presentation failure\",\"symbol\":\"P\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var library = new TestBmsLibrary(databaseScope, songPath, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings);
            var playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt }, () => new CustomFolderOutputSettingsSnapshot(), CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            table.playlist_id = 7511;
            table.is_bmt_output = true;
            Assert.IsTrue(table.EnableExternalSync());
            var other = new BMSTable
            {
                playlist_id = 7512,
                name = "Other target",
                Page_url = new Uri(header),
                Header_url = new Uri(header),
                Data_url = new Uri(data),
                entries = []
            };
            Assert.IsTrue(other.EnableExternalSync());
            playlist.BMSTables = new ObservableCollection<BMSTable>(optionalBatch ? [table, other] : [table]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            workspace = CreatePlaylistWorkspace(playlist, library);
            var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
            var presentation = new ConcurrentQueue<Action>();
            void DrainPresentation() { while (presentation.TryDequeue(out Action? action)) { action(); } }
            hub.AttachPlaylistProgressSources(workspace, presentation.Enqueue, () => false);
            string source = optionalBatch ? "external_playlist_sync" : "playlist_manual_reload";
            playlist.BmtOutput.ExportProgressReporter = snapshot =>
            {
                if (snapshot.IsActive && !bmtEntered.Task.IsCompleted)
                {
                    bmtEntered.TrySetResult();
                    releaseBmt.Wait();
                }
            };
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("1"));
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            var presentationFailure = new InvalidOperationException("post-adoption detail presentation failed");
            PlaylistWorkspaceViewModel currentWorkspace = workspace;
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) =>
            {
                if (!failPresentation) { return; }
                Assert.AreNotSame(table, currentWorkspace.CapturePlaylistDetailSelection().Table);
                Assert.AreSame(playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id), currentWorkspace.CapturePlaylistDetailSelection().Table);
                entered.TrySetResult();
                release.Wait();
                throw presentationFailure;
            };
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"level\":\"2\"}]");
            blocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            operation = Task.Run(async () =>
            {
                if (optionalBatch) { await currentWorkspace.RunExternalPlaylistSyncAsync("post-adoption-failure", false, true, 21); }
                else { await currentWorkspace.ResyncPlaylistTableAsync(table); }
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(bmtEntered.Task, operation, "resync-bmt-start");
            DrainPresentation();
            PlaylistSyncProgressSnapshot allProcessed = progress.Last(snapshot => snapshot.IsActive && snapshot.Source == source
                && snapshot.CompletedTableCount == snapshot.TotalTableCount);
            Assert.AreEqual(optionalBatch ? 2 : 1, allProcessed.CompletedTableCount);
            Assert.AreEqual(allProcessed.TotalTableCount, allProcessed.CompletedTableCount);
            Assert.IsFalse(operation.IsCompleted, "N/NはBMTを含む複合操作の終端ではありません。");
            OperationProgressRow pendingOutput = hub.Rows.Single(row => row.Key.StartsWith("playlist:" + source + ":", StringComparison.Ordinal));
            Assert.AreEqual((double)allProcessed.CompletedTableCount, pendingOutput.Value);
            Assert.AreEqual((double)allProcessed.TotalTableCount, pendingOutput.Maximum);
            Assert.AreEqual(string.Format(Resources.Playlist_reload_processed_label_format,
                optionalBatch ? Resources.Statusbar_progress_task_external_playlist_sync : Resources.Statusbar_progress_task_playlist_manual_reload,
                allProcessed.CompletedTableCount, allProcessed.TotalTableCount), pendingOutput.Label);
            Assert.AreEqual(string.Format(Resources.Playlist_reload_last_processed_label_format, allProcessed.CurrentTableName), pendingOutput.Detail);
            releaseBmt.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "resync-post-adoption-presentation");
            Assert.IsFalse(operation.IsCompleted);
            bool enteredP = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsFalse(enteredP);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == table.playlist_id));
            }
            release.Set();
            Exception? failure = null;
            try { await operation; } catch (Exception caught) { failure = caught; }
            Assert.IsNotNull(failure);
            IReadOnlyList<Exception> failures = new AggregateException(failure).Flatten().InnerExceptions;
            Assert.IsTrue(failures.Any(cause => cause is PlaylistMutationPostCommitException), "元BMT失敗を後段通知失敗が隠しません。");
            Assert.IsTrue(failures.Any(cause => ReferenceEquals(cause, presentationFailure)));
            Assert.IsFalse(progress.Last().IsActive);
            DrainPresentation();
            Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:" + source + ":", StringComparison.Ordinal)));
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            failPresentation = false;
            blocker.Dispose();
            blocker = null;
            BMSTable confirmed = playlist.BMSTables.Single(candidate => candidate.playlist_id == table.playlist_id);
            if (optionalBatch) { await workspace.RunExternalPlaylistSyncAsync("after-presentation-failure", false, true, 22); }
            else { await workspace.ResyncPlaylistTableAsync(confirmed); }
            DrainPresentation();
            Assert.IsFalse(hub.HasRows);
            Assert.AreSame(confirmed, workspace.CapturePlaylistDetailSelection().Table);
        }
        finally
        {
            releaseBmt.Set();
            release.Set();
            if (operation != null) { try { await operation; } catch (Exception) { } }
            blocker?.Dispose();
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 実JSON再取得・SQLite確定・参照置換・BMT成功またはmanifest故障へ、推薦実力の実変化で生じるInformationの表示・診断故障を重ねます。
    /// 有効行と削除履歴の有限集合をmodel/DBで照合し、BMTは有効行だけ、manifest故障では先行生成物と旧台帳を保ちます。
    /// BMT Warningを別に観測し、終端Information配送中のP/Taskと実結果・完了事実、次の明示JSON変更と空receiptの非表示を保ちます。削除履歴の分岐はPlaylistReloadMergeTests、実modalは上位へ分担します。
    /// </summary>
    [TestMethod]
    [DataRow(false)] // BMT成功を実Informationの表示・診断故障で失敗へ変えない。
    [DataRow(true)] // manifest失敗のWarningと終端Informationを分け、必須出力原因を保つ。
    [TestCategory("Playlist")]
    public async Task ExternalPlaylistSync_ReceiptDisplayFailurePreservesCommittedResultAndNextRequest(bool failBmt)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var receiptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        Task? operation = null;
        FileStream? blocker = null;
        bool failPresentation = true;
        try
        {
            const string OldMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string ChangedMd5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string FreshMd5 = "cccccccccccccccccccccccccccccccc";
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            byte[] originalManifest = File.ReadAllBytes(manifest);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Receipt boundary\",\"symbol\":\"P\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var library = new TestBmsLibrary(databaseScope, songPath, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings);
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songPath, () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = true }, playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(), beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt }, recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            original.playlist_id = 7513;
            original.is_bmt_output = true;
            Assert.IsTrue(original.EnableExternalSync());
            BMSTable recommendation = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
            recommendation.playlist_id = 7514;
            recommendation.org_name = "旧方針 ★3.87";
            recommendation.header_sha256 = null;
            recommendation.data_sha256 = null;
            recommendation.is_bmt_output = false;
            Assert.IsTrue(recommendation.EnableExternalSync());
            Assert.IsFalse(recommendation.entries.Any(entry => entry.md5 is OldMd5 or ChangedMd5 or FreshMd5),
                "推薦入力とJSON入力のhashを分離し、JSON表の索引反映を完全一致で観測します。");
            playlist.BMSTables = new ObservableCollection<BMSTable>([original, recommendation]);
            playlist.CommitBMSTablesWithEntriesToDB([original, recommendation]);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(OldMd5, null).Symbols);
            var oldReferenceKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, OldMd5);
            var changedReferenceKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, ChangedMd5);
            PlaylistReferenceReadSnapshot initialReferences = library.CapturePlaylistReferenceReadSnapshot([oldReferenceKey, changedReferenceKey]);
            Assert.AreEqual("P", initialReferences.Values[oldReferenceKey].Symbols);
            Assert.IsFalse(initialReferences.Values.ContainsKey(changedReferenceKey));
            var diagnostics = new List<(Exception Exception, string Diagnostic)>();
            var diagnosticFailure = new InvalidOperationException("external receipt diagnostic failed");
            workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
                reloadFailureLog: (exception, diagnostic) =>
                {
                    diagnostics.Add((exception, diagnostic));
                    throw diagnosticFailure;
                },
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            var bmtFailures = new List<BmtTableExportService.FileOperationFailure>();
            playlist.BmtOutput.FailureReporter = failures =>
            {
                bmtFailures.AddRange(failures);
                workspace.ReportBmtOutputFailures(failures);
            };
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.RequestDetailSelection(original, PlaylistFolderNode.CreateFolder("1"));
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            var receipts = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            var displayAttempts = new List<(string Route, PlaylistOperationNotificationOwner.OperationNotification Notification)>();
            var presentationFailure = new InvalidOperationException("external receipt display failed");
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            workspace.PlaylistExternalSyncCompleted += (_, completion) => completed.Add(completion);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                receipts.Add(request);
                foreach (PlaylistOperationNotificationOwner.OperationNotification notification in request.Receipt.Notifications)
                {
                    displayAttempts.Add((request.RouteName, notification));
                    // BMT Warningは必要出力の独立通知です。終端Informationだけを保持します。
                    if (request.RouteName == "external playlist sync notification" && failPresentation)
                    {
                        receiptEntered.TrySetResult();
                        releaseReceipt.Task.GetAwaiter().GetResult();
                        throw presentationFailure;
                    }
                }
            };
            File.WriteAllText(data, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"level\":\"2\"}]");
            if (failBmt) { blocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read); }
            PlaylistWorkspaceViewModel currentWorkspace = workspace;
            operation = Task.Run(() => currentWorkspace.RunExternalPlaylistSyncAsync("receipt-display-failure", false, true, 31));
            await TestUiDispatcherHost.AwaitNotificationAsync(receiptEntered.Task, operation, "external-sync.receipt-display");
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(0, completed.Count, "receipt配送の実終端前に完了事実を公開しません。");
            Assert.AreEqual(failBmt ? 2 : 1, receipts.Count);
            PlaylistOperationNotificationPresentationRequestedEventArgs terminalReceipt = receipts.Single(request =>
                request.RouteName == "external playlist sync notification");
            Assert.IsFalse(terminalReceipt.Receipt.IsEmpty);
            PlaylistOperationNotificationOwner.OperationNotification information = terminalReceipt.Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, information.Severity);
            Assert.AreEqual(Resources.Recommend_SkillUpdatedTitle, information.Caption);
            double expectedSkill = ReadWalkureCase("standard")["rating"]?.Value<double>("playerStarRating")
                ?? throw new AssertFailedException("The independent recommendation skill expectation is missing.");
            Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage,
                expectedSkill.ToString("F2"), (expectedSkill - 3.87).ToString(" (+#0.00); (-#0.00);")), information.Message);
            Assert.AreSame(information, displayAttempts.Single(attempt => attempt.Route == "external playlist sync notification").Notification);
            PlaylistOperationNotificationPresentationRequestedEventArgs[] bmtReceipts = receipts.Where(request =>
                request.RouteName == "beatoraja BMT output failure").ToArray();
            Assert.AreEqual(failBmt ? 1 : 0, bmtReceipts.Length);
            bool acceptedWhilePresenting = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsFalse(acceptedWhilePresenting);
            Task mutationIdle = playlist.WaitForPlaylistMutationIdleAsync();
            Assert.IsFalse(mutationIdle.IsCompleted);
            Assert.AreEqual(2, playlist.BMSTables.Count);
            BMSTable confirmed = playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id);
            Assert.AreNotSame(original, confirmed);
            Assert.AreSame(confirmed, workspace.CapturePlaylistDetailSelection().Table);
            (int? PlaylistId, string Md5, bool IsRemoved, string Title, double? Level)[] expectedConfirmedEntries =
            [
                (original.playlist_id, ChangedMd5, false, "After", 2),
                (original.playlist_id, OldMd5, true, "Before", 1)
            ];
            Assert.AreEqual(2, confirmed.entries.Count);
            CollectionAssert.AreEquivalent(expectedConfirmedEntries, confirmed.entries
                .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
            BMSTableEntry active = confirmed.entries.Single(entry => !entry.is_removed);
            Assert.AreEqual(ChangedMd5, active.md5);
            Assert.AreEqual(2, active.level);
            Assert.AreEqual("After", active.title);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(OldMd5, null).Symbols);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(ChangedMd5, null).Symbols);
            PlaylistReferenceReadSnapshot confirmedReferences = library.CapturePlaylistReferenceReadSnapshot([oldReferenceKey, changedReferenceKey]);
            Assert.IsTrue(confirmedReferences.Revision > initialReferences.Revision);
            Assert.IsFalse(confirmedReferences.Values.ContainsKey(oldReferenceKey));
            Assert.AreEqual("P", confirmedReferences.Values[changedReferenceKey].Symbols);
            Assert.AreEqual("P", initialReferences.Values[oldReferenceKey].Symbols);
            Assert.IsFalse(initialReferences.Values.ContainsKey(changedReferenceKey));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                var savedEntries = db.Table<BMSTableEntry>()
                    .Where(entry => entry.playlist_id == original.playlist_id).ToList();
                Assert.AreEqual(2, savedEntries.Count);
                CollectionAssert.AreEquivalent(expectedConfirmedEntries, savedEntries
                    .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
                BMSTableEntry saved = savedEntries.Single(entry => !entry.is_removed);
                Assert.AreEqual(original.playlist_id, saved.playlist_id);
                Assert.AreEqual(ChangedMd5, saved.md5);
                Assert.AreEqual(2, saved.level);
                Assert.AreEqual("After", saved.title);
            }
            releaseReceipt.TrySetResult();
            Exception? observedFailure = null;
            try { await operation; }
            catch (Exception failure) { observedFailure = failure; }
            await mutationIdle;
            string outputFile = Directory.GetFiles(bmt, "*.bmt").Single();
            JObject exported = BeatorajaBmtTableImportService.ReadCachedTableData(outputFile);
            JToken song = exported["folder"]?.Single()?["songs"]?.Single()
                ?? throw new AssertFailedException("The required BMT row was not exported.");
            Assert.AreEqual(ChangedMd5, song.Value<string>("md5"));
            Assert.AreEqual("After", song.Value<string>("title"));
            if (failBmt)
            {
                Assert.IsInstanceOfType<PlaylistMutationPostCommitException>(observedFailure);
                var failure = (PlaylistMutationPostCommitException)observedFailure;
                Exception cause = failure.InnerException ?? throw new AssertFailedException("The required BMT cause was lost.");
                Assert.IsInstanceOfType<IOException>(cause.GetBaseException());
                StringAssert.Contains(cause.Message, manifest);
                Assert.IsTrue(operation.IsFaulted);
                PlaylistOperationNotificationOwner.OperationNotification warning = bmtReceipts.Single().Receipt.Notifications.Single();
                Assert.IsFalse(bmtReceipts.Single().Receipt.IsEmpty);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, warning.Severity);
                Assert.AreEqual(Resources.MessageBoxTitle_Warning, warning.Caption);
                BmtTableExportService.FileOperationFailure manifestFailure = bmtFailures.Single();
                Assert.AreEqual(bmt, manifestFailure.Path, "manifest公開の例外はBMT出力先の失敗として通知されます。");
                StringAssert.Contains(manifestFailure.Cause, manifest);
                StringAssert.Contains(manifestFailure.Cause, cause.Message);
                StringAssert.Contains(manifestFailure.Cause, cause.GetBaseException().Message);
                Assert.AreEqual(string.Format(Resources.Beatoraja_bmt_output_failure_format,
                    manifestFailure.Path, manifestFailure.Cause), warning.Message);
                Assert.AreSame(warning, displayAttempts.Single(attempt => attempt.Route == "beatoraja BMT output failure").Notification);
                Assert.IsFalse(failure.ToString().Contains(presentationFailure.Message, StringComparison.Ordinal));
                Assert.IsFalse(failure.ToString().Contains(diagnosticFailure.Message, StringComparison.Ordinal));
                CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifest));
            }
            else
            {
                Assert.IsNull(observedFailure);
                Assert.IsTrue(operation.IsCompletedSuccessfully);
                var managed = JObject.Parse(File.ReadAllText(manifest));
                Assert.AreEqual(Path.GetFileName(outputFile), managed["files"]?.Single()?.Value<string>());
            }
            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual(!failBmt, completed[0].Succeeded);
            Assert.IsFalse(completed[0].WasSkipped);
            Assert.AreEqual(failBmt ? 2 : 1, receipts.Count, "表示失敗で操作や通知を再実行しません。");
            Assert.AreEqual(failBmt ? 2 : 1, displayAttempts.Count);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreSame(presentationFailure, diagnostics[0].Exception);
            StringAssert.Contains(diagnostics[0].Diagnostic, "external playlist sync notification");
            Assert.IsFalse(progress.Last().IsActive);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsFalse(playlist.HasShutdownBlockingWork);

            failPresentation = false;
            blocker?.Dispose();
            blocker = null;
            File.WriteAllText(data, "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"Fresh\",\"level\":\"3\"}]");
            operation = Task.Run(() => currentWorkspace.RunExternalPlaylistSyncAsync("fresh-json-change", false, true, 32));
            await operation;
            await playlist.WaitForPlaylistMutationIdleAsync();
            Assert.AreEqual(2, playlist.BMSTables.Count);
            BMSTable fresh = playlist.BMSTables.Single(table => table.playlist_id == original.playlist_id);
            Assert.AreNotSame(confirmed, fresh);
            Assert.AreSame(fresh, workspace.CapturePlaylistDetailSelection().Table);
            (int? PlaylistId, string Md5, bool IsRemoved, string Title, double? Level)[] expectedFreshEntries =
            [
                (original.playlist_id, FreshMd5, false, "Fresh", 3),
                (original.playlist_id, ChangedMd5, true, "After", 2),
                (original.playlist_id, OldMd5, true, "Before", 1)
            ];
            Assert.AreEqual(3, fresh.entries.Count);
            CollectionAssert.AreEquivalent(expectedFreshEntries, fresh.entries
                .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
            BMSTableEntry freshActive = fresh.entries.Single(entry => !entry.is_removed);
            Assert.AreEqual(FreshMd5, freshActive.md5);
            Assert.AreEqual(3, freshActive.level);
            Assert.AreEqual("Fresh", freshActive.title);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(OldMd5, null).Symbols);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(ChangedMd5, null).Symbols);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(FreshMd5, null).Symbols);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                var savedEntries = db.Table<BMSTableEntry>()
                    .Where(entry => entry.playlist_id == original.playlist_id).ToList();
                Assert.AreEqual(3, savedEntries.Count);
                CollectionAssert.AreEquivalent(expectedFreshEntries, savedEntries
                    .Select(entry => (entry.playlist_id, entry.md5, entry.is_removed, entry.title, entry.level)).ToArray());
                BMSTableEntry saved = savedEntries.Single(entry => !entry.is_removed);
                Assert.AreEqual(original.playlist_id, saved.playlist_id);
                Assert.AreEqual(FreshMd5, saved.md5);
                Assert.AreEqual(3, saved.level);
                Assert.AreEqual("Fresh", saved.title);
            }
            string freshOutputFile = Directory.GetFiles(bmt, "*.bmt").Single();
            JObject freshOutput = BeatorajaBmtTableImportService.ReadCachedTableData(freshOutputFile);
            JToken freshSong = freshOutput["folder"]?.Single()?["songs"]?.Single()
                ?? throw new AssertFailedException("The next explicit change was not exported.");
            Assert.AreEqual(FreshMd5, freshSong.Value<string>("md5"));
            Assert.AreEqual("Fresh", freshSong.Value<string>("title"));
            var freshManaged = JObject.Parse(File.ReadAllText(manifest));
            Assert.AreEqual(Path.GetFileName(freshOutputFile), freshManaged["files"]?.Single()?.Value<string>());
            Assert.AreEqual(2, completed.Count);
            Assert.IsTrue(completed[1].Succeeded);
            Assert.IsFalse(completed[1].WasSkipped);
            Assert.AreEqual(failBmt ? 3 : 2, receipts.Count);
            PlaylistOperationNotificationPresentationRequestedEventArgs[] terminalReceipts = receipts.Where(request =>
                request.RouteName == "external playlist sync notification").ToArray();
            Assert.AreEqual(2, terminalReceipts.Length);
            Assert.IsTrue(terminalReceipts[1].Receipt.IsEmpty, "推薦skillが不変の次操作は表示内容を生成しません。");
            Assert.AreEqual(failBmt ? 2 : 1, displayAttempts.Count, "空receiptのevent配送では表示を試みません。");
            Assert.AreEqual(1, diagnostics.Count, "前操作の表示失敗を次の明示要求へ持ち越しません。");
            Assert.IsFalse(progress.Last().IsActive);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            Assert.IsFalse(playlist.HasShutdownBlockingWork);
        }
        finally
        {
            releaseReceipt.TrySetResult();
            // 本体で観測済みのfaultを再awaitして元の失敗位置を書き換えず、未終端だけを回収します。
            if (operation is { IsCompleted: false }) { try { await operation; } catch (Exception) { } }
            blocker?.Dispose();
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>三推薦の実登録・再取得で利用者状態、日時、不要保存抑止、通知とLR2/BMT出力を保持し、保存行の所属とmemoは新主体の本番hydrationで確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalRecommendations_KeepUserStateAndAvoidUnchangedWritesThenReachLr2AndBmt()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null, reader = null;
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            var repository = new PlaylistPersistenceRepository(databaseScope, songPath);
            int reads = 0;
            string selectedInput = "mixed";
            CustomFolderOutputSettingsSnapshot settings = new() { ShowRecommUpdatedMsg = true };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songPath, CustomFolderOutputPhysicalSurface.Empty);
            playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => settings, synchronization, recommendationScoreReader: _ =>
                {
                    reads++;
                    return Task.FromResult(ReadWalkureInput(selectedInput));
                }, settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            using PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession();
            Uri[] uris = [new("bmseeker:table.recommended"), new("bmseeker:table.recommended?base=failed"), new("bmseeker:table.recommended?failed=noplay")];
            List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loaded = await playlist.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(uris.Select(uri => new BMSTable { Page_url = uri }), false);
            Assert.AreEqual(1, reads);
            await playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(loaded.Select(item => item.ExternalTable), false, "local-recommendations");
            Assert.AreEqual(3, playlist.BMSTables.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty, "取得・新規登録だけでは実力変化を通知しません。");
            BMSTable original = playlist.BMSTables.Single(table => table.Page_url == uris[0]);
            original.name = "My recommendations";
            original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            original.symbol = "CUSTOM";
            var prefixEdit = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = "KEEP " };
            original.ApplyEntryMutation(original.PrepareHeaderMutation(prefixEdit));
            original.Output_dir = "LocalRecommendations";
            original.custom_folder_output_base_name = "CustomName";
            original.bmt_sort = 42;
            original.is_bmt_output = false;
            original.entries[0].memo = "keep-user-memo";
            playlist.CommitBMSTableWithEntriesToDB(original);
            int? id = original.playlist_id;
            DateTime updatedAt = original.last_update;
            string hash = original.data_sha256;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.AreEqual(2, reads);
            Assert.IsTrue(unchanged.All(item => item.Succeeded && !item.Updated));
            Assert.IsTrue(unchanged.Single(item => item.ResultTable.playlist_id == id).StatePersisted);
            Assert.IsTrue(unchanged.Where(item => item.ResultTable.playlist_id != id).All(item => !item.StatePersisted));
            string mixedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                ReadWalkureCase("mixed")["rating"]!.Value<double>("playerStarRating").ToString("F2", CultureInfo.InvariantCulture));
            BMSTable renamed = playlist.BMSTables.Single(table => table.playlist_id == id);
            Assert.AreEqual(mixedName, renamed.name);
            Assert.AreEqual(mixedName, renamed.org_name);
            Assert.AreEqual(0, synchronization.Operations.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            BMSTable persisted = repository.LoadPlaylistHeaders().Single(table => table.playlist_id == id);
            Assert.AreEqual(updatedAt, persisted.last_update);
            Assert.AreEqual(hash, persisted.data_sha256);
            Assert.AreEqual(mixedName, persisted.name);
            Assert.AreEqual(mixedName, persisted.org_name);
            Assert.AreEqual("CUSTOM", persisted.symbol);
            Assert.AreEqual("KEEP ", persisted.compat_prefix);
            Assert.AreEqual("LocalRecommendations", persisted.Output_dir);
            Assert.AreEqual("CustomName", persisted.custom_folder_output_base_name);
            Assert.AreEqual(42, persisted.bmt_sort);
            Assert.IsFalse(persisted.is_bmt_output);
            reader = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            await reader.ReloadTablesAsync(exportBeatorajaBmt: false);
            BMSTable hydrated = reader.BMSTables.Single(table => table.playlist_id == id);
            reader.EnsurePlaylistEntriesLoaded(hydrated, "recommendation-state-hydration-test");
            Assert.IsTrue(hydrated.entries.Any(entry => entry.memo == "keep-user-memo"));
            Assert.IsTrue(hydrated.entries.All(entry => ReferenceEquals(hydrated, entry.parent) && entry.playlist_id == id));
            Assert.IsTrue(renamed.entries.All(entry => ReferenceEquals(renamed, entry.parent) && entry.playlist_id == id));

            // 数値を変えず org_name だけ旧表記にした場合もヘッダーへ保存する。
            renamed.org_name = "旧方針 ★3.87";
            playlist.CommitBMSTableWithEntriesToDB(renamed);
            unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(unchanged.Single(item => item.ResultTable.playlist_id == id).StatePersisted);
            persisted = repository.LoadPlaylistHeaders().Single(table => table.playlist_id == id);
            Assert.AreEqual(mixedName, persisted.org_name);
            Assert.AreEqual(mixedName, playlist.BMSTables.Single(table => table.playlist_id == id).org_name);
            Assert.AreEqual(updatedAt, persisted.last_update);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);

            unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(unchanged.All(item => item.Succeeded && !item.Updated && !item.StatePersisted));
            Assert.AreEqual(0, synchronization.Operations.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);

            selectedInput = "standard";
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult changed = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.AreEqual(5, reads);
            Assert.IsTrue(changed.All(item => item.Succeeded && item.Updated && item.StatePersisted));
            Assert.AreEqual(3, session.TakeReceipt().Notifications.Count);
            BMSTable updated = playlist.BMSTables.Single(table => table.playlist_id == id);
            string standardName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                ReadWalkureCase("standard")["rating"]!.Value<double>("playerStarRating").ToString("F2", CultureInfo.InvariantCulture));
            Assert.AreEqual(standardName, updated.name);
            Assert.AreEqual(standardName, updated.org_name);
            Assert.AreEqual("CUSTOM", updated.symbol);
            Assert.AreEqual("KEEP ", updated.compat_prefix);
            Assert.AreEqual("LocalRecommendations", updated.Output_dir);
            Assert.AreEqual(42, updated.bmt_sort);
            Assert.IsFalse(updated.is_bmt_output);
            Assert.IsTrue(repository.LoadPersistedPlaylistEntries(id, false).Any(entry => entry.memo == "keep-user-memo"));

            // 通知設定を無効にした、成功反映の次回操作でも実力更新通知を出しません。
            settings = new CustomFolderOutputSettingsSnapshot();
            selectedInput = "mixed";
            changed = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(changed.All(item => item.Succeeded && item.Updated));
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            updated = playlist.BMSTables.Single(table => table.playlist_id == id);
            // 保持した利用者の追加先名を登録して出力します。未登録名の停止条件は出力担当のテストへ分担します。
            string additionalOutputBase = Path.Combine(directory, "CustomName");
            settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = Path.Combine(directory, "Output"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase])
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([updated], "local-recommendations-output");
            string lr2Path = Path.Combine(additionalOutputBase, updated.Output_dir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(lr2Path));
            StringAssert.Contains(ReadShiftJisText(lr2Path), "#TITLE");
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verify.Table<LR2SongDB.folder>().Any(row => row.path == lr2Path));
            }
            string bmtDirectory = Path.Combine(directory, "Bmt");
            string bmtName = BmtTableExportService.ExportTableData(bmtDirectory, BmtTableExportService.BuildTableData(updated), id.ToString());
            Assert.IsTrue(File.Exists(Path.Combine(bmtDirectory, bmtName)));
            // 既存BMT契約のtag＋互換levelを固定lamp対応から期待し、保存prefixを出力名へ残さない。
            var bmtFolders = new Dictionary<string, string>
            {
                ["KEEP EASY"] = "CUSTOMEASY",
                ["KEEP NORMAL"] = "CUSTOMNORMAL",
                ["KEEP HARD"] = "CUSTOMHARD",
                ["KEEP FC"] = "CUSTOMFC"
            };
            // 同じ譜面の複数目標も有効行ごとに、実際の圧縮出力から照合します。
            string[] expectedBmtRows = updated.entries.Where(entry => !entry.is_removed && !string.IsNullOrWhiteSpace(entry.title))
                .Select(entry => entry.md5 + ":" + bmtFolders[entry.folder]).ToArray();
            JObject actualBmt = BeatorajaBmtTableImportService.ReadCachedTableData(Path.Combine(bmtDirectory, bmtName));
            Assert.AreEqual(updated.Page_url.OriginalString, actualBmt.Value<string>("url"));
            Assert.AreEqual(mixedName, actualBmt.Value<string>("name"));
            Assert.AreEqual("CUSTOM", actualBmt.Value<string>("tag"));
            CollectionAssert.AreEqual(bmtFolders.Where(pair => updated.entries.Any(entry => !entry.is_removed && !string.IsNullOrWhiteSpace(entry.title) && entry.folder == pair.Key)).Select(pair => pair.Value).ToArray(),
                ((JArray)(actualBmt["folder"] ?? throw new FormatException())).Select(folder => folder.Value<string>("name")).ToArray());
            CollectionAssert.AreEquivalent(expectedBmtRows,
                ((JArray)(actualBmt["folder"] ?? throw new FormatException())).SelectMany(folder =>
                    ((JArray)(folder["songs"] ?? throw new FormatException())).Select(song => song.Value<string>("md5") + ":" + folder.Value<string>("name"))).ToArray());
        }
        finally
        {
            reader?.RequestShutdown("local-recommendations-test-complete");
            playlist?.RequestShutdown("local-recommendations-test-complete");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>保存済みの旧難度、通知設定、保存成否を変えて初回hash保存を行い、数値の難度変化だけを通知します。保存失敗で旧状態を戻さない誤りと、名前変更や同値表記だけで通知する誤りを検出します。</summary>
    [TestMethod]
    [DataRow("旧方針 ★3.87", "standard", true, false, 1)]
    [DataRow("旧方針 ★6.6", "standard", true, false, 0)]
    [DataRow("旧方針 ★6.60", "mixed", true, false, 1)]
    [DataRow("手動名のみ", "standard", true, false, 0)]
    [DataRow("旧方針 ★3.87", "standard", false, false, 0)]
    [DataRow("旧方針 ★3.87", "standard", true, true, 0)]
    [TestCategory("Playlist")]
    public async Task LocalRecommendations_HashInitializationAndSaveFailureKeepSkillNotificationBoundary(
        string oldOrgName, string inputName, bool showNotification, bool failSave, int notificationCount)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        const string triggerName = "recommendation_test_write_failure";
        try
        {
            string songPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            var repository = new PlaylistPersistenceRepository(databaseScope, songPath);
            playlist = new TestBmsPlaylist(databaseScope, songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = showNotification }, recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput(inputName)), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
            original.playlist_id = 9071;
            original.name = "Manual name ★999";
            original.org_name = oldOrgName;
            original.header_sha256 = null;
            original.data_sha256 = null;
            original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
            playlist.CommitBMSTableWithEntriesToDB(original);
            BMSTable before = repository.LoadPlaylistHeaders().Single();
            using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
            if (failSave)
            {
                using LR2SongDBExtended writeFailure = databaseScope.OpenSongWrite(songPath);
                writeFailure.Execute("CREATE TRIGGER " + triggerName + " BEFORE INSERT ON playlist WHEN NEW.playlist_id = 9071 "
                    + "BEGIN SELECT RAISE(ABORT, 'recommendation DB write failure'); END;");
            }
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results;
            try
            {
                results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original]);
            }
            finally
            {
                if (failSave)
                {
                    using LR2SongDBExtended cleanup = databaseScope.OpenSongWrite(songPath);
                    cleanup.Execute("DROP TRIGGER " + triggerName + ";");
                }
            }
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            Assert.IsFalse(result.Updated);
            Assert.AreEqual(!failSave, result.Succeeded);
            Assert.AreEqual(!failSave, result.StatePersisted);
            Assert.AreEqual(original.last_update, result.ResultTable.last_update);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            BMSTable persisted = repository.LoadPlaylistHeaders().Single();
            Assert.AreEqual(original.last_update, persisted.last_update);
            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = notifications.TakeReceipt();
            Assert.AreEqual(notificationCount, receipt.Notifications.Count);
            if (failSave)
            {
                Assert.AreSame(original, playlist.BMSTables.Single());
                Assert.AreEqual(before.name, persisted.name);
                Assert.AreEqual(before.org_name, persisted.org_name);
                Assert.AreEqual(before.header_sha256, persisted.header_sha256);
                Assert.AreEqual(before.data_sha256, persisted.data_sha256);
                // 障害を除いた別操作を受理でき、元の操作の再試行は行わない。
                Assert.IsTrue((await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original])).Single().Succeeded);
            }
            else
            {
                double expectedSkill = ReadWalkureCase(inputName)["rating"]!.Value<double>("playerStarRating");
                string expectedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                    expectedSkill.ToString("F2", CultureInfo.InvariantCulture));
                Assert.AreEqual(expectedName, persisted.name);
                Assert.AreEqual(expectedName, persisted.org_name);
                Assert.AreEqual(expectedName, playlist.BMSTables.Single().name);
                Assert.AreEqual(expectedName, playlist.BMSTables.Single().org_name);
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
                Assert.AreEqual(original.entries.Count, repository.LoadPersistedPlaylistEntries(original.playlist_id, false).Count());
                if (notificationCount == 1)
                {
                    double oldSkill = inputName == "mixed" ? 6.60 : 3.87;
                    Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage,
                        expectedSkill.ToString("F2"), (expectedSkill - oldSkill).ToString(" (+#0.00); (-#0.00);")),
                        receipt.Notifications.Single().Message);
                }
            }
        }
        finally
        {
            playlist?.RequestShutdown("recommendation-hash-test-complete");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>同期portの元例外をそのまま返し、DBパスをディレクトリで塞いでもDBアクセスの例外へ置き換えません。port失敗後にDB保存を続ける誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_PropagatesSynchronizationFailureBeforeDatabaseAccess()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output");
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            var originalException = new InvalidOperationException("forced LR2 folder sync failure");
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope,
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.Failure = originalException;
            var table = new BMSTable
            {
                playlist_id = 9002,
                name = "SyncCapabilityFailure",
                symbol = "SCF",
                Output_dir = "SyncCapabilityFailure",
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder")],
                Folder_order = ["Folder"]
            };
            BMSPlaylist playlist = CreatePlaylist(databaseScope, songDbPath, synchronization, settings: testSettings, modelUiScheduler: new TestUiScheduler(() => null));
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_capability_failure"));

            Assert.AreSame(originalException, exception);
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実再取得の同receipt・DBと参照bucketを確定し、旧表と捕捉値を保持します。購読者と同期callbackの二重失敗でも最初の原因を返すことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_PublishesCommittedReferencesBeforeFailingCallback()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"SnapshotTable\",\r\n\"symbol\":\"S\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath, () => new CustomFolderOutputSettingsSnapshot(), settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900011;
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            playlist.CommitBMSTableWithEntriesToDB(table);
            var oldKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, new string('a', 32));
            var newKey = new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, new string('c', 32));
            PlaylistReferenceReadSnapshot before = library.CapturePlaylistReferenceReadSnapshot([oldKey, newKey]);
            Assert.AreEqual("S", before.Values[oldKey].Symbols);
            Assert.IsFalse(before.Values.ContainsKey(newKey));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"New Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            var receiptFailure = new InvalidOperationException("receipt consumer failure");
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                throw receiptFailure;
            };

            PlaylistExternalSyncOwner.PlaylistReloadTargetResult? reportedTarget = null;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult operation = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: target =>
                {
                    reportedTarget = target;
                    throw new InvalidOperationException("sync result callback failure");
                },
                publishReferenceReceipts: true);
            Assert.AreSame(receiptFailure, operation.Exception);
            Assert.AreSame(operation.Single(), reportedTarget);
            Assert.AreSame(receiptFailure, reportedTarget?.Exception);
            Assert.AreSame(receipt, reportedTarget?.UpdateReceipt);
            Assert.AreSame(receipt.NewTable, operation.Single().ResultTable);

            Assert.IsNotNull(receipt);
            Assert.AreSame(table, receipt!.OldTable);
            Assert.IsNotNull(receipt.NewTable);
            Assert.IsFalse(playlist.BMSTables.Contains(table));
            Assert.AreSame(receipt.NewTable, playlist.BMSTables.Single());
            Assert.AreEqual(new string('a', 32), table.entries.Single().md5);
            Assert.AreSame(table, table.entries.Single().parent);
            Assert.IsTrue(receipt.ReferenceEntriesChanged);
            PlaylistReferenceReadSnapshot after = library.CapturePlaylistReferenceReadSnapshot([oldKey, newKey]);
            Assert.IsTrue(after.Revision > before.Revision);
            Assert.IsFalse(after.Values.ContainsKey(oldKey));
            Assert.AreEqual("S", after.Values[newKey].Symbols);
            Assert.AreEqual("S", before.Values[oldKey].Symbols);
            Assert.IsFalse(before.Values.ContainsKey(newKey));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            List<LR2SongDBExtended.playlist_entry> persisted = verify.Query<LR2SongDBExtended.playlist_entry>(
                "SELECT * FROM playlist_entry WHERE playlist_id = ? AND COALESCE(is_removed, 0) = 0;", table.playlist_id);
            Assert.AreEqual(1, persisted.Count);
            Assert.AreEqual(new string('c', 32), persisted.Single().md5);
            Assert.AreEqual(new string('b', 64), persisted.Single().sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>未設定hashを実再取得・保存してもUpdatedと日時は進めず、手動名、新旧表の行所属と参照変更receiptを保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_HashInitializationPersistsWithoutUpdatedFlag()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"HashInitTable\",\r\n\"symbol\":\"H\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.name = "Manual generic name";
            table.playlist_id = 900001;
            table.header_sha256 = null;
            table.data_sha256 = null;
            var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
            table.last_update = existingLastUpdate;
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries)
                    {
                        db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                });
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Repaired Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
            };

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                reason: "test_hash_initialization",
                publishReferenceReceipts: true);

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Updated);
            Assert.IsTrue(results[0].StatePersisted);
            Assert.IsNotNull(receipt);
            Assert.IsFalse(receipt!.Updated);
            Assert.IsTrue(receipt.ReferenceEntriesChanged);
            Assert.AreEqual(existingLastUpdate, results[0].ResultTable.last_update);
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.data_sha256));
            Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            Assert.IsTrue(table.entries.All(entry => ReferenceEquals(table, entry.parent) && entry.playlist_id == table.playlist_id));
            Assert.IsTrue(results[0].ResultTable.entries.All(entry => ReferenceEquals(results[0].ResultTable, entry.parent) && entry.playlist_id == table.playlist_id));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == 900001);
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
            Assert.AreEqual(existingLastUpdate, persisted.last_update);
            Assert.AreEqual("Manual generic name", persisted.name);
            Assert.AreEqual("Manual generic name", results[0].ResultTable.name);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 900001 AND is_removed = 0;"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同一source hashの旧地域誤読levelも明示reloadで実JSONから訂正し、新しいDB接続と所属・ローカル属性を保ちます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_SameSourceHashRepairsOldRegionalLevel()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string header = Path.Combine(directory, "header.json");
            File.WriteAllText(header, "{\"name\":\"Regional repair\",\"compat_prefix\":\"\",\"data_url\":\"data.json\"}");
            File.WriteAllText(Path.Combine(directory, "data.json"), "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Repair level\",\"level\":\"★1.5\"}]");
            string dbPath = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
            var playlist = new TestBmsPlaylist(databaseScope, dbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, dbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable old = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            old.playlist_id = 9017;
            old.is_bmt_output = false;
            old.entries.Single().level = 15; // 同一原文を旧地域解釈で保存した値を明示して投入します。
            old.entries.Single().memo = "local memo";
            DateTime date = old.entries.Single().adddate;
            string hash = old.data_sha256;
            playlist.BMSTables = new ObservableCollection<BMSTable>([old]);
            playlist.CommitBMSTableWithEntriesToDB(old);
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([old]);
            result.ThrowIfFailed();
            Assert.IsTrue(result.Single().Succeeded);
            Assert.IsTrue(result.Single().StatePersisted);
            BMSTable repaired = playlist.BMSTables.Single();
            Assert.AreEqual(hash, repaired.data_sha256);
            Assert.AreEqual(old.playlist_id, repaired.playlist_id);
            BMSTableEntry entry = repaired.entries.Single();
            Assert.AreEqual(1.5, entry.level);
            Assert.AreEqual("★1.5", entry.folder);
            Assert.AreSame(repaired, entry.parent);
            Assert.AreEqual("local memo", entry.memo);
            Assert.AreEqual(date, entry.adddate);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), dbPath).OpenSongDbReadOnly();
            BMSTableEntry saved = verify.Table<BMSTableEntry>().Single();
            Assert.AreEqual(1.5, saved.level);
            Assert.AreEqual(repaired.playlist_id, saved.playlist_id);
            Assert.AreEqual("local memo", saved.memo);
        }
        finally { CultureInfo.CurrentCulture = previous; Directory.Delete(directory, recursive: true); }
    }

    /// <summary>実外部再取得でfolder投影を保存・LR2出力へ更新し、新旧表の行所属と採番IDを保持します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReoutputsLr2FolderProjectionAfterPersistedEntryChange()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalFolderProjection\",\r\n\"symbol\":\"E\",\r\n\"tag\":\"LEVEL \",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Projection Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, synchronization, settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900002;
            table.Output_dir = "ExternalReloadProjection";
            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            playlist.ReOutputCustomFolder(table);

            string outputPath = Path.Combine(outputBaseDir, table.Output_dir, "0000.lr2folder");
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE LEVEL 1");
            using (LR2SongDBExtended verifyInitial = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("LEVEL 1", verifyInitial.Table<LR2SongDB.folder>().Single(row => row.path == outputPath).title);
            }
            synchronization.Operations.Clear();
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Projection Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                reason: "test_external_reload_custom_folder_projection");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsTrue(results[0].StatePersisted);
            BMSTable reloadedTable = results[0].ResultTable;
            Assert.AreSame(reloadedTable, playlist.BMSTables.Single());
            Assert.IsTrue(table.entries.All(entry => ReferenceEquals(table, entry.parent) && entry.playlist_id == table.playlist_id));
            Assert.IsTrue(reloadedTable.entries.All(entry => ReferenceEquals(reloadedTable, entry.parent) && entry.playlist_id == table.playlist_id));
            Assert.AreEqual("LEVEL 2", reloadedTable.entries.Single(entry => !entry.is_removed).folder);
            string reloadedText = ReadShiftJisText(outputPath);
            StringAssert.Contains(reloadedText, "#TITLE LEVEL 2");
            Assert.IsFalse(reloadedText.Contains("#TITLE LEVEL 1", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
            using (LR2SongDBExtended verifyReloaded = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    "LEVEL 2",
                    verifyReloaded.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        900002,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
                LR2SongDB.folder folderRow = verifyReloaded.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
                Assert.AreEqual("LEVEL 2", folderRow.title);
                StringAssert.Contains(folderRow.command, "playlist_entry");
            }

            synchronization.Operations.Clear();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult unchangedResults = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [reloadedTable],
                reason: "test_external_reload_custom_folder_projection_no_change");

            Assert.AreEqual(1, unchangedResults.Count);
            Assert.IsTrue(unchangedResults[0].Succeeded);
            Assert.IsFalse(unchangedResults[0].Updated);
            Assert.IsFalse(unchangedResults[0].StatePersisted);
            Assert.AreEqual(0, synchronization.Operations.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>LR2同期予約中はフォルダの改名・削除・作成、行のフォルダ追加・削除をモデル、DB、出力へ反映せず、解放後の次の明示変更を一回同期します。拒否の副作用と後続要求を拒み続ける誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalPlaylistMutations_RejectBusyWithoutEffectsAndAcceptNextExplicitMutation()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        foreach (LocalPlaylistMutationCase mutationCase in CreateLocalPlaylistMutationCases())
        {
            string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            IDisposable? incumbent = null;
            try
            {
                string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
                string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
                PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
                CustomFolderOutputSettingsSnapshot outputSettings = CreateLocalMutationOutputSettings(tempDirectory, outputBaseDir, operationModeLr2Db: true);
                TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope,
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty);
                var admission = new ChartFileOperationSynchronizer();
                var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, synchronization, mutationAdmission: admission, settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
                BMSTable table = CreateLocalMutationTable();
                playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                playlist.CommitBMSTableWithEntriesToDB(table);
                playlist.ReOutputCustomFolder(table);
                synchronization.Operations.Clear();

                PlaylistModelSnapshot modelBefore = CapturePlaylistModel(table);
                byte[] databaseBefore = File.ReadAllBytes(songDbPath);
                Dictionary<string, byte[]> filesBefore = CaptureFileSurface(outputBaseDir);
                Assert.IsTrue(admission.TryEnter(out incumbent));

                Task<Exception?> busyCall = Task.Run(() => CaptureMutationFailure(mutationCase, playlist, table));
                Exception? busyException = await busyCall;
                Assert.IsNotNull(busyException, mutationCase.Name + " must reject while the lease is busy.");

                AssertPlaylistModelUnchanged(modelBefore, CapturePlaylistModel(table));
                CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(songDbPath));
                AssertFileSurfaceUnchanged(filesBefore, CaptureFileSurface(outputBaseDir));
                Assert.AreEqual(0, synchronization.Operations.Count, mutationCase.Name + " must not reach LR2 output while admission is rejected.");

                incumbent.Dispose();
                incumbent = null;
                object retryResult = mutationCase.Invoke(playlist, table, true);
                Assert.IsTrue(mutationCase.WasApplied(retryResult), mutationCase.Name + " retry should apply the requested mutation.");

                CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
                AssertPersistedEntriesMatchModel(databaseScope, table, songDbPath);
                Assert.IsTrue(CaptureFileSurface(outputBaseDir).Count > 0, mutationCase.Name + " retry should leave an app-managed custom-folder surface.");
            }
            finally
            {
                incumbent?.Dispose();
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }

    /// <summary>未commitの変更と単体モードのcommitではLR2出力を変えず、前者はDBも保持し後者はDBへ保存します。保存有無と外部出力の条件を混同する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LocalPlaylistMutations_SkipLr2OutputForUncommittedAndStandaloneChanges()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        foreach (LocalPlaylistMutationCase mutationCase in CreateLocalPlaylistMutationCases())
        {
            foreach ((bool CommitFlag, bool OperationModeLr2Db) in new[]
            {
                (false, true),
                (true, false)
            })
            {
                string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                try
                {
                    string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
                    string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
                    PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
                    CustomFolderOutputSettingsSnapshot outputSettings = CreateLocalMutationOutputSettings(
                        tempDirectory,
                        outputBaseDir,
                        OperationModeLr2Db);
                    TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope,
                        songDbPath,
                        CustomFolderOutputPhysicalSurface.Empty);
                    var admission = new ChartFileOperationSynchronizer();
                    var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => outputSettings, synchronization, mutationAdmission: admission, settings: testSettings, uiScheduler: new TestUiScheduler(() => null));
                    BMSTable table = CreateLocalMutationTable();
                    playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    PlaylistModelSnapshot modelBefore = CapturePlaylistModel(table);
                    byte[] databaseBefore = File.ReadAllBytes(songDbPath);
                    Dictionary<string, byte[]> filesBefore = CaptureFileSurface(outputBaseDir);

                    object result = mutationCase.Invoke(playlist, table, CommitFlag);
                    Assert.IsTrue(mutationCase.WasApplied(result), mutationCase.Name + " negative control should apply the in-memory mutation.");

                    Assert.IsFalse(
                        string.Equals(
                            string.Join("\u001f", modelBefore.EntryStates),
                            string.Join("\u001f", CapturePlaylistModel(table).EntryStates),
                            StringComparison.Ordinal),
                        mutationCase.Name + " negative control should change the model.");
                    AssertFileSurfaceUnchanged(filesBefore, CaptureFileSurface(outputBaseDir));
                    Assert.AreEqual(0, synchronization.Operations.Count);
                    if (!CommitFlag)
                    {
                        CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(songDbPath));
                    }
                    else
                    {
                        AssertPersistedEntriesMatchModel(databaseScope, table, songDbPath);
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
        }
    }

    /// <summary>実外部表一件の再取得で初期0件には名前・URIを付けず、試行終了時だけ対象と件数を届けます。終端では空名と完了件数を保ち、開始名の混入と終了漏れを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReportsPlaylistSyncProgressSnapshots()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ProgressTable\",\r\n\"symbol\":\"P\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Progress Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            List<PlaylistSyncProgressSnapshot> snapshots = [];

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult updated = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: null,
                progressCallback: snapshots.Add);

            Assert.IsNotNull(updated);
            Assert.IsTrue(snapshots.Count >= 3);
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 0));
            Assert.IsTrue(snapshots.Where(s => s.IsActive && s.CompletedTableCount == 0)
                .All(s => string.IsNullOrEmpty(s.CurrentTableName) && s.CurrentUri == null), "0件通知は開始対象名を持ちません。");
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 1 && string.Equals(s.CurrentTableName, "ProgressTable", StringComparison.Ordinal)));
            Assert.IsTrue(snapshots.Where(s => s.IsActive && s.CompletedTableCount == 1).All(s => s.CurrentUri == new Uri(headerJsonPath)));
            PlaylistSyncProgressSnapshot playlistSyncProgressSnapshot = snapshots.Last();
            Assert.IsFalse(playlistSyncProgressSnapshot.IsActive);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.TotalTableCount);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.CompletedTableCount);
            Assert.AreEqual(string.Empty, playlistSyncProgressSnapshot.CurrentTableName);
            Assert.IsNull(playlistSyncProgressSnapshot.CurrentUri);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同期対象の外部表だけを再取得し、同期を切った不正JSONの表へは結果callbackを届けません。自動同期が利用者の対象指定を無視する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReloadsOnlyExternalSyncTargets()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string externalHeaderPath = Path.Combine(tempDirectory, "external-header.json");
            string externalScorePath = Path.Combine(tempDirectory, "external-score.json");
            string manualHeaderPath = Path.Combine(tempDirectory, "manual-header.json");
            File.WriteAllBytes(externalHeaderPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./external-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(externalScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(manualHeaderPath, CreateUtf8BomBytes("{ invalid json"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable externalTable = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(externalHeaderPath));
            externalTable.EnableExternalSync();
            var manualTable = new BMSTable
            {
                name = "ManualTarget",
                symbol = "M",
                Header_url = new Uri(manualHeaderPath)
            };
            manualTable.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { externalTable, manualTable });
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> syncResults = [];

            await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: syncResults.Add);

            Assert.AreEqual(1, syncResults.Count);
            Assert.AreSame(externalTable, syncResults[0].SourceTable);
            Assert.IsTrue(syncResults[0].Succeeded);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同期を切った表でも明示再取得は実データの追加を採用し、同期設定は保持します。自動同期の対象条件を明示要求にも適用する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReloadsExplicitTargetRegardlessOfExternalSyncFlag()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ManualTarget\",\r\n\"symbol\":\"M\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9001;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([table], reason: "test_explicit_reload");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsFalse(results[0].ResultTable.is_external_sync);
            using (results[0].ResultTable.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
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

    /// <summary>保存中のURIと別の明示URIから実表を再取得し、追加行を採用します。引数の取得元を無視して旧URIを再利用する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadAndApplySingleTable_UsesExplicitUriOverride()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(tempDirectory, "first");
        string secondDirectory = Path.Combine(tempDirectory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        try
        {
            string firstHeaderPath = Path.Combine(firstDirectory, "header.json");
            string firstScorePath = Path.Combine(firstDirectory, "score.json");
            string secondHeaderPath = Path.Combine(secondDirectory, "header.json");
            string secondScorePath = Path.Combine(secondDirectory, "score.json");
            const string headerJson = "{\r\n\"name\":\"ExplicitUriTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}";
            File.WriteAllBytes(firstHeaderPath, CreateUtf8BomBytes(headerJson));
            File.WriteAllBytes(secondHeaderPath, CreateUtf8BomBytes(headerJson));
            File.WriteAllBytes(firstScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(secondScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(firstHeaderPath));
            table.playlist_id = 9002;
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult operation = await playlist.ExternalSyncOwner.ReloadAndApplySingleTableAsync(
                table,
                new Uri(secondHeaderPath),
                "test_explicit_uri");

            operation.ThrowIfFailed();
            BMSTable result = operation.Single().ResultTable;
            using (result.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, result.entries.Count(entry => !entry.is_removed));
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

    /// <summary>
    /// 実手動再取得で行・ヘッダー変更の参照置換と進捗終端を確認します。不正JSONでは現在参照とDBを保ち、
    /// STATUSに形式不正・日時・取得元を残して、Workspaceから元例外を再送出・重複記録しません。
    /// 元例外の診断はPlaylistExternalFailureBoundaryTestsの取得境界テストで確認します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceManualResync_UsesActiveTableAndPublishesLifecycle()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "workspace-header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "workspace-score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"WorkspaceTarget\",\r\n\"symbol\":\"W\",\r\n\"data_url\":\"./workspace-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9011;
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
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings);
            var lifecycleLogs = new List<string>();
            var workspaceFailureLogs = new List<(Exception Exception, string Message)>();
            workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
                () => playlist,
                PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
                () => library,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                message => lifecycleLogs.Add(message),
                (exception, message) => workspaceFailureLogs.Add((exception, message)), (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck);
            workspace.ConfigureCatalogNotificationQueue(action => action());
            PlaylistWorkspaceTestPorts.AttachImmediatePlaylistPresentationRouter(workspace);
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            long summaryDataGenerationBeforeResync = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder(string.Empty));
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);

            Assert.IsTrue(workspace.ContainsActivePlaylistTable(table));
            Assert.IsTrue(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = table }]));

            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            BMSTable firstSelectionTarget = await workspace.ResyncPlaylistTableAsync(table);

            Assert.AreEqual(
                1,
                lifecycleLogs.Count(log => log.StartsWith(
                    "playlist_reload_operation started operationKind=single reason=manual_resync tableCount=1",
                    StringComparison.Ordinal)));
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            progress.Clear();
            Assert.IsTrue(lifecycleLogs.Any(log => log.StartsWith(
                "playlist_reload_operation completed operationKind=single reason=manual_resync tableCount=1 processedCount=1",
                StringComparison.Ordinal)));
            Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > summaryDataGenerationBeforeResync);

            BMSTable reloadedTable = playlist.BMSTables.Single();
            Assert.AreNotSame(table, reloadedTable);
            Assert.AreSame(reloadedTable, firstSelectionTarget);
            PlaylistDetailSelection reloadedSelection = workspace.CapturePlaylistDetailSelection()
                ?? throw new AssertFailedException("Replacement detail selection was not retained.");
            Assert.AreSame(reloadedTable, reloadedSelection.Table);
            Assert.IsFalse(workspace.ContainsActivePlaylistTable(table));
            Assert.IsFalse(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = table }]));
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(reloadedTable));
            Assert.IsTrue(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = reloadedTable }]));

            reloadedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"WorkspaceTarget\",\r\n\"symbol\":\"W\",\r\n\"tag\":\"header-refresh\",\r\n\"data_url\":\"./workspace-score.json\",\r\n\"level_order\":[1]\r\n}"));
            BMSTable secondSelectionTarget = await workspace.ResyncPlaylistTableAsync(reloadedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            progress.Clear();
            BMSTable headerRefreshedTable = playlist.BMSTables.Single();
            Assert.AreNotSame(reloadedTable, headerRefreshedTable);
            Assert.AreSame(headerRefreshedTable, secondSelectionTarget);
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.IsFalse(workspace.ContainsActivePlaylistTable(reloadedTable));
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(headerRefreshedTable));

            Assert.AreEqual(
                2,
                lifecycleLogs.Count(log => log.StartsWith(
                    "playlist_reload_operation started operationKind=single reason=manual_resync tableCount=1",
                    StringComparison.Ordinal)));

            BMSTable persistedBeforeFailure = new PlaylistPersistenceRepository(databaseScope, songDbPath)
                .LoadPlaylistHeaders().Single();
            headerRefreshedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{"));
            Uri failureUri = headerRefreshedTable.Page_url ?? headerRefreshedTable.Header_url;
            BMSTable failureSelectionTarget = await workspace.ResyncPlaylistTableAsync(headerRefreshedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            Assert.AreEqual(0, workspaceFailureLogs.Count,
                "取得境界で診断した通常失敗をWorkspaceから再記録しません。");
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.AreSame(headerRefreshedTable, failureSelectionTarget);
            Assert.AreSame(headerRefreshedTable, playlist.BMSTables.Single());
            Assert.IsFalse(playlist.IsPlaylistUpdating);

            PlaylistSyncRuntimeStatus failedStatus = workspace.CapturePlaylistSyncStatusSnapshot()["id:9011"];
            Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, failedStatus.Kind);
            Assert.AreEqual(Resources.Playlist_sync_status_header, failedStatus.StatusText);
            Assert.IsTrue(failedStatus.HasFailureStatus);
            Assert.AreNotEqual(DateTime.MinValue, failedStatus.CheckedAt);
            Assert.AreEqual(string.Join(Environment.NewLine,
                failedStatus.StatusText,
                failedStatus.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                failureUri.ToString()), failedStatus.Detail,
                "STATUSには分類・日時・取得元だけを渡し、例外詳細はログへ分離します。");

            BMSTable persistedAfterFailure = new PlaylistPersistenceRepository(databaseScope, songDbPath)
                .LoadPlaylistHeaders().Single();
            Assert.AreEqual(persistedBeforeFailure.header_sha256, persistedAfterFailure.header_sha256);
            Assert.AreEqual(persistedBeforeFailure.data_sha256, persistedAfterFailure.data_sha256);
            Assert.AreEqual(persistedBeforeFailure.last_update, persistedAfterFailure.last_update);
            using LR2SongDBExtended verify = databaseScope.OpenSongWrite(songDbPath);
            Assert.AreEqual(2, verify.Table<LR2SongDBExtended.playlist_entry>()
                .Count(entry => entry.playlist_id == 9011));
        }
        finally
        {
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void AssertManualResyncProgress(ConcurrentQueue<PlaylistSyncProgressSnapshot> progress, Uri targetUri)
    {
        PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
        Assert.IsTrue(notifications.Length > 0);
        Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "playlist_manual_reload"));
        Assert.IsTrue(notifications.All(snapshot => snapshot.OperationId == 0));
        Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive
            && snapshot.TotalTableCount == 1 && snapshot.CurrentUri == targetUri));
        Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 1));
        Assert.IsFalse(notifications[^1].IsActive);
    }

    private static IReadOnlyList<LocalPlaylistMutationCase> CreateLocalPlaylistMutationCases()
    {
        return
        [
            new LocalPlaylistMutationCase(
                "rename-folder",
                (playlist, table, commitFlag) => playlist.RenameFolderBMSTable(table, "Folder A", "Folder B", commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "remove-folder",
                (playlist, table, commitFlag) => playlist.RemoveFolderBMSTable(table, "Folder A", commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "create-folder",
                (playlist, table, commitFlag) => playlist.CreateNewFolderBMSTable(table, "Folder B", commitFlag),
                result => result is string folderName && !string.IsNullOrWhiteSpace(folderName)),
            new LocalPlaylistMutationCase(
                "add-entries-to-folder",
                (playlist, table, commitFlag) => playlist.AddPlaylistEntriesToFolderBMSTable(
                    [table.entries.Single(entry => entry.md5 == LocalMutationSecondHash)],
                    table,
                    "Folder B",
                    commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "remove-entries",
                (playlist, table, commitFlag) => playlist.RemoveEntriesBMSTable(
                    [table.entries.Single(entry => entry.md5 == LocalMutationSecondHash)],
                    table,
                    commitFlag),
                result => result is true)
        ];
    }

    private static CustomFolderOutputSettingsSnapshot CreateLocalMutationOutputSettings(
        string tempDirectory,
        string outputBaseDirectory,
        bool operationModeLr2Db)
    {
        return new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = operationModeLr2Db,
            LR2RootPath = tempDirectory,
            LR2CustomFolderOutputBaseDir = outputBaseDirectory,
            LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootCustomFolder"),
            LR2CustomFolderAdditionalOutputBaseDirs = "[]"
        };
    }

    private static BMSTable CreateLocalMutationTable()
    {
        BMSTableEntry firstEntry = CreateEntry(LocalMutationFirstHash, "Folder A");
        firstEntry.folder = "Folder A";
        firstEntry.level = 1;
        firstEntry.title = "Local admission first";
        BMSTableEntry secondEntry = CreateEntry(LocalMutationSecondHash, "Folder A");
        secondEntry.folder = "Folder A";
        secondEntry.level = 2;
        secondEntry.title = "Local admission second";
        return new BMSTable
        {
            playlist_id = 990001,
            name = "LocalAdmissionTable",
            symbol = "LAT",
            Output_dir = "LocalAdmissionOutput",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
            entries = [firstEntry, secondEntry],
            Folder_order = ["Folder A"]
        };
    }

    private static Exception? CaptureMutationFailure(
        LocalPlaylistMutationCase mutationCase,
        TestBmsPlaylist playlist,
        BMSTable table)
    {
        try
        {
            mutationCase.Invoke(playlist, table, true);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static PlaylistModelSnapshot CapturePlaylistModel(BMSTable table)
    {
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new PlaylistModelSnapshot(
                table.last_update,
                [.. (table.Folder_order ?? [])],
                [.. (table.entries ?? []).Select(CreateEntryState)]);
        }
    }

    private static void AssertPlaylistModelUnchanged(
        PlaylistModelSnapshot expected,
        PlaylistModelSnapshot actual)
    {
        Assert.AreEqual(expected.LastUpdate, actual.LastUpdate);
        CollectionAssert.AreEqual(expected.FolderOrder.ToArray(), actual.FolderOrder.ToArray());
        CollectionAssert.AreEqual(expected.EntryStates.ToArray(), actual.EntryStates.ToArray());
    }

    /// <summary>呼出し元と同じDB所有者で保存状態の期待結果を確認します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private static void AssertPersistedEntriesMatchModel(ApplicationDatabaseScope databaseScope, BMSTable table, string songDbPath)
    {
        string[] expected;
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            expected = [.. (table.entries ?? []).Select(CreateEntryState).OrderBy(state => state, StringComparer.Ordinal)];
        }

        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
        string[] actual =
        [
            .. db.Table<LR2SongDBExtended.playlist_entry>()
                .Where(entry => entry.playlist_id == table.playlist_id)
                .Select(CreatePersistedEntryState)
                .OrderBy(state => state, StringComparer.Ordinal)
        ];
        CollectionAssert.AreEqual(expected, actual);
    }

    private static string CreateEntryState(BMSTableEntry entry)
    {
        return string.Join(
            "\u001f",
            entry?.md5 ?? string.Empty,
            entry?.sha256 ?? string.Empty,
            entry?.folder ?? string.Empty,
            entry?.is_removed == true ? "1" : "0");
    }

    private static string CreatePersistedEntryState(LR2SongDBExtended.playlist_entry entry)
    {
        return string.Join(
            "\u001f",
            entry?.md5 ?? string.Empty,
            entry?.sha256 ?? string.Empty,
            entry?.folder ?? string.Empty,
            entry?.is_removed == true ? "1" : "0");
    }

    private static Dictionary<string, byte[]> CaptureFileSurface(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(rootDirectory, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertFileSurfaceUnchanged(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (string path in expected.Keys)
        {
            CollectionAssert.AreEqual(expected[path], actual[path], path);
        }
    }

    private const string LocalMutationFirstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string LocalMutationSecondHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class LocalPlaylistMutationCase
    {
        internal LocalPlaylistMutationCase(
            string name,
            Func<TestBmsPlaylist, BMSTable, bool, object> invoke,
            Func<object, bool> wasApplied)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
            WasApplied = wasApplied ?? throw new ArgumentNullException(nameof(wasApplied));
        }

        internal string Name { get; }

        internal Func<TestBmsPlaylist, BMSTable, bool, object> Invoke { get; }

        internal Func<object, bool> WasApplied { get; }
    }

    private sealed class PlaylistModelSnapshot
    {
        internal PlaylistModelSnapshot(
            DateTime lastUpdate,
            IReadOnlyList<string> folderOrder,
            IReadOnlyList<string> entryStates)
        {
            LastUpdate = lastUpdate;
            FolderOrder = folderOrder ?? [];
            EntryStates = entryStates ?? [];
        }

        internal DateTime LastUpdate { get; }

        internal IReadOnlyList<string> FolderOrder { get; }

        internal IReadOnlyList<string> EntryStates { get; }
    }

}
