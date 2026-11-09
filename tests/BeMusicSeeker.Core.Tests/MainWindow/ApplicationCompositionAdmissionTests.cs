using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ApplicationCompositionTestSupport;

namespace BeMusicSeeker.Tests;


public sealed partial class ApplicationCompositionTests
{

    /// <summary>受理済み権限を標準構成の実表出力とLR2同期へ貸し、題名・区分・categoryをファイルとfolderへ保存して外側受付を保つことを確認し、継続権限や出力連携の欠落を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_PlaylistBindingGeneratesFileAndPersistsFolderUnderAcceptedLr2Capability()
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string outputBase = Path.Combine(scope.DirectoryPath, "Output");
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = outputBase };
        var outputSettings = new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = outputBase };
        Settings values = TestSettingsFactory.CreateIsolatedSettings(settings =>
        {
            settings.OperationModeLR2DB = true;
            settings.LR2ConfigXmlPath = TestLr2ConfigFactory.CreateLr2Config(scope.DirectoryPath).ConfigFilePath;
            settings.LR2CustomFolderOutputBaseDir = outputBase;
        });
        var composition = new ApplicationComposition(bmsLibraryOptionsProvider: () => options,
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            customFolderOutputSettingsProvider: () => outputSettings,
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.MissingEverythingBridge),
            rootFileEnumerator: new EmptyAdmissionRootFileEnumerator(),
            uiScheduler: new TestUiScheduler(() => null),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            dialogService: UndeclaredDialogs.Create());
        var profile = new LibraryProfile(true, scope.SongDbPath, [], composition.ResolveLr2Config(values.LR2ConfigXmlPath), null, false, true, false, false, "binding-output-test");
        BMSLibrary library = composition.CreateBmsLibrary(profile);
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var table = new BMSTable
        {
            playlist_id = 7301,
            name = "AcceptedTable",
            symbol = "AT",
            Output_dir = "AcceptedTable",
            ignore_folder_output = BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
            entries = [PlaylistTestFixture.CreateEntry(new string('a', 32), "Folder A")],
            Folder_order = ["Folder A"]
        };
        playlist.BMSTables = new System.Collections.ObjectModel.ObservableCollection<BMSTable>([table]);
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
        Task? operation = null;
        try
        {
            using LibraryFileMutationCapability capability = composition.OperationAdmission.CreateMutationCapability(lease);
            operation = runtime.QueueAsync("binding-output-test", force: true, prepareGeneratedData: true, capability: capability);
            await operation;
            Assert.IsTrue(composition.OperationAdmission.IsActive, "内部借用の終端は外側の受理済みleaseを解放しません。");
            string outputPath = Path.Combine(outputBase, "AcceptedTable", "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            string text = File.ReadAllText(outputPath, System.Text.Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(text, "#TITLE Folder A");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            BeMusicSeeker.Models.LR2.LR2SongDB.folder folder = verify.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual("Folder A", folder.title);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("AcceptedTable", folder.category);
            StringAssert.Contains(folder.command, "playlist_entry");
        }
        finally
        {
            try { if (operation != null) { await operation; } }
            finally { lease.Dispose(); library.RequestShutdown("binding-output-cleanup"); }
        }
        Assert.IsFalse(composition.OperationAdmission.IsActive);
    }


    /// <summary>生成設定を捕捉した後で独立項目を確定しても今回のUNSENTファイルと行を保持し、次の明示同期で除去して両完了を保存することを確認し、実行途中の設定再読込や旧定義残留を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task StandardComposition_Lr2UsesCapturedGenerationOptionAndAdoptsConfirmedOptionOnNextRequest()
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string outputA = Path.Combine(scope.DirectoryPath, "OutputA");
        BeMusicSeeker.Properties.Settings values = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2CustomFolderOutputBaseDir = outputA;
                values.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            });
        values.LR2ConfigXmlPath = TestLr2ConfigFactory.CreateLr2Config(scope.DirectoryPath).ConfigFilePath;
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCapture = new System.Threading.ManualResetEventSlim();
        bool holdCapture = false;
        var composition = new ApplicationComposition(bmsLibraryOptionsProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(values),
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            customFolderOutputSettingsProvider: () =>
            {
                var snapshot = CustomFolderOutputSettingsSnapshot.CreateCurrent(values);
                if (holdCapture)
                {
                    captured.TrySetResult();
                    releaseCapture.Wait();
                }
                return snapshot;
            },
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.MissingEverythingBridge),
            rootFileEnumerator: new EmptyAdmissionRootFileEnumerator(),
            uiScheduler: new TestUiScheduler(() => null),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            dialogService: UndeclaredDialogs.Create());
        var profile = new LibraryProfile(true, scope.SongDbPath, [], composition.ResolveLr2Config(values.LR2ConfigXmlPath), null, false, true, false, false, "capture-output-test");
        BMSLibrary library = composition.CreateBmsLibrary(profile);
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var table = new BMSTable
        {
            playlist_id = 7302,
            name = "CapturedTable",
            symbol = "CT",
            Output_dir = "CapturedTable",
            ignore_folder_output = BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                & ~BeMusicSeeker.Models.LR2.LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
            entries = [PlaylistTestFixture.CreateEntry(new string('a', 32), "Folder A")],
            Folder_order = ["Folder A"]
        };
        playlist.BMSTables = new System.Collections.ObjectModel.ObservableCollection<BMSTable>([table]);
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
        using LibraryFileMutationCapability capability = composition.OperationAdmission.CreateMutationCapability(lease);
        Task? first = null;
        Task? next = null;
        try
        {
            holdCapture = true;
            first = Task.Run(() => runtime.QueueAsync("capture-output-A", force: true, prepareGeneratedData: true, capability: capability));
            await Task.WhenAny(captured.Task, first);
            if (!captured.Task.IsCompleted) { await first; Assert.Fail("設定入力捕捉へ到達しませんでした。"); }
            values.Commit((nameof(BeMusicSeeker.Properties.Settings.EnableDownloadLr2IrScoreAndDetectUnsent), false));
            releaseCapture.Set();
            await first;
            Assert.IsTrue(File.Exists(Path.Combine(outputA, "CapturedTable", "0001.lr2folder")), "受理したAを生成処理が消費します。");
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed,
                library.GetLr2SongDbSyncStatusSnapshot().Status,
                "非配置optionの項目保存は今回捕捉した生成入力を変えません。");
            {
                string[] files = Directory.GetFiles(Path.Combine(outputA, "CapturedTable"), "*.lr2folder", SearchOption.AllDirectories);
                Assert.IsTrue(files.Any(path => File.ReadAllText(path, System.Text.Encoding.GetEncoding("shift_jis")).Contains("#TITLE UNSENT SONGS", StringComparison.Ordinal)),
                    "捕捉したA=trueの未送信定義を実ファイルへ生成します。");
                using LR2SongDBExtended firstDb = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                Assert.IsTrue(firstDb.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Any(row => row.title == "UNSENT SONGS"));
                Assert.AreEqual("Completed", firstDb.Find<BeMusicSeeker.Models.LR2.LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
            }
            lease.Dispose();
            next = runtime.QueueAsync("capture-output-B", force: true, prepareGeneratedData: true);
            await next;
            string outputPath = Path.Combine(outputA, "CapturedTable", "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("Folder A", verify.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Single(row => row.path == outputPath).title);
            {
                string[] files = Directory.GetFiles(Path.Combine(outputA, "CapturedTable"), "*.lr2folder", SearchOption.AllDirectories);
                Assert.IsFalse(files.Any(path => File.ReadAllText(path, System.Text.Encoding.GetEncoding("shift_jis")).Contains("#TITLE UNSENT SONGS", StringComparison.Ordinal)),
                    "次の明示要求では確定値B=falseを消費して管理定義を除去します。");
                Assert.IsFalse(verify.Table<BeMusicSeeker.Models.LR2.LR2SongDB.folder>().Any(row => row.title == "UNSENT SONGS"));
                Assert.AreEqual("Completed", verify.Find<BeMusicSeeker.Models.LR2.LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
            }
        }
        finally
        {
            releaseCapture.Set();
            try { if (first != null) { await first; } }
            finally
            {
                try { if (next != null) { await next; } }
                finally { lease.Dispose(); library.RequestShutdown("capture-output-cleanup"); }
            }
        }
    }

    /// <summary>実差分走査の受付保持中でも独立した表登録と対応出力を確定し、LR2全体同期が競合する条件では確定差分を保って見送ることを確認し、非交差操作の過剰排他や確定結果の破棄を検出します。</summary>
    [TestMethod]
    [DataRow(false, false)] // standalone: 表正本を登録しLR2出力は行いません。
    [DataRow(true, false)] // LR2: 表出力と差分後の全体同期まで確定します。
    [DataRow(true, true)] // LR2: 表受付が競合する全体同期だけ見送り、確定済み結果は残します。
    public async Task DailyLibraryDiffAndIndependentPlaylistRegistration_CompleteActualChangesConcurrently(bool lr2Mode, bool skipWhole)
    {

        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string music = Path.Combine(scope.DirectoryPath, "Music");
        string output = Path.Combine(scope.DirectoryPath, "Output");
        Directory.CreateDirectory(music);
        if (lr2Mode) { Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(scope.DirectoryPath, "RootOutput")); }
        string chart = Path.Combine(music, "chart.bms");
        File.WriteAllText(chart, "#PLAYER 1\n#TITLE Actual Diff\n#ARTIST Fixture\n#BPM 120\n#00111:01\n");
        BeMusicSeeker.Properties.Settings values = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = lr2Mode;
                values.LR2RootPath = scope.DirectoryPath;
                values.BMSRootPath = music;
                values.StandaloneBmsRootPaths = music;
                values.BMSInstallDir = music;
                values.LR2CustomFolderOutputBaseDir = output;
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(scope.DirectoryPath, "RootOutput");
                values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                values.EnablePlaylistUrlCompletion = false;
                values.EnableBeatorajaBmtOutput = false;
                values.ScanBmsFilesOnStartup = false;
            });
        TestBmsFactory.CreateRootSettings([music], values);
        var composition = new ApplicationComposition(settingsEditSession: new FakeSettingsEditSession { Values = values },
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(TestBmsFactory.AvailableEverythingBridge), rootFileEnumerator: new FastRootFileEnumerator(),
            uiScheduler: new TestUiScheduler(() => null),
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            dialogService: UndeclaredDialogs.Create());
        PlaylistPersistenceRepository.EnsureSchema(composition.DatabaseScope, scope.SongDbPath);
        var scanner = CapturedChartFileScanner.FromFixture([chart], new Dictionary<string, IEnumerable<string>>(), [music]);
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScan = new System.Threading.ManualResetEventSlim();
        scanner.ScanObserved = () => { scanned.TrySetResult(); releaseScan.Wait(); };
        Lr2ConfigOwner? configOwner = lr2Mode ? composition.ResolveLr2Config(values.LR2ConfigXmlPath) : null;
        var library = new TestBmsLibrary(composition.DatabaseScope, scope.SongDbPath, configOwner, null, null, composition.BmsLibraryOptionsProvider,
            TestBmsFactory.AvailableEverythingBridge, scanner, new TestUiScheduler(() => null), new FastRootFileEnumerator(),
            operationAdmission: composition.OperationAdmission, settings: values)
        { BmsCharts = [], BmsonCharts = [] };
        var profile = new LibraryProfile(lr2Mode, scope.SongDbPath, [music], configOwner, null, false, lr2Mode, false, false, "parallel-daily-test");
        BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
        var table = new BMSTable
        {
            name = "Independent",
            symbol = "I",
            Output_dir = "Independent",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
            entries = [PlaylistTestFixture.CreateEntry(new string('a', 32), "Daily Folder")],
            Folder_order = ["Daily Folder"]
        };
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => lr2Mode);
        int references = 0;
        var reloadOwner = new FileDiffReloadWorkflowOwner(request => Task.Run(() => library.ReloadFileDiff(request.Capability)),
            new Lr2SongDbSyncWorkflowOwner(runtime), _ => { references++; return playlist.ApplyRequiredLibraryReferencesAsync(); });
        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
        using LibraryFileMutationCapability capability = composition.OperationAdmission.CreateMutationCapability(lease);
        Task<FileDiffReloadWorkflowResult>? reload = null;
        Task<BMSTable>? registration = null;
        IDisposable? heldPlaylist = null;
        try
        {
            reload = reloadOwner.ReloadAsync(new FileDiffReloadRequest("parallel-daily", 101, capability));
            await Task.WhenAny(scanned.Task, reload);
            if (!scanned.Task.IsCompleted) { await reload; Assert.Fail("実FileDiffの固定scanへ到達しませんでした。"); }
            Assert.IsTrue(composition.OperationAdmission.IsActive);
            registration = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(table, false, "parallel-daily-registration");
            await registration;
            Assert.IsFalse(reload.IsCompleted, "非交差PはLのscan終端を待ちません。");
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist>().Count(row => row.name == "Independent"));
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist_entry>().Count(row => row.folder == "Daily Folder"));
                if (lr2Mode)
                {
                    string generated = Path.Combine(output, "Independent", "0001.lr2folder");
                    StringAssert.Contains(File.ReadAllText(generated, System.Text.Encoding.GetEncoding("shift_jis")), "#TITLE Daily Folder");
                    Assert.AreEqual("Daily Folder", db.Table<LR2SongDB.folder>().Single(row => row.path == generated).title);
                }
                else { Assert.IsFalse(Directory.Exists(output)); }
            }
            if (skipWhole) { Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out heldPlaylist)); }
            releaseScan.Set();
            FileDiffReloadWorkflowResult result = await reload;
            Assert.AreEqual(1, references);
            if (skipWhole)
            {
                Assert.AreEqual(Lr2SongDbSyncQueueStatus.SkippedCompeting, result.Lr2QueueResult.Status);
                Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
                Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);
                Assert.IsFalse(library.Lr2SongDbSyncRunning);
            }
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(chart, db.Table<LR2SongDB.song>().Single().path);
                if (lr2Mode) { Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.type == 1 && row.path.Contains("Music", StringComparison.Ordinal))); }
            }
            heldPlaylist?.Dispose(); lease.Dispose();
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable next)); next.Dispose();
            Assert.AreEqual(skipWhole ? 0 : library.Lr2SongDbSyncRequestedVersion, library.Lr2SongDbSyncCompletedVersion);
        }
        finally
        {
            releaseScan.Set();
            try { if (registration != null) { await registration; } }
            finally { try { if (reload != null) { await reload; } } finally { heldPlaylist?.Dispose(); lease.Dispose(); library.RequestShutdown("parallel-daily-cleanup"); } }
        }
    }

}
