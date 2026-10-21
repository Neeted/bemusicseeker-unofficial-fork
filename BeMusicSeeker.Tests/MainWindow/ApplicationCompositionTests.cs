using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class ApplicationCompositionTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
    /// <summary>再構成したL/Pにも同じ受付権限を接続し、外側Pの借用中は新規要求を拒否・同能力だけを許可し、別compositionの能力と欠損依存を拒否します。</summary>
    [TestMethod]
    public void CompositionCreatesPlaylistWithTypedLibraryBindings()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(CompositionCreatesPlaylistWithTypedLibraryBindings),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        using (var initialize = new LR2SongDBExtended(songDbPath))
        {
        }

        try
        {
            var composition = new ApplicationComposition(
                bmsLibraryOptionsProvider: () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings },
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var profile = new LibraryProfile(
                operationModeLR2DB: false,
                songDbPath: songDbPath,
                searchRoots: [tempDirectory],
                lr2ConfigProvider: () => null!,
                lr2ScoreDbPath: null,
                canWriteLr2Config: false,
                canOutputLr2Folders: false,
                canUseLr2Backup: false,
                canUseLr2IrScore: false,
                startupRequiredFileScanReason: "typed-playlist-binding-test");

            BMSLibrary library = composition.CreateBmsLibrary(profile);
            BMSPlaylist playlist = composition.CreateBmsPlaylist(profile, library);
            BmsPlaylistLibraryBindings bindings = playlist.LibraryBindings;

            Assert.AreSame(library, bindings.SourceLibrary);
            Assert.AreSame(
                library.Lr2PlaylistFolderSynchronization,
                bindings.Lr2PlaylistFolderSynchronization);

            string secondDbPath = Path.Combine(tempDirectory, "Second", "song.db");
            Directory.CreateDirectory(Path.GetDirectoryName(secondDbPath)!);
            using (var initialize = new LR2SongDBExtended(secondDbPath)) { }
            LibraryProfile secondProfile = StartupLibraryConstructionTestSupport.CreateProfile(tempDirectory, secondDbPath);
            BMSLibrary secondLibrary = composition.CreateBmsLibrary(secondProfile);
            BMSPlaylist secondPlaylist = composition.CreateBmsPlaylist(secondProfile, secondLibrary);
            Assert.AreSame(composition.PlaylistOperationAdmission, library.Lr2Synchronization.PlaylistOperationAdmission);
            Assert.AreSame(library.Lr2Synchronization.PlaylistOperationAdmission, secondLibrary.Lr2Synchronization.PlaylistOperationAdmission);
            Assert.AreNotSame(composition.OperationAdmission, composition.PlaylistOperationAdmission);
            Assert.AreNotSame(library.Lr2Synchronization, secondLibrary.Lr2Synchronization);
            Assert.AreNotSame(playlist, secondPlaylist);
            Assert.AreSame(secondLibrary, secondPlaylist.LibraryBindings.SourceLibrary);
            using (LibraryFileMutationLease outer = playlist.AcquirePlaylistMutationLease("reconstruction-outer"))
            using (LibraryFileMutationCapability authority = outer.CreateMutationCapability())
            {
                Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive);
                Assert.ThrowsException<InvalidOperationException>(() => secondPlaylist.AcquirePlaylistMutationLease("new-store-busy"));
                using (LibraryFileMutationLease borrowed = secondPlaylist.AcquirePlaylistMutationLease("same-operation", capability: authority))
                {
                    Assert.IsNotNull(borrowed);
                }
                Assert.IsTrue(composition.PlaylistOperationAdmission.IsActive, "借用終端は外側Pを解放しません。");
                var otherComposition = new ApplicationComposition(
                    bmsLibraryOptionsProvider: () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
                    settingsEditSession: new FakeSettingsEditSession { Values = testSettings },
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                BMSLibrary otherLibrary = otherComposition.CreateBmsLibrary(secondProfile);
                BMSPlaylist otherPlaylist = otherComposition.CreateBmsPlaylist(secondProfile, otherLibrary);
                try
                {
                    Assert.ThrowsException<InvalidOperationException>(() => otherPlaylist.AcquirePlaylistMutationLease("different-owner", capability: authority));
                }
                finally { otherPlaylist.RequestShutdown("isolated-cleanup"); otherLibrary.RequestShutdown("isolated-cleanup"); }
            }
            using (LibraryFileMutationLease next = secondPlaylist.AcquirePlaylistMutationLease("fresh-operation"))
            {
                Assert.IsNotNull(next);
            }
            secondPlaylist.RequestShutdown("reconstructed-cleanup");
            secondLibrary.RequestShutdown("reconstructed-cleanup");

            Assert.ThrowsException<ArgumentNullException>(
                () => new BmsPlaylistLibraryBindings(null!));
            Assert.ThrowsException<ArgumentNullException>(
                () => composition.CreateBmsPlaylist(null!, library));
            Assert.ThrowsException<ArgumentNullException>(
                () => composition.CreateBmsPlaylist(profile, null!));

            var missingProfile = new LibraryProfile(
                operationModeLR2DB: false,
                songDbPath: Path.Combine(tempDirectory, "missing", "song.db"),
                searchRoots: [tempDirectory],
                lr2ConfigProvider: () => null!,
                lr2ScoreDbPath: null,
                canWriteLr2Config: false,
                canOutputLr2Folders: false,
                canUseLr2Backup: false,
                canUseLr2IrScore: false,
                startupRequiredFileScanReason: "typed-playlist-binding-failure-test");
            Assert.ThrowsException<ArgumentException>(
                () => composition.CreateBmsPlaylist(missingProfile, library));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>構成済みworkspaceは保存前のdraftを正本へ追加せず、利用者出力設定と作成時刻を引き継ぎ、store不在は失敗として返します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceCreatesNewPlaylistDraftThroughConfiguredStore()
    {
        testSettings.PlaylistDefaultIgnoreFolderOutput =
            (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
            playlist.BMSTables = new ObservableCollection<BMSTable>();
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainChartListViewModel missingProviderMainChartList = composition.CreateMainChartListViewModel(action => action(), _ => { });
            PlaylistWorkspaceViewModel missingProviderWorkspace = composition.CreatePlaylistWorkspaceViewModel(
                action => action(),
                missingProviderMainChartList,
                () => null,
                () => [],
                _ => { },
                _ => { },
                PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
                PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                (_, _) => { },
                _ => { },
                (_, _) => { },
                _ => { },
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, playlistUrlInstallCompletionProvider: () => Task.CompletedTask);
            AttachImmediatePlaylistPresentationRouter(missingProviderWorkspace);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => missingProviderWorkspace.CreatePlaylistAsync());

            MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(action => action(), _ => { });
            PlaylistWorkspaceViewModel workspace = composition.CreatePlaylistWorkspaceViewModel(
                action => action(),
                mainChartList,
                () => playlist,
                () => playlist.BMSTables,
                _ => { },
                _ => { },
                PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
                PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                (_, _) => { },
                _ => { },
                (_, _) => { },
                _ => { },
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, playlistUrlInstallCompletionProvider: () => Task.CompletedTask);
            AttachImmediatePlaylistPresentationRouter(workspace);
            DateTime startedAt = DateTime.Now;
            BMSTable created = await workspace.CreatePlaylistAsync();
            DateTime completedAt = DateTime.Now;

            Assert.IsNotNull(created);
            Assert.AreEqual(0, playlist.BMSTables.Count, "新規draftは保存前に正本へ登録しません。");
            Assert.IsTrue(created.last_update >= startedAt && created.last_update <= completedAt);
            Assert.AreEqual(
                LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                created.ignore_folder_output);
            Assert.IsTrue(created.is_bmt_output);

            PlaylistPropertyDialogViewModel dialog =
                await workspace.CreatePlaylistPropertyDialogAsync();
            Assert.IsNotNull(dialog);
            Assert.AreEqual(testSettings.OperationModeLR2DB, dialog.OperationModeLR2DB);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            Assert.AreSame(dialog, workspace.ActivePropertyDialog);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            dialog.Dispose();
            workspace.ClosePropertyDialog(dialog);
            Assert.AreEqual(0, playlist.BMSTables.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>uBMplay選択と実行ファイルの存在を入力すると該当playerへそのpathを渡し、異なるplayer選択やpath取り違えを検出します。</summary>
    [TestMethod]
    public void CompositionCreatesTheConfiguredUbMplayPlayer()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "uBMplay.exe");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(executablePath, []);
        try
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var settings = new StartupSettingsSnapshot
            {
                UsePlayeruBMplay = true,
                uBMplayPath = executablePath
            };

            IBMSPlayer player = composition.CreateBmsPlayer(settings, null);

            Assert.IsInstanceOfType(player, typeof(uBMplay));
            Assert.AreEqual(executablePath, player.ExePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>BMIIDXView選択と実行ファイルの存在を入力すると該当playerへそのpathを渡し、異なるplayer選択やpath取り違えを検出します。</summary>
    [TestMethod]
    public void CompositionCreatesTheConfiguredBmiIdxViewPlayer()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "BMIIDXView2015_64.exe");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(executablePath, []);
        try
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var settings = new StartupSettingsSnapshot
            {
                UsePlayerBMIIDXView = true,
                BMIIDXViewPath = executablePath
            };

            IBMSPlayer player = composition.CreateBmsPlayer(settings, null);

            Assert.IsInstanceOfType(player, typeof(BMIIDXView2015));
            Assert.AreEqual(executablePath, player.ExePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>LR2body選択と実行ファイル・設定XMLを入力するとLR2playerへそのpathを渡し、player切替の接続漏れを検出します。</summary>
    [TestMethod]
    public void CompositionCreatesTheConfiguredLr2Player()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "LR2body.exe");
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        string configPath = Path.Combine(configDirectory, "config.xml");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllBytes(executablePath, []);
        File.WriteAllText(configPath, "<config><jukebox /></config>");
        try
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var settings = new StartupSettingsSnapshot
            {
                OperationModeLR2DB = false,
                UsePlayerLR2body = true,
                LR2bodyPath = executablePath
            };

            IBMSPlayer player = composition.CreateBmsPlayer(settings, () => new LR2Config(configPath));

            Assert.IsInstanceOfType(player, typeof(LR2body));
            Assert.AreEqual(executablePath, player.ExePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>単独DBモードでも選択されたLR2bodyを構成し、ライブラリモードだけを理由に既定playerへ切り替える誤りを検出します。</summary>
    [TestMethod]
    public void CompositionCreatesConfiguredLr2PlayerWhenLibraryOperationModeIsStandalone()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "LR2body.exe");
        string configPath = Path.Combine(root, "LR2files", "Config", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllBytes(executablePath, []);
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.UsePlayerLR2body = true;
                values.LR2RootPath = root;
                values.LR2ConfigXmlPath = configPath;
            });
        int defaultFactoryCalls = 0;
        try
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = values },
                defaultBmsPlayerFactory: () =>
                {
                    defaultFactoryCalls++;
                    throw new InvalidOperationException("standalone LR2body must not use the default player");
                },
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());

            IBMSPlayer player = composition.CreateBmsPlayerForSettings(StartupSettingsSnapshot.CreateCurrent(values));

            Assert.IsInstanceOfType(player, typeof(LR2body));
            Assert.AreEqual(executablePath, player.ExePath);
            Assert.AreEqual(0, defaultFactoryCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>選択したLR2の設定XML欠損を元のファイル例外で返し、既定playerへのフォールバックで失敗を隠しません。</summary>
    [TestMethod]
    public void CompositionPropagatesConfiguredLr2PlayerConfigFailureWithoutDefaultFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "LR2body.exe");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(executablePath, []);
        string missingConfigPath = Path.Combine(root, "missing-config.xml");
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.UsePlayerLR2body = true;
                values.LR2RootPath = root;
                values.LR2ConfigXmlPath = missingConfigPath;
            });
        int defaultFactoryCalls = 0;
        try
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = values },
                defaultBmsPlayerFactory: () =>
                {
                    defaultFactoryCalls++;
                    throw new InvalidOperationException("configured LR2 failure must not fall back");
                },
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog());

            Assert.ThrowsException<FileNotFoundException>(
                () => composition.CreateBmsPlayerForSettings(StartupSettingsSnapshot.CreateCurrent(values)));
            Assert.AreEqual(0, defaultFactoryCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>外部player選択を全て解除すると注入した既定factoryの結果を使い、設定による選択判断を迂回する誤りを検出します。</summary>
    [TestMethod]
    public void CompositionUsesInjectedDefaultWhenNoExternalPlayerIsSelected()
    {
        var expected = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => testSettings),
            new BassAudioPlaybackRuntime());
        var composition = new ApplicationComposition(
            defaultBmsPlayerFactory: () => expected,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        testSettings.UsePlayeruBMplay = false;
        testSettings.UsePlayerBMIIDXView = false;
        testSettings.UsePlayerLR2body = false;

        Assert.AreSame(
            expected,
            composition.CreateBmsPlayerForSettings(
                StartupSettingsSnapshot.CreateCurrent(testSettings)));
    }

    /// <summary>LR2bodyを選択しても実行ファイルがなければ構成失敗を伝え、欠損pathのplayerで成功しません。</summary>
    [TestMethod]
    public void CompositionRejectsConfiguredLr2PlayerWhenExecutableIsMissing()
    {
        testSettings.UsePlayeruBMplay = false;
        testSettings.UsePlayerBMIIDXView = false;
        testSettings.UsePlayerLR2body = true;
        string missingRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        testSettings.LR2RootPath = missingRoot;
        testSettings.LR2ConfigXmlPath = Path.Combine(missingRoot, "LR2files", "Config.xml");
        var composition = new ApplicationComposition(
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
            () => composition.CreateBmsPlayerForSettings(
                StartupSettingsSnapshot.CreateCurrent(testSettings)));

        Assert.AreEqual("Configured LR2 playback player could not be created.", exception.Message);
    }

    /// <summary>構成済みスコア登録を実譜面pathで開始し、確認・結果通知とhash照会・アップロード先を注入した窓口へ接続します。</summary>
    [TestMethod]
    public async Task CompositionScoreViewerWorkflowUsesInjectedDialogService()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string chartPath = Path.Combine(tempDirectory, "composition-score-viewer.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\n");
        try
        {
            Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.ShowScoreViewerRegisterConfirmMsg = true;
            });
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
            };
            var gateway = new CompositionScoreViewerGateway();
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = values },
                playlistWorkspaceDialogService: dialogs,
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                scoreViewerRegistrationGateway: gateway);

            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            ScoreViewerRegistrationResult result = await viewModel.ScoreViewerRegistration.RunAsync(
                [new ScoreViewerTarget("composition-hash", chartPath, "Composition chart")],
                openSingleViewerOnSuccess: false,
                logName: "composition-test");

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.UploadedCount);
            CollectionAssert.AreEqual(new[] { "composition-hash" }, gateway.StatusHashes.ToArray());
            CollectionAssert.AreEqual(new[] { chartPath }, gateway.UploadPaths.ToArray());
            Assert.IsNotNull(dialogs.LastConfirmationRequest);
            Assert.IsNotNull(dialogs.LastMessageRequest);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>構成済みworkspaceのBMT順変更を実DBへ保存し、表示中のデータ再構築延期と非表示時の即時反映を分けます。</summary>
    [TestMethod]
    public async Task ComposedPlaylistWorkspacePersistsSummaryBmtOrderAndRefreshesSummary()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
            playlist.BMSTables = new ObservableCollection<BMSTable>([first, second]);
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(action => action(), _ => { });
            PlaylistWorkspaceViewModel workspace = composition.CreatePlaylistWorkspaceViewModel(
                action => action(),
                mainChartList,
                () => playlist,
                () => playlist.BMSTables,
                _ => { },
                _ => { },
                PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
                PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                (_, _) => { },
                _ => { },
                (_, _) => { },
                _ => { },
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { },
                (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, playlistUrlInstallCompletionProvider: () => Task.CompletedTask);
            AttachPlaylistPresentationRouter(workspace, deferSummaryData: true);
            workspace.IsPlaylistSummaryMode = true;
            MainWindowChildComposition childComposition = composition.CreateMainWindowChildComposition(
                mainChartList,
                workspace,
                () => new InternalBMSAutoPlayerSoundOnly(
                    new SettingsPlayerSettingsGateway(() => testSettings),
                    new BassAudioPlaybackRuntime()),
                new ChartFileOperationSynchronizer(),
                _ => { },
                action => action(),
                _ => { },
                action =>
                {
                    action();
                    return true;
                },
                () => null!,
                new TestUiDialogService(),
                duplicateMaintenanceDialogService: new TestUiDialogService(),
                showDuplicateFileCheckConfirmProvider: () => false,
                duplicateMaintenanceLibraryProvider: () => null!,
            selectedChartMutationDialogService: new TestUiDialogService(),
            selectedChartMutationLibraryProvider: () => null!,
            selectedChartResourceHealthDialogService: new TestUiDialogService(),
            selectedChartResourceHealthLibraryProvider: () => null!,
            folderAutoRenameDialogService: new TestUiDialogService(),
            maintenanceRescanDialogService: new TestUiDialogService(),
            chartInfoParseFailureRemovalDialogService: new TestUiDialogService(),
                 chartInfoParseFailureRemovalLibraryProvider: () => null!,
                 lr2SongDbSyncWorkflow: CreateDisabledLr2SongDbSyncWorkflowOwner(),
                 rankingCacheDownloadWorkflow: CreateDisabledRankingCacheDownloadWorkflowOwner(),
                 startupProgressWorkflowOwner: TestStartupProgressOwnerFactory.Create());
            try
            {
                long generationBeforeVisibleRefresh = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
                await workspace.ApplyCurrentVisibleBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first }
                ]);

                Assert.AreEqual(2, first.bmt_sort);
                Assert.AreEqual(1, second.bmt_sort);
                Assert.AreEqual(generationBeforeVisibleRefresh + 1L, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
                Assert.AreEqual(
                    PlaylistSummaryDeferredRefreshKind.Data,
                    workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
                using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT bmt_sort FROM playlist WHERE playlist_id = ?;", 1));
                    Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT bmt_sort FROM playlist WHERE playlist_id = ?;", 2));
                }
                workspace.IsPlaylistSummaryMode = false;
                await workspace.ApplyCurrentVisibleBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ]);
                Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > generationBeforeVisibleRefresh + 1L);
                Assert.AreEqual(
                    PlaylistSummaryDeferredRefreshKind.None,
                    workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
            }
            finally
            {
                childComposition.RegularChartListOwner.Dispose();
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

    /// <summary>BMTドラッグ後の選択復元を対応するsummary適用まで遅らせ、無変更や世代更新では復元せず、次の対応適用で対象と現在行を復元します。</summary>
    [TestMethod]
    public async Task ComposedPlaylistWorkspaceRestoresBmtDropSelectionAfterMatchingSummaryApply()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var third = new BMSTable { playlist_id = 3, name = "Third", symbol = "T", bmt_sort = 3 };
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
            playlist.BMSTables = new ObservableCollection<BMSTable>([first, second, third]);
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            var restoreRequests = new List<PlaylistSummarySelectionRestoreRequest>();
            MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(action => action(), _ => { });
            PlaylistWorkspaceViewModel workspace = composition.CreatePlaylistWorkspaceViewModel(
                action => action(),
                mainChartList,
                () => playlist,
                () => playlist.BMSTables,
                _ => { },
                _ => { },
                PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
                PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                (_, _) => { },
                _ => { },
                (_, _) => { },
                _ => { },
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { },
                (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, playlistUrlInstallCompletionProvider: () => Task.CompletedTask);
            AttachPlaylistPresentationRouter(workspace, deferSummaryData: true);
            workspace.PlaylistSummarySelectionRestoreRequested += restoreRequests.Add;
            MainWindowChildComposition childComposition = composition.CreateMainWindowChildComposition(
                mainChartList,
                workspace,
                () => new InternalBMSAutoPlayerSoundOnly(
                    new SettingsPlayerSettingsGateway(() => testSettings),
                    new BassAudioPlaybackRuntime()),
                new ChartFileOperationSynchronizer(),
                _ => { },
                action => action(),
                _ => { },
                action =>
                {
                    action();
                    return true;
                },
                () => null!,
                new TestUiDialogService(),
                duplicateMaintenanceDialogService: new TestUiDialogService(),
                showDuplicateFileCheckConfirmProvider: () => false,
                duplicateMaintenanceLibraryProvider: () => null!,
            selectedChartMutationDialogService: new TestUiDialogService(),
            selectedChartMutationLibraryProvider: () => null!,
            selectedChartResourceHealthDialogService: new TestUiDialogService(),
            selectedChartResourceHealthLibraryProvider: () => null!,
            folderAutoRenameDialogService: new TestUiDialogService(),
            maintenanceRescanDialogService: new TestUiDialogService(),
            chartInfoParseFailureRemovalDialogService: new TestUiDialogService(),
                 chartInfoParseFailureRemovalLibraryProvider: () => null!,
                 lr2SongDbSyncWorkflow: CreateDisabledLr2SongDbSyncWorkflowOwner(),
                 rankingCacheDownloadWorkflow: CreateDisabledRankingCacheDownloadWorkflowOwner(),
                 startupProgressWorkflowOwner: TestStartupProgressOwnerFactory.Create());
            try
            {
                workspace.IsPlaylistSummaryMode = true;

                await workspace.DropSummaryRowsInBmtOrderAsync(
                    [
                        new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                        new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                        new PlaylistSummaryRow { PlaylistId = third.playlist_id, TableRef = third }
                    ],
                    [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                    visibleInsertIndex: 0,
                    currentPlaylistId: second.playlist_id);

                Assert.AreEqual(0, restoreRequests.Count);
                Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
                long dataRebuildGeneration = buildRequest.Generation;
                Assert.IsTrue(dataRebuildGeneration > 0L);

                long presentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
                long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
                Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                {
                    Rows = new ObservableCollection<PlaylistSummaryRow>(),
                    PresentationGeneration = presentationGeneration,
                    DataRebuildGeneration = dataRebuildGeneration,
                    CacheGeneration = cacheGeneration
                }));

                Assert.AreEqual(1, restoreRequests.Count);
                PlaylistSummarySelectionRestoreRequest restore = restoreRequests[0];
                CollectionAssert.AreEquivalent(new[] { second.playlist_id }, restore.PlaylistIds.ToArray());
                Assert.AreEqual(second.playlist_id, restore.CurrentPlaylistId);
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);

                long previousDataRebuildGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
                await workspace.DropSummaryRowsInBmtOrderAsync(
                    [
                        new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                        new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                        new PlaylistSummaryRow { PlaylistId = third.playlist_id, TableRef = third }
                    ],
                    [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                    visibleInsertIndex: 0,
                    currentPlaylistId: second.playlist_id);
                Assert.AreEqual(previousDataRebuildGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
                Assert.AreEqual(1, restoreRequests.Count);

                await workspace.DropSummaryRowsInBmtOrderAsync(
                    [
                        new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                        new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                        new PlaylistSummaryRow { PlaylistId = third.playlist_id, TableRef = third }
                    ],
                    [new PlaylistSummaryRow { PlaylistId = third.playlist_id, TableRef = third }],
                    visibleInsertIndex: 0,
                    currentPlaylistId: third.playlist_id);
                workspace.RequestPlaylistSummaryDataRefresh();
                Assert.AreEqual(1, restoreRequests.Count);

                await workspace.DropSummaryRowsInBmtOrderAsync(
                    [
                        new PlaylistSummaryRow { PlaylistId = third.playlist_id, TableRef = third },
                        new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                        new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first }
                    ],
                    [new PlaylistSummaryRow { PlaylistId = third.playlist_id, TableRef = third }],
                    visibleInsertIndex: 2,
                    currentPlaylistId: third.playlist_id);
                Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest synchronousBuild));
                PlaylistSummarySelectionRestoreRequest synchronousRestore;
                try
                {
                    Assert.IsTrue(synchronousBuild.Generation > 0L);
                    Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                    {
                        Rows = new ObservableCollection<PlaylistSummaryRow>(),
                        PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                        DataRebuildGeneration = synchronousBuild.Generation,
                        CacheGeneration = synchronousBuild.CacheGeneration
                    }));
                    Assert.AreEqual(2, restoreRequests.Count);
                    synchronousRestore = restoreRequests[1];
                }
                finally
                {
                    workspace.CompletePlaylistSummaryDataBuild(synchronousBuild);
                }
                Assert.IsNotNull(synchronousRestore);
                CollectionAssert.AreEquivalent(new[] { third.playlist_id }, synchronousRestore.PlaylistIds.ToArray());
                Assert.AreEqual(third.playlist_id, synchronousRestore.CurrentPlaylistId);
            }
            finally
            {
                childComposition.RegularChartListOwner.Dispose();
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

    /// <summary>重複選択とnull・false・trueの出力設定から実変更だけを保存し、必要出力の終端後に完了して再要求の無変更では出力を繰り返しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceBmtOutputTogglePersistsDistinctChangedHeadersAndRequestsSummaryRefresh()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 11, name = "First", is_bmt_output = null };
            var second = new BMSTable { playlist_id = 12, name = "Second", is_bmt_output = false };
            var third = new BMSTable { playlist_id = 13, name = "Third", is_bmt_output = true };
            string bmtPath = Path.Combine(tempDirectory, "beatoraja", "table.json");
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = bmtPath
                },
                () => new CustomFolderOutputSettingsSnapshot())
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second, third])
            };
            playlist.CommitBMSTableHeadersToDB([first, second, third]);
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables),
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, playlistUrlInstallCompletionProvider: () => Task.CompletedTask);
            AttachImmediatePlaylistPresentationRouter(workspace);
            int completedOutputCount = 0;
            playlist.BmtOutput.RequestProgressReporter = (_, running) =>
            {
                if (!running) { completedOutputCount++; }
            };

            long generationBeforeFirstAction = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.ApplyPlaylistSummaryCellActionAsync(
                [
                    new PlaylistSummaryRow { TableRef = first },
                    new PlaylistSummaryRow { TableRef = second },
                    new PlaylistSummaryRow { TableRef = second },
                    new PlaylistSummaryRow { TableRef = third }
                ],
                "IsBmtOutput",
                value: true).GetAwaiter().GetResult();

            Assert.IsNull(first.is_bmt_output);
            Assert.AreEqual(true, second.is_bmt_output);
            Assert.AreEqual(true, third.is_bmt_output);
            Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > generationBeforeFirstAction);
            Assert.AreEqual(1, completedOutputCount, "必要出力の実終端後に変更Taskを完了します。");
            long generationBeforeNoOp = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = third }],
                "IsBmtOutput",
                value: true).GetAwaiter().GetResult();
            Assert.AreEqual(generationBeforeNoOp, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            Assert.AreEqual(1, completedOutputCount, "変更のない再要求は必要出力を再実行しません。");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.playlist persistedFirst = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == first.playlist_id);
            LR2SongDBExtended.playlist persistedSecond = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == second.playlist_id);
            LR2SongDBExtended.playlist persistedThird = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == third.playlist_id);
            Assert.IsNull(persistedFirst.is_bmt_output);
            Assert.AreEqual(true, persistedSecond.is_bmt_output);
            Assert.AreEqual(true, persistedThird.is_bmt_output);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実取り込みの推定を停止しても確定済み保留行とツリーを公開し、先行推定の導入先を表示します。後続推定の終端までは共通受付と実Taskを保持します。</summary>
    [TestMethod]
    // 標準compositionの共有Resource通知購読から実処理の終端・Disposeまでを非並列で所有します。
    [DoNotParallelize]
    public void MainWindowCompositionShowsCommittedPendingPackagesDuringEstimation()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "Library", "Song");
        Directory.CreateDirectory(installed);
        string installedChart = Path.Combine(installed, "chart.bms");
        const string chart = "#TITLE Song\r\n#ARTIST Artist\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01\r\n";
        File.WriteAllText(installedChart, chart, Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(installed, "sound.wav"), [1]);
        string[] sources = [Path.Combine(root, "Input0"), Path.Combine(root, "Input1")];
        for (int index = 0; index < sources.Length; index++)
        {
            Directory.CreateDirectory(sources[index]);
            File.WriteAllText(Path.Combine(sources[index], "diff.bms"), chart + "#PLAYLEVEL " + (10 + index) + "\r\n", Encoding.ASCII);
        }
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var background = new ConcurrentQueue<Func<Task>>();
        var observer = new PendingCompositionEstimationObserver();
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
                {
                    values.OperationModeLR2DB = false;
                    values.StartupSelectInstallPending = false;
                });
                MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(settings, new FileDbReportRecordingDialogs());
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                var options = new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    ScanBmsFilesOnStartup = true,
                    KeepInstallablePackagesPending = true,
                    PendingInstallEstimateMaxParallelPackages = 2
                };
                var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => options,
                    CapturedChartFileScanner.FromFixture([installedChart],
                        new Dictionary<string, IEnumerable<string>> { [installed] = ["sound.wav"] }, [installed]),
                    observer, composition.OperationAdmission, composition.PlaylistOperationAdmission)
                {
                    SearchTargets = [Path.Combine(root, "Library")],
                    StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
                };
                var rowsApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var treeApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                System.ComponentModel.PropertyChangedEventHandler rowsHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(MainChartListViewModel.Rows)
                        && viewModel.MainChartList.Rows.Count == 2) { rowsApplied.TrySetResult(); }
                };
                System.ComponentModel.PropertyChangedEventHandler treeHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(InstallTreeViewModel.ChartPackagesPending)
                        && viewModel.InstallTree.ChartPackagesPending.Count == 2) { treeApplied.TrySetResult(); }
                };
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.Run(() => library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup)), "pending-composition.initialize");
                    while (background.TryDequeue(out Func<Task>? work))
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(work(), "pending-composition.background");
                    }
                    ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                    viewModel.InstallTree.AttachLibrary(library);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.RegularChartList.NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected), "pending-composition.navigate");
                    viewModel.MainChartList.PropertyChanged += rowsHandler;
                    viewModel.InstallTree.PropertyChanged += treeHandler;
                    Assert.IsTrue(viewModel.PackageInstallWorkflow.Enqueue(sources));
                    Task idle = viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(Task.WhenAll(observer.Evaluated.Select(signal => signal.Task)), idle,
                            "pending-composition.evaluated"), "pending-composition.evaluated");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(Task.WhenAll(rowsApplied.Task, treeApplied.Task), idle,
                            "pending-composition.presentation"), "pending-composition.presentation");
                    using (LR2SongDBExtended connection = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual(2, connection.Table<LR2SongDBExtended.install>().Count());
                    }
                    Assert.IsFalse(idle.IsCompleted);
                    Assert.IsTrue(composition.OperationAdmission.IsActive);
                    Assert.IsFalse(viewModel.PackageInstallWorkflow.Enqueue(sources));
                    LibraryChartRow first = viewModel.MainChartList.Rows.Cast<LibraryChartRow>()
                        .Single(row => row.path == Path.Combine(sources[0], "diff.bms"));
                    Assert.AreEqual(string.Empty, first.instl_dst);
                    var destinationChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    System.ComponentModel.PropertyChangedEventHandler destinationHandler = (_, _) =>
                    {
                        if (first.instl_dst == installed) { destinationChanged.TrySetResult(); }
                    };
                    first.PropertyChanged += destinationHandler;
                    try
                    {
                        observer.Release[0].TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            TestUiDispatcherHost.AwaitNotificationAsync(Task.WhenAll(observer.FirstApplied.Task, destinationChanged.Task), idle,
                                "pending-composition.destination"), "pending-composition.destination");
                        Assert.AreEqual(installed, first.instl_dst);
                        Assert.IsFalse(idle.IsCompleted);
                        Assert.IsTrue(composition.OperationAdmission.IsActive);
                    }
                    finally { first.PropertyChanged -= destinationHandler; }
                    observer.Release[1].TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "pending-composition.idle");
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                }
                finally
                {
                    foreach (TaskCompletionSource signal in observer.Release) { signal.TrySetResult(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "pending-composition.cleanup-install");
                    viewModel.MainChartList.PropertyChanged -= rowsHandler;
                    viewModel.InstallTree.PropertyChanged -= treeHandler;
                    viewModel.InstallTree.DetachLibrary();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.RegularChartList.StopAsync(), "pending-composition.cleanup-rows");
                    viewModel.SettingDialog.Dispose();
                    library.RequestShutdown("pending-composition-cleanup");
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class PendingCompositionEstimationObserver : IInstallEstimationExecutionObserver
    {
        internal readonly TaskCompletionSource[] Evaluated = [NewSignal(), NewSignal()];
        internal readonly TaskCompletionSource[] Release = [NewSignal(), NewSignal()];
        internal readonly TaskCompletionSource FirstApplied = NewSignal();

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation) => new EvaluationScope();
        public void ObserveEvaluationCompleted(InstallEstimationAppliedObservation observation)
        {
            Evaluated[observation.OrderIndex].TrySetResult();
            Release[observation.OrderIndex].Task.GetAwaiter().GetResult();
        }
        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 0) { FirstApplied.TrySetResult(); }
        }
        public void ObserveDispatchStopped() { }
        public void ObserveProgress(InstallEstimationProgressObservation observation) { }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private sealed class EvaluationScope : IDisposable { public void Dispose() { } }
    }

    /// <summary>実構成の同時サマリー要求を一回のデータ構築へまとめ、導入記録操作が別一覧を抑制中でもUIへ採用します。</summary>
    // trueは実記録削除の開始通知でworkerを保持し、shellのLibraryMainView/InstallTree抑制中を検証します。
    [TestMethod]
    // 標準compositionの共有Resource通知購読から全ownerの終了・Disposeまでを非並列で所有します。
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void MainWindowCompositionRoutesPlaylistSummaryRefreshThroughShellArbiter(bool suppressPackageViews)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        try
        {
            new BmsLibraryDbGateway(songDb).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(songDb);
            TestUiDispatcherHost.Invoke(() =>
            {
                var scheduler = new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                var composition = new ApplicationComposition(
                    settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: scheduler,
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
                PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<PackageCatalogMutationPhaseEventArgs> phaseHandler = (_, args) =>
                {
                    if (args.Phase == PackageCatalogMutationPhase.RefreshSuppressionStarted)
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    }
                };
                int appliedCount = 0;
                int appliedThreadId = 0;
                System.ComponentModel.PropertyChangedEventHandler presentationHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
                    {
                        appliedCount++;
                        appliedThreadId = Environment.CurrentManagedThreadId;
                    }
                };
                Task<PackageCatalogMutationResult>? removal = null;
                try
                {
                    TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDb, testSettings, viewModel);
                    TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDb, testSettings, library: library);
                    var expectedTable = new BMSTable { playlist_id = 17, name = "Summary fixture", symbol = "S" };
                    expectedTable.entries = [PlaylistSummaryAggregationTestSupport.CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)];
                    playlist.BMSTables = new ObservableCollection<BMSTable>([expectedTable]);
                    var profile = new LibraryProfile(false, songDb, [root], null, null, false, false, false, false);
                    IStartupLibraryApplicationPort applicationPort = viewModel;
                    applicationPort.AttachStartupLibrary(library);
                    applicationPort.AttachStartupServices(new StartupLibraryServices(profile, library, playlist));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        scheduler.InvokeAsync(() => { }, UiSchedulePriority.ContextIdle), "summary-composition.attach");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        workspace.WaitForPlaylistSummaryDataBuildIdleAsync(), "summary-composition.attach-summary");
                    workspace.IsPlaylistSummaryMode = true;
                    long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
                    Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache([], dataGeneration));
                    var package = ChartPackage.FromChartEntries([]);
                    package.path = Path.Combine(root, "Installed");
                    library.ChartPackagesInstalled.Add(package);
                    workspace.PropertyChanged += presentationHandler;
                    viewModel.PackageCatalog.MutationPhasePublished += phaseHandler;
                    if (suppressPackageViews)
                    {
                        removal = viewModel.PackageCatalog.RemovePackageAsync(PackageCatalogSection.Installed, package);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, removal, "summary-composition.suppression"),
                            "summary-composition.suppression");
                    }
                    workspace.RequestPlaylistSummaryPresentationRefresh();
                    long expectedGeneration = workspace.RequestPlaylistSummaryDataRefresh("summary-composition.data", rebuildAsync: false);
                    workspace.RequestPlaylistSummaryPresentationRefresh();
                    Assert.AreEqual(0, appliedCount, "同じUI turnの要求を同期適用しません。");
                    Assert.IsTrue(workspace.HasDeferredPlaylistSummaryRefresh());
                    // 小さい同期buildを含む予約を実Dispatcherで終え、通知が無い退行も待機停止せず表明します。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        scheduler.InvokeAsync(() => { }, UiSchedulePriority.ContextIdle), "summary-composition.dispatch");
                    Assert.AreEqual(1, appliedCount, "Dataを優先し、前後のPresentation要求を別々に適用しません。");
                    Assert.AreEqual(TestUiDispatcherHost.Dispatcher.Thread.ManagedThreadId, appliedThreadId);
                    Assert.AreEqual(expectedGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
                    Assert.IsFalse(workspace.HasDeferredPlaylistSummaryRefresh());
                    Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
                    PlaylistSummaryRow actualRow = workspace.PlaylistSummaryView.Cast<PlaylistSummaryRow>().Single();
                    Assert.AreEqual(expectedTable.playlist_id, actualRow.PlaylistId);
                    Assert.AreEqual(expectedTable.name, actualRow.Name);
                    Assert.AreSame(expectedTable, actualRow.TableRef);
                    Assert.AreEqual(1, actualRow.TotalCharts);
                    Assert.AreEqual(0, actualRow.OwnedCharts);
                    Assert.AreEqual(1, actualRow.MissingCharts);
                    if (removal != null)
                    {
                        Assert.IsFalse(removal.IsCompleted);
                        Assert.IsTrue(composition.OperationAdmission.IsActive);
                        Assert.IsTrue(viewModel.IsLibraryOperationInProgress);
                        release.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(removal, "summary-composition.removal");
                        Assert.IsTrue(removal.GetAwaiter().GetResult().Succeeded);
                        Assert.IsFalse(library.ChartPackagesInstalled.Contains(package));
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                        Assert.IsFalse(viewModel.IsLibraryOperationInProgress);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            scheduler.InvokeAsync(() => { }, UiSchedulePriority.ContextIdle), "summary-composition.flush");
                        Assert.AreEqual(expectedGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration,
                            "別一覧の抑制終了だけではサマリーデータを再構築しません。");
                    }
                }
                finally
                {
                    release.TrySetResult();
                    if (removal != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(removal, "summary-composition.cleanup-removal"); }
                    workspace.PropertyChanged -= presentationHandler;
                    viewModel.PackageCatalog.MutationPhasePublished -= phaseHandler;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "summary-composition.cleanup");
                    viewModel.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>workerからサマリー選択を要求すると実UI Dispatcherで一回だけ状態通知し、要求元workerでUI状態を書き換えません。</summary>
    [TestMethod]
    public void PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
            long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
            Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache([], dataGeneration));
            int uiThreadId = Thread.CurrentThread.ManagedThreadId;
            int workerThreadId = 0;
            int propertyChangedThreadId = 0;
            int propertyChangedCount = 0;
            var propertyChanged = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler propertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode))
                {
                    propertyChangedThreadId = Thread.CurrentThread.ManagedThreadId;
                    propertyChangedCount++;
                    propertyChanged.TrySetResult(true);
                }
            };
            workspace.PropertyChanged += propertyChangedHandler;
            try
            {
                Task worker = Task.Factory.StartNew(
                    () =>
                    {
                        workerThreadId = Thread.CurrentThread.ManagedThreadId;
                        workspace.RequestSummarySelection();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    worker,
                    nameof(PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher) + ".worker");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    propertyChanged.Task,
                    nameof(PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher) + ".property-changed");

                Assert.IsTrue(workspace.IsPlaylistSummaryMode);
                Assert.IsTrue(workspace.IsPlaylistSummaryModeRequested);
                Assert.AreNotEqual(uiThreadId, workerThreadId);
                Assert.AreEqual(uiThreadId, propertyChangedThreadId);
                Assert.AreEqual(1, propertyChangedCount);
            }
            finally
            {
                workspace.PropertyChanged -= propertyChangedHandler;
            }
        });
    }

    /// <summary>起動snapshotは注入した編集sessionのモードとLR2ルートを取得し、グローバル設定を参照する接続漏れを検出します。</summary>
    [TestMethod]
    public void CompositionDefaultStartupSettingsProviderUsesInjectedEditSessionValues()
    {
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.LR2RootPath = "injected-lr2-root";
            });
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        StartupSettingsSnapshot snapshot = composition.StartupSettingsProvider();

        Assert.IsFalse(snapshot.OperationModeLR2DB);
        Assert.AreEqual("injected-lr2-root", snapshot.LR2RootPath);
    }

    /// <summary>各workflowへ編集sessionの導入・出力・URL設定を配送し、後続snapshotでも設定変更を捕捉することで初回値の固定を防ぎます。</summary>
    [TestMethod]
    public void CompositionDefaultWorkflowSnapshotProvidersUseInjectedEditSessionValues()
    {
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.PendingInstallEstimateMaxParallelPackages = 11;
                values.LR2CustomFolderAdditionalOutputBaseDirs = "[\"session-output\"]";
                values.EnablePlaylistUrlCompletion = true;
                values.EnableBeatorajaBmtOutput = true;
                values.LR2CustomFolderOutputBaseDir = "session-output-base";
                values.ShowDiffBMSInstallConfirmMsg = true;
                values.ShowNewPackageInstallConfirmMsg = false;
                values.DeletePendingPackageSourceAfterInstall = true;
            });
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        BmsLibraryOptionsSnapshot libraryOptions = composition.BmsLibraryOptionsProvider();
        PlaylistUrlCompletionOptionsSnapshot playlistOptions = composition.PlaylistUrlCompletionOptionsProvider();
        BeatorajaBmtOptionsSnapshot beatorajaOptions = composition.BeatorajaBmtOptionsProvider();
        CustomFolderOutputSettingsSnapshot customFolderOptions = composition.CustomFolderOutputSettingsProvider();
        InstallDestinationWorkflowSettingsSnapshot installDestinationOptions =
            composition.InstallDestinationSettingsProvider();

        Assert.AreEqual(11, libraryOptions.PendingInstallEstimateMaxParallelPackages);
        Assert.AreEqual(1, libraryOptions.LR2CustomFolderAdditionalOutputBaseDirs.Count);
        Assert.AreEqual(
            Path.GetFullPath("session-output"),
            libraryOptions.LR2CustomFolderAdditionalOutputBaseDirs[0]);
        Assert.IsTrue(playlistOptions.EnablePlaylistUrlCompletion);
        Assert.IsTrue(beatorajaOptions.EnableBeatorajaBmtOutput);
        Assert.AreEqual("session-output-base", customFolderOptions.LR2CustomFolderOutputBaseDir);
        Assert.IsTrue(installDestinationOptions.ShowManualInstallConfirmation);
        Assert.IsFalse(installDestinationOptions.ShowNewPackageInstallConfirmation);
        Assert.IsFalse(libraryOptions.ShowNewPackageInstallConfirmMsg);
        values.ShowNewPackageInstallConfirmMsg = true;
        Assert.IsTrue(composition.InstallDestinationSettingsProvider().ShowNewPackageInstallConfirmation);
        Assert.IsTrue(composition.BmsLibraryOptionsProvider().ShowNewPackageInstallConfirmMsg);
        Assert.IsTrue(installDestinationOptions.DeletePendingPackageSourceAfterInstall);

        values.PendingInstallEstimateMaxParallelPackages = 13;
        Assert.AreEqual(13, composition.BmsLibraryOptionsProvider().PendingInstallEstimateMaxParallelPackages);
    }

    /// <summary>検索履歴と履歴表示設定のstoreを注入sessionへ双方向接続し、別の設定主体への保存を検出します。</summary>
    [TestMethod]
    public void CompositionDefaultSerializedSettingsStoresUseInjectedEditSessionValues()
    {
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.KeywordSearchHistory = "keyword-history";
                values.PlaylistSummaryKeywordSearchHistory = "playlist-keyword-history";
                values.PlayHistorySelectedDisplayTargetIdentity = "set:session";
                values.PlayHistoryDisplayTargetSetsJson = "display-target-sets";
            });
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreEqual("keyword-history", composition.KeywordSearchHistorySettingsStore.KeywordSearchHistory);
        Assert.AreEqual("playlist-keyword-history", composition.KeywordSearchHistorySettingsStore.PlaylistSummaryKeywordSearchHistory);
        Assert.AreEqual("set:session", composition.PlayHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity);
        Assert.AreEqual("display-target-sets", composition.PlayHistoryDisplaySettingsStore.DisplayTargetSetsJson);

        composition.KeywordSearchHistorySettingsStore.KeywordSearchHistory = "updated-keyword-history";
        composition.PlayHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = "all";

        Assert.AreEqual("updated-keyword-history", values.KeywordSearchHistory);
        Assert.AreEqual("all", values.PlayHistorySelectedDisplayTargetIdentity);
    }


    /// <summary>グローバルと注入sessionの追加出力先を異なる値にすると設定画面は注入先だけを表示し、グローバル参照の混入を検出します。</summary>
    [TestMethod]
    // グローバル設定の一時差替えと復元を一組にし、他の設定参照と並列化しません。
    [DoNotParallelize]
    public void SettingDialogAdditionalOutputPathsUseInjectedSessionInsteadOfGlobalSettings()
    {
        string previousGlobalPaths = BeMusicSeeker.Properties.Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string sessionPath = Path.GetFullPath("session-additional-output");
        string globalPath = Path.GetFullPath("global-additional-output");
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([sessionPath]);
            });
        BeMusicSeeker.Properties.Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
            CustomFolderOutputBaseRegistry.SerializeBaseDirectories([globalPath]);
        try
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = values },
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

            CollectionAssert.Contains(viewModel.SettingDialog.CustomFolderAdditionalOutputBaseDirList, sessionPath);
            CollectionAssert.DoesNotContain(viewModel.SettingDialog.CustomFolderAdditionalOutputBaseDirList, globalPath);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousGlobalPaths;
        }
    }

    /// <summary>構成済みplaylistの出力先候補へsessionの基本・追加出力先を渡し、設定画面以外の候補生成でも設定接続を維持します。</summary>
    [TestMethod]
    public void MainWindowPlaylistOutputOptionsUseCompositionCustomFolderSettings()
    {
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.LR2CustomFolderOutputBaseDir = "session-output-base";
                values.LR2CustomFolderAdditionalOutputBaseDirs = "[\"session-additional\"]";
            });
        var composition = new ApplicationComposition(
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        IReadOnlyList<PlaylistCustomFolderOutputBaseOption> options =
            viewModel.PlaylistWorkspace.CreatePlaylistCustomFolderOutputBaseOptionsForCurrentSettings();

        Assert.IsTrue(options.Any(option => option.Label == "session-output-base"));
        Assert.IsTrue(options.Any(option => option.Label == "session-additional"));
    }

    /// <summary>通常譜面とサマリーの検索履歴確定を注入storeの別scopeへ保存し、片側未接続やscope混同を検出します。</summary>
    [TestMethod]
    public void MainWindowKeywordSearchHistoryUsesCompositionSettingsStore()
    {
        var store = new FakeKeywordSearchHistorySettingsStore
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["old"]),
            PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["summary-old"])
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            keywordSearchHistorySettingsStore: store,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        Assert.IsTrue(viewModel.ChartFilters.KeywordSearchAssistanceOwner.SavedQueryOwner.TryCommitHistory("new").Succeeded);
        Assert.IsTrue(viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner.SavedQueryOwner.TryCommitHistory("summary-new").Succeeded);

        Assert.AreEqual("new", KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory)[0]);
        Assert.AreEqual("summary-new", KeywordSearchHistoryStore.Deserialize(store.PlaylistSummaryKeywordSearchHistory)[0]);
    }

    /// <summary>通常譜面とサマリーのお気に入り追加を編集sessionの別設定へ保存し、既定store接続の漏れを検出します。</summary>
    [TestMethod]
    public void MainWindowKeywordSearchFavoritesDefaultUsesCompositionSettingsSession()
    {
        Settings values = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        KeywordSearchSavedQueryMutationResult normalResult =
            viewModel.ChartFilters.KeywordSearchAssistanceOwner.TryAddFavorite("title:default");
        KeywordSearchSavedQueryMutationResult summaryResult =
            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner.TryAddFavorite("output:default");

        Assert.IsTrue(normalResult.Succeeded);
        Assert.IsTrue(summaryResult.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "title:default" },
            KeywordSearchFavoritesStore.Deserialize(values.KeywordSearchFavorites).ToArray());
        CollectionAssert.AreEqual(
            new[] { "output:default" },
            KeywordSearchFavoritesStore.Deserialize(values.PlaylistSummaryKeywordSearchFavorites).ToArray());
    }

    /// <summary>明示注入したお気に入りstoreへ通常譜面とサマリーの追加を別scopeで保存し、既定storeへの誤配送を検出します。</summary>
    [TestMethod]
    public void MainWindowKeywordSearchFavoritesUseCompositionSettingsStore()
    {
        var historyStore = new FakeKeywordSearchHistorySettingsStore();
        var favoriteStore = new FakeKeywordSearchFavoritesSettingsStore();
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            keywordSearchHistorySettingsStore: historyStore,
            keywordSearchFavoritesSettingsStore: favoriteStore,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        Assert.AreSame(favoriteStore, composition.KeywordSearchFavoritesSettingsStore);
        KeywordSearchSavedQueryMutationResult normalResult =
            viewModel.ChartFilters.KeywordSearchAssistanceOwner.TryAddFavorite("title:normal");
        KeywordSearchSavedQueryMutationResult summaryResult =
            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner.TryAddFavorite("output:summary");

        Assert.IsTrue(normalResult.Succeeded);
        Assert.IsTrue(summaryResult.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "title:normal" },
            KeywordSearchFavoritesStore.Deserialize(favoriteStore.KeywordSearchFavorites).ToArray());
        CollectionAssert.AreEqual(
            new[] { "output:summary" },
            KeywordSearchFavoritesStore.Deserialize(favoriteStore.PlaylistSummaryKeywordSearchFavorites).ToArray());
    }

    /// <summary>サマリー補助はサマリー履歴と専用output候補を表示し、通常譜面だけのmemo条件を警告してサマリーscopeへ履歴を保存します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceOwnsSummaryKeywordSearchAssistanceAndHistoryScope()
    {
        var store = new FakeKeywordSearchHistorySettingsStore
        {
            PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["summary-old"])
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            keywordSearchHistorySettingsStore: store,
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        KeywordSearchAssistanceOwner assistance = workspace.PlaylistSummaryKeywordSearchAssistanceOwner;
        Assert.IsNotNull(assistance);
        KeywordSearchPresentationState state = assistance.Focus(string.Empty, 0);
        KeywordSearchPresentationSection history = state.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.History);
        Assert.AreEqual("summary-old", history.Items.Single().Query);
        Assert.IsTrue(state.Sections
            .Single(section => section.Kind == KeywordSearchPresentationSectionKind.Fields)
            .Items.Any(item => item.DisplayText == "output:"));
        Assert.IsFalse(state.Sections.Any(section => section.Kind == KeywordSearchPresentationSectionKind.Values));

        workspace.PlaylistSummaryKeywordFilter = "memo:alpha";
        StringAssert.Contains(workspace.PlaylistSummaryKeywordSearchWarningText, "memo");

        Assert.IsTrue(workspace.PlaylistSummaryKeywordSearchAssistanceOwner.SavedQueryOwner.TryCommitHistory("summary-new").Succeeded);
        Assert.AreEqual("summary-new", KeywordSearchHistoryStore.Deserialize(store.PlaylistSummaryKeywordSearchHistory)[0]);
    }

    private sealed class FakeKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;
    }

    private sealed class FakeKeywordSearchFavoritesSettingsStore : IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }

    /// <summary>構成時に保存済み履歴表示対象を復元し、画面側の対象変更を同じstoreへ返して選択設定の接続漏れを検出します。</summary>
    [TestMethod]
    public void MainWindowPlayHistoryDisplaySettingsUseCompositionStore()
    {
        var store = new FakePlayHistoryDisplaySettingsStore
        {
            SelectedDisplayTargetIdentity = "set:CUSTOM",
            DisplayTargetSetsJson = PlayHistoryDisplayTargetSetStore.Serialize(
            [
                new PlayHistoryDisplayTargetSet
                {
                    Name = "custom",
                    Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 123 }]
                }
            ])
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            playHistoryDisplaySettingsStore: store,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        Assert.AreEqual("set:CUSTOM", viewModel.PlayHistory.SelectedDisplayTargetIdentity);
        viewModel.PlayHistory.SelectedDisplayTargetIdentity = "all";
        Assert.AreEqual("all", store.SelectedDisplayTargetIdentity);


    }

    private sealed class FakePlayHistoryDisplaySettingsStore : IPlayHistoryDisplaySettingsStore
    {
        public string SelectedDisplayTargetIdentity { get; set; } = string.Empty;

        public string DisplayTargetSetsJson { get; set; } = string.Empty;
    }

    private static Lr2SongDbSyncWorkflowOwner CreateDisabledLr2SongDbSyncWorkflowOwner()
    {
        return new Lr2SongDbSyncWorkflowOwner(
            new BmsLr2SongDbSyncWorkflowRuntime(
                () => null!,
                () => null!,
                () => false));
    }

    private static RankingCacheDownloadWorkflowOwner CreateDisabledRankingCacheDownloadWorkflowOwner()
    {
        return new RankingCacheDownloadWorkflowOwner(
            new BmsRankingCacheDownloadRuntime(() => null!),
            new TestUiDialogService());
    }

    private static void AttachImmediatePlaylistPresentationRouter(PlaylistWorkspaceViewModel workspace)
    {
        AttachPlaylistPresentationRouter(workspace, deferSummaryData: false);
    }

    private static void AttachPlaylistPresentationRouter(
        PlaylistWorkspaceViewModel workspace,
        bool deferSummaryData)
    {
        workspace.PlaylistEntriesHydrationRequested += (_, request) =>
        {
            if (!workspace.TryBeginPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt))
            {
                return;
            }
            workspace.ExecuteCurrentPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt,
                () => { });
        };
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            switch (request.Kind)
            {
                case PlaylistPresentationRefreshKind.SummaryData:
                    workspace.ApplyPlaylistSummaryDataRefresh(deferSummaryData, request.RebuildAsync);
                    break;
                case PlaylistPresentationRefreshKind.SummaryPresentation:
                    workspace.ApplyPlaylistSummaryPresentationRefresh(deferred: false);
                    break;
                case PlaylistPresentationRefreshKind.Tree:
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, deferred: false);
                    break;
                case PlaylistPresentationRefreshKind.HydrationCompleted:
                    if (request.HydrationSourceStore != null
                        && !workspace.IsCurrentPlaylistTreeNotificationSnapshot(
                            request.HydrationSourceStore,
                            request.HydrationSourceTables,
                            request.HydrationNotificationGeneration))
                    {
                        break;
                    }
                    if (!workspace.TryBeginPlaylistHydrationNotification(
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt))
                    {
                        break;
                    }
                    workspace.PublishPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    workspace.ApplyPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        deferred: false,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    break;
                default:
                    throw new InvalidOperationException("Unknown playlist presentation refresh request kind.");
            }
        };
    }

    private sealed class CompositionScoreViewerGateway : IScoreViewerRegistrationGateway
    {
        internal List<string> StatusHashes { get; } = [];

        internal List<string> UploadPaths { get; } = [];

        public bool IsRegistered(string hash)
        {
            StatusHashes.Add(hash);
            return false;
        }

        public ScoreViewerUploadResponse Upload(string path)
        {
            UploadPaths.Add(path);
            return ScoreViewerUploadResponse.Success("composition-hash");
        }
    }

    private sealed class FakeSettingsEditSession : ISettingsEditSession
    {
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        public BeMusicSeeker.Properties.Settings Values { get; set; } = new();

        public List<string> Calls { get; } = [];

        /// <summary>標準接続の保存拒否検証だけで、fixtureが所有するXMLへ実保存・再読込みを接続します。</summary>
        public bool PersistOwnedValues { get; init; }

        public int ReloadCount { get; private set; }

        public int SaveCount { get; private set; }

        public void Reload()
        {
            ReloadCount++;
            Calls.Add("reload");
            if (PersistOwnedValues) { Values.Reload(); }
        }

        public void Save()
        {
            SaveCount++;
            Calls.Add("save");
            if (PersistOwnedValues) { Values.Save(); }
        }
    }

    /// <summary>ライブラリの並列度取得ごとに注入snapshotを再評価し、provider差替え後も初回並列度を使い続ける誤りを検出します。</summary>
    [TestMethod]
    public void LibraryEvaluatesTheInjectedOptionsProviderForEachOperation()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeekerOptionsProvider_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        using (var initialize = new LR2SongDBExtended(songDbPath))
        {
        }
        BmsLibraryOptionsSnapshot current = new()
        {
            PendingInstallEstimateMaxParallelPackages = 3
        };
        try
        {
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => current);
            Assert.AreEqual(3, library.ResolvePendingInstallEstimateParallelPackageDegree());
            current = new BmsLibraryOptionsSnapshot
            {
                PendingInstallEstimateMaxParallelPackages = 5
            };
            Assert.AreEqual(5, library.ResolvePendingInstallEstimateParallelPackageDegree());
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
