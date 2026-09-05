using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationCompositionTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void CompositionKeepsTheConfiguredLibraryOptionsProvider()
    {
        var snapshot = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            PendingInstallEstimateMaxParallelPackages = 7
        };
        var composition = new ApplicationComposition(
            () => snapshot,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreSame(snapshot, composition.BmsLibraryOptionsProvider());
    }

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
            CollectionAssert.AreEqual(library.GetBMSScores(), bindings.GetBmsScores());

            var request = new BmtSongHashResolveRequest
            {
                Md5 = "missing-md5",
                Sha256 = "missing-sha256",
                Title = "missing-title"
            };
            Tuple<string, string> directResolution = library.CreateBeatorajaBmtSongHashResolver()(request);
            Tuple<string, string> boundResolution = bindings.CreateBeatorajaBmtSongHashResolver()(request);
            Assert.AreEqual(directResolution?.Item1, boundResolution?.Item1);
            Assert.AreEqual(directResolution?.Item2, boundResolution?.Item2);

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

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceCreatesNewPlaylistThroughConfiguredStore()
    {
        int previousDefault = testSettings.PlaylistDefaultIgnoreFolderOutput;
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
            var playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
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
                () => false,
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
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
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
                () => false,
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
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            AttachImmediatePlaylistPresentationRouter(workspace);
            DateTime startedAt = DateTime.Now;
            BMSTable created = await workspace.CreatePlaylistAsync();
            DateTime completedAt = DateTime.Now;

            Assert.IsNotNull(created);
            Assert.AreSame(created, playlist.BMSTables.Single());
            Assert.IsTrue(created.last_update >= startedAt && created.last_update <= completedAt);
            Assert.AreEqual(
                LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                created.ignore_folder_output);
            Assert.IsTrue(created.is_bmt_output);

            PlaylistPropertyDialogViewModel dialog =
                await workspace.CreatePlaylistPropertyDialogAsync();
            Assert.IsNotNull(dialog);
            Assert.AreEqual(testSettings.OperationModeLR2DB, dialog.OperationModeLR2DB);
            Assert.AreEqual(2, playlist.BMSTables.Count);
            Assert.AreSame(dialog, workspace.ActivePropertyDialog);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            dialog.Dispose();
            workspace.ClosePropertyDialog(dialog);
            Assert.AreEqual(1, playlist.BMSTables.Count);
        }
        finally
        {
            testSettings.PlaylistDefaultIgnoreFolderOutput = previousDefault;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredStartupSettingsProvider()
    {
        var snapshot = new StartupSettingsSnapshot
        {
            OperationModeLR2DB = false,
            LR2SongDBPath = "startup-song.db"
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            () => snapshot,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreSame(snapshot, composition.StartupSettingsProvider());
    }

    [TestMethod]
    public void CompositionCreatesTheDefaultInternalBmsPlayer()
    {
        var composition = new ApplicationComposition(
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.IsInstanceOfType(composition.CreateDefaultBmsPlayer(), typeof(InternalBMSAutoPlayerSoundOnly));
    }

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

    [TestMethod]
    public void CompositionCreatesTheConfiguredLr2Player()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "LR2body.exe");
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        string configPath = Path.Combine(configDirectory, "config.xml");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllBytes(executablePath, []);
        File.WriteAllText(configPath, "<config />");
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

    [TestMethod]
    public void CompositionCreatesConfiguredLr2PlayerWhenLibraryOperationModeIsStandalone()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "LR2body.exe");
        string configPath = Path.Combine(root, "LR2files", "Config", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllBytes(executablePath, []);
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");
        var values = new BeMusicSeeker.Properties.Settings
        {
            OperationModeLR2DB = false,
            UsePlayerLR2body = true,
            LR2RootPath = root,
            LR2ConfigXmlPath = configPath
        };
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

    [TestMethod]
    public void CompositionPropagatesConfiguredLr2PlayerConfigFailureWithoutDefaultFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayerComposition", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "LR2body.exe");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(executablePath, []);
        string missingConfigPath = Path.Combine(root, "missing-config.xml");
        var values = new BeMusicSeeker.Properties.Settings
        {
            OperationModeLR2DB = false,
            UsePlayerLR2body = true,
            LR2RootPath = root,
            LR2ConfigXmlPath = missingConfigPath
        };
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

    [TestMethod]
    public void CompositionUsesInjectedDefaultWhenNoExternalPlayerIsSelected()
    {
        bool originalUbMplay = testSettings.UsePlayeruBMplay;
        bool originalBmi = testSettings.UsePlayerBMIIDXView;
        bool originalLr2 = testSettings.UsePlayerLR2body;
        var expected = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => testSettings),
            new BassAudioPlaybackRuntime());
        var composition = new ApplicationComposition(
            defaultBmsPlayerFactory: () => expected,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        try
        {
            testSettings.UsePlayeruBMplay = false;
            testSettings.UsePlayerBMIIDXView = false;
            testSettings.UsePlayerLR2body = false;

            Assert.AreSame(
                expected,
                composition.CreateBmsPlayerForSettings(
                    StartupSettingsSnapshot.CreateCurrent(testSettings)));
        }
        finally
        {
            testSettings.UsePlayeruBMplay = originalUbMplay;
            testSettings.UsePlayerBMIIDXView = originalBmi;
            testSettings.UsePlayerLR2body = originalLr2;
        }
    }

    [TestMethod]
    public void CompositionRejectsConfiguredLr2PlayerWhenExecutableIsMissing()
    {
        bool originalUbMplay = testSettings.UsePlayeruBMplay;
        bool originalBmi = testSettings.UsePlayerBMIIDXView;
        bool originalLr2 = testSettings.UsePlayerLR2body;
        string originalLr2RootPath = testSettings.LR2RootPath;
        string originalLr2ConfigPath = testSettings.LR2ConfigXmlPath;
        try
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
        finally
        {
            testSettings.UsePlayeruBMplay = originalUbMplay;
            testSettings.UsePlayerBMIIDXView = originalBmi;
            testSettings.UsePlayerLR2body = originalLr2;
            testSettings.LR2RootPath = originalLr2RootPath;
            testSettings.LR2ConfigXmlPath = originalLr2ConfigPath;
        }
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredPlaylistUrlCompletionOptionsProvider()
    {
        var snapshot = new PlaylistUrlCompletionOptionsSnapshot
        {
            EnablePlaylistUrlCompletion = true,
            PlaylistMd5UrlMappingTsvUri = "https://example.invalid/playlist.tsv"
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            playlistUrlCompletionOptionsProvider: () => snapshot,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreSame(snapshot, composition.PlaylistUrlCompletionOptionsProvider());
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredBeatorajaBmtOptionsProvider()
    {
        var snapshot = new BeatorajaBmtOptionsSnapshot
        {
            EnableBeatorajaBmtOutput = true,
            BeatorajaBmtTablePath = "table.json",
            BeatorajaBmtHashOutputMode = "FillMissingMd5Sha256"
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            beatorajaBmtOptionsProvider: () => snapshot,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreSame(snapshot, composition.BeatorajaBmtOptionsProvider());
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredCustomFolderOutputSettingsProvider()
    {
        var snapshot = new CustomFolderOutputSettingsSnapshot
        {
            LR2CustomFolderOutputBaseDir = "output-base"
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            customFolderOutputSettingsProvider: () => snapshot,
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreSame(snapshot, composition.CustomFolderOutputSettingsProvider());
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredFirstStartupLifecycleBoundary()
    {
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: true),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.IsTrue(composition.ApplicationLifetime.IsFirstStartup);
        composition.ApplicationLifetime.CompleteFirstStartup();
        Assert.IsFalse(composition.ApplicationLifetime.IsFirstStartup);
    }

    [TestMethod]
    public void CompositionCreatesMainTableOwnersFromOneBoundary()
    {
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(
            action => action(),
            _ =>
            {
            });
        var emptyPlaylistTreeSource = new ObservableCollection<BMSTable>();

        PlaylistWorkspaceViewModel playlistWorkspace = composition.CreatePlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            () => null,
            () => [],
            _ =>
            {
            },
            _ =>
            {
            },
            () => false,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            (_, _) => { },
            _ => { },
            (_, _) => { },
            _ => { },
            () => null!,
            () => null!,
            _ => { },
            emptyPlaylistTreeSource,
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        AttachImmediatePlaylistPresentationRouter(playlistWorkspace);

        Assert.IsNotNull(mainChartList);
        Assert.IsNotNull(playlistWorkspace);
        Assert.IsNotNull(playlistWorkspace.DetailBuildState);
        Assert.IsNotNull(playlistWorkspace.DetailViewState);
        Assert.AreSame(emptyPlaylistTreeSource, playlistWorkspace.PlaylistTreeTables);
    }

    [TestMethod]
    public void CompositionCreatesMainWindowChildOwnersFromOneBoundary()
    {
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(
            action => action(),
            _ =>
            {
            });
        PlaylistWorkspaceViewModel playlistWorkspace = composition.CreatePlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            () => null,
            () => [],
            _ =>
            {
            },
            _ =>
            {
            },
            () => false,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        AttachImmediatePlaylistPresentationRouter(playlistWorkspace);
        MainWindowChildComposition childComposition = composition.CreateMainWindowChildComposition(
            mainChartList,
            playlistWorkspace,
            () => new InternalBMSAutoPlayerSoundOnly(
                new SettingsPlayerSettingsGateway(() => testSettings),
                new BassAudioPlaybackRuntime()),
                new ChartFileOperationSynchronizer(),
            _ =>
            {
            },
            action => action(),
            _ =>
            {
            },
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
            Assert.AreSame(mainChartList, childComposition.MainChartList);
            Assert.AreSame(playlistWorkspace, childComposition.PlaylistWorkspace);
            Assert.IsNotNull(childComposition.ProgressHub);
            Assert.IsNotNull(childComposition.PlaybackPanel);
            Assert.IsNotNull(childComposition.ChartFilters);
            Assert.IsNotNull(childComposition.LibraryFolderTree);
            Assert.IsNotNull(childComposition.InstallTree);
            Assert.IsNotNull(childComposition.MaintenanceTree);
            Assert.IsNotNull(childComposition.PlayHistory);
            Assert.IsNotNull(childComposition.PendingPackageWorkflow);
            Assert.AreSame(
                childComposition.ChartMutationActivity,
                GetChartMutationActivity(childComposition.PendingPackageWorkflow));
            Assert.IsNotNull(childComposition.RegularChartListOwner);
            Assert.IsNotNull(childComposition.PackageInstallWorkflow);
            Assert.IsNotNull(childComposition.MaintenanceRescanWorkflow);
            Assert.IsNotNull(childComposition.ChartInfoParseFailureRemoval);
            Assert.IsNotNull(childComposition.FolderAutoRenameWorkflow);
            Assert.IsNotNull(childComposition.StartupUpdateWorkflow);
            Assert.IsNotNull(childComposition.ElevatedProcessWarningWorkflow);
            Assert.IsNotNull(childComposition.ScoreViewerRegistrationWorkflow);
            Assert.IsNotNull(childComposition.ZeroNoteMaintenanceWorkflow);
            Assert.IsNotNull(childComposition.PackageCatalogWorkflow);
            Assert.AreSame(
                childComposition.ChartMutationActivity,
                GetChartMutationActivity(childComposition.PackageCatalogWorkflow));
            Assert.AreSame(
                childComposition.ChartMutationActivity,
                GetChartMutationActivity(childComposition.DuplicateMaintenanceWorkflow));
            Assert.AreSame(
                childComposition.ChartMutationActivity,
                GetChartMutationActivity(childComposition.SelectedChartMutations));
            Assert.IsNotNull(childComposition.SelectedChartAudioConversion);
            Assert.IsNotNull(childComposition.SelectedChartExternalActions);
            Assert.IsNotNull(childComposition.Lr2SongDbSyncWorkflow);
            Assert.IsNotNull(childComposition.RankingCacheDownloadWorkflow);
        }
        finally
        {
            childComposition.RegularChartListOwner.Dispose();
        }
    }

    [TestMethod]
    public async Task CompositionScoreViewerWorkflowUsesInjectedDialogService()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string chartPath = Path.Combine(tempDirectory, "composition-score-viewer.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\n");
        try
        {
            var values = new BeMusicSeeker.Properties.Settings
            {
                OperationModeLR2DB = false,
                ShowScoreViewerRegisterConfirmMsg = true
            };
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

    private static ChartMutationActivityOwner GetChartMutationActivity(object workflowOwner)
    {
        return (ChartMutationActivityOwner)workflowOwner
            .GetType()
            .GetField("chartMutationActivity", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(workflowOwner);
    }

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
            var playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
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
                () => false,
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
                (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
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
                using (var verify = new LR2SongDBExtended(songDbPath))
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
            var playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings);
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
                () => false,
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
                (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
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
                Assert.AreEqual(1, restoreRequests.Count);
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
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            AttachImmediatePlaylistPresentationRouter(workspace);
            var queuedReasons = new List<string>();
            playlist.StartupBackgroundTaskScheduler = (_, reason, _, _) =>
            {
                queuedReasons.Add(reason);
                return true;
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
            CollectionAssert.AreEqual(new[] { "playlist_summary_bmt_output_changed" }, queuedReasons);
            long generationBeforeNoOp = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = third }],
                "IsBmtOutput",
                value: true).GetAwaiter().GetResult();
            Assert.AreEqual(generationBeforeNoOp, workspace.CurrentPlaylistSummaryDataRebuildGeneration);

            using var verify = new LR2SongDBExtended(songDbPath);
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

    [TestMethod]
    public void MainWindowCompositionRoutesPlaylistSummaryRefreshThroughShellArbiter()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var composition = new ApplicationComposition(
                settingsEditSession: new FakeSettingsEditSession { Values = testSettings }, uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
            workspace.IsPlaylistSummaryMode = true;

            long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
            Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache([], dataGeneration));

            workspace.RequestPlaylistSummaryPresentationRefresh();

            Assert.IsFalse(workspace.HasDeferredPlaylistSummaryRefresh());
            Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
        });
    }

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

    [TestMethod]
    public void MainWindowSettingDialogUsesInjectedEditSessionForOpenAndRestartSave()
    {
        bool operationMode = testSettings.OperationModeLR2DB;
        var session = new FakeSettingsEditSession();
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        try
        {
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

            Assert.AreSame(session, composition.SettingsEditSession);
            Assert.AreEqual(1, session.ReloadCount);

            viewModel.SettingDialog.SaveOperationModeForRestart(operationMode);

            Assert.AreEqual(2, session.ReloadCount);
            Assert.AreEqual(1, session.SaveCount);
        }
        finally
        {
            testSettings.OperationModeLR2DB = operationMode;
        }
    }

    [TestMethod]
    public void CompositionDefaultStartupSettingsProviderUsesInjectedEditSessionValues()
    {
        var values = new BeMusicSeeker.Properties.Settings
        {
            OperationModeLR2DB = false,
            LR2RootPath = "injected-lr2-root"
        };
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        StartupSettingsSnapshot snapshot = composition.StartupSettingsProvider();

        Assert.IsFalse(snapshot.OperationModeLR2DB);
        Assert.AreEqual("injected-lr2-root", snapshot.LR2RootPath);
    }

    [TestMethod]
    public void CompositionDefaultWorkflowSnapshotProvidersUseInjectedEditSessionValues()
    {
        var values = new BeMusicSeeker.Properties.Settings
        {
            PendingInstallEstimateMaxParallelPackages = 11,
            LR2CustomFolderAdditionalOutputBaseDirs = "[\"session-output\"]",
            EnablePlaylistUrlCompletion = true,
            EnableBeatorajaBmtOutput = true,
            LR2CustomFolderOutputBaseDir = "session-output-base",
            ShowDiffBMSInstallConfirmMsg = true,
            DeletePendingPackageSourceAfterInstall = true
        };
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
        Assert.IsTrue(installDestinationOptions.DeletePendingPackageSourceAfterInstall);

        values.PendingInstallEstimateMaxParallelPackages = 13;
        Assert.AreEqual(13, composition.BmsLibraryOptionsProvider().PendingInstallEstimateMaxParallelPackages);
    }

    [TestMethod]
    public void CompositionDefaultColumnSettingsStoreUsesInjectedEditSessionValues()
    {
        var columns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var summaryColumns = new PlaylistSummaryColumnSettings();
        var values = new BeMusicSeeker.Properties.Settings
        {
            StandardCustomTableColumnSettings = columns,
            PlaylistSummaryColumnsSettings = summaryColumns
        };
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        Assert.AreSame(
            columns,
            composition.MainChartColumnSettingsStore.GetMain(
                CustomTableColumnSettings.ViewKind.STANDARD,
                reset: false));
        Assert.AreSame(summaryColumns, composition.MainChartColumnSettingsStore.GetPlaylistSummary(ensureCompatibility: false));
    }

    [TestMethod]
    public void CompositionDefaultSerializedSettingsStoresUseInjectedEditSessionValues()
    {
        var values = new BeMusicSeeker.Properties.Settings
        {
            KeywordSearchHistory = "keyword-history",
            PlaylistSummaryKeywordSearchHistory = "playlist-keyword-history",
            PlayHistorySelectedDisplayTargetIdentity = "set:session",
            PlayHistoryDisplayTargetSetsJson = "display-target-sets"
        };
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

    [TestMethod]
    public async Task CompositionSettingsLifecycleSharesSessionAcrossOpenEditReloadRedisplayAndShutdown()
    {
        var values = new BeMusicSeeker.Properties.Settings
        {
            OperationModeLR2DB = true,
            PlayHistorySelectedDisplayTargetIdentity = "all"
        };
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            settingsEditSession: session,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        Assert.AreEqual(1, session.ReloadCount);
        Assert.AreSame(values, composition.SettingsEditSession.Values);

        viewModel.SettingDialog.SaveOperationModeForRestart(operationMode: false);

        Assert.IsFalse(values.OperationModeLR2DB);
        Assert.AreEqual(2, session.ReloadCount);
        Assert.AreEqual(1, session.SaveCount);

        SettingsDialogViewModel redisplayed = composition.CreateSettingDialogViewModel(
            viewModel,
            viewModel.PlaylistWorkspace,
            viewModel.PlaylistWorkspace,
            viewModel.PlayHistory,
            viewModel.LibraryFolderTree,
            composition,
            viewModel.PlaybackPanel,
            viewModel.Lr2SongDbSyncWorkflow);
        Assert.IsFalse(redisplayed.OperationModeLR2DB);
        Assert.AreEqual(3, session.ReloadCount);

        await viewModel.ShellShutdownWorkflow.CompleteTerminalShutdownAsync();

        Assert.AreEqual(2, session.SaveCount);
        CollectionAssert.AreEqual(
            new[] { "reload", "save", "reload", "reload", "save" },
            session.Calls);
    }

    [TestMethod]
    [DoNotParallelize]
    public void SettingDialogAdditionalOutputPathsUseInjectedSessionInsteadOfGlobalSettings()
    {
        string previousGlobalPaths = BeMusicSeeker.Properties.Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string sessionPath = Path.GetFullPath("session-additional-output");
        string globalPath = Path.GetFullPath("global-additional-output");
        var values = new BeMusicSeeker.Properties.Settings
        {
            LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([sessionPath])
        };
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

    [TestMethod]
    public void MainWindowPlaylistOutputOptionsUseCompositionCustomFolderSettings()
    {
        var values = new BeMusicSeeker.Properties.Settings
        {
            LR2CustomFolderOutputBaseDir = "session-output-base",
            LR2CustomFolderAdditionalOutputBaseDirs = "[\"session-additional\"]"
        };
        var composition = new ApplicationComposition(
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        IReadOnlyList<PlaylistCustomFolderOutputBaseOption> options =
            viewModel.PlaylistWorkspace.CreatePlaylistCustomFolderOutputBaseOptionsForCurrentSettings();

        Assert.IsTrue(options.Any(option => option.Label == "session-output-base"));
        Assert.IsTrue(options.Any(option => option.Label == "session-additional"));
    }

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
        viewModel.ChartFilters.CommitKeywordSearchHistory("new");
        viewModel.PlaylistWorkspace.CommitPlaylistSummaryKeywordSearchHistory("summary-new");

        Assert.AreEqual("new", KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory)[0]);
        Assert.AreEqual("summary-new", KeywordSearchHistoryStore.Deserialize(store.PlaylistSummaryKeywordSearchHistory)[0]);
    }

    [TestMethod]
    public void MainWindowKeywordSearchFavoritesDefaultUsesCompositionSettingsSession()
    {
        var values = new BeMusicSeeker.Properties.Settings();
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            settingsEditSession: new FakeSettingsEditSession { Values = values },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        KeywordSearchSavedQueryMutationResult normalResult =
            viewModel.ChartFilters.TryAddKeywordSearchFavorite("title:default");
        KeywordSearchSavedQueryMutationResult summaryResult =
            viewModel.PlaylistWorkspace.TryAddPlaylistSummaryKeywordSearchFavorite("output:default");

        Assert.IsTrue(normalResult.Succeeded);
        Assert.IsTrue(summaryResult.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "title:default" },
            KeywordSearchFavoritesStore.Deserialize(values.KeywordSearchFavorites).ToArray());
        CollectionAssert.AreEqual(
            new[] { "output:default" },
            KeywordSearchFavoritesStore.Deserialize(values.PlaylistSummaryKeywordSearchFavorites).ToArray());
    }

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
            viewModel.ChartFilters.TryAddKeywordSearchFavorite("title:normal");
        KeywordSearchSavedQueryMutationResult summaryResult =
            viewModel.PlaylistWorkspace.TryAddPlaylistSummaryKeywordSearchFavorite("output:summary");

        Assert.IsTrue(normalResult.Succeeded);
        Assert.IsTrue(summaryResult.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "title:normal" },
            KeywordSearchFavoritesStore.Deserialize(favoriteStore.KeywordSearchFavorites).ToArray());
        CollectionAssert.AreEqual(
            new[] { "output:summary" },
            KeywordSearchFavoritesStore.Deserialize(favoriteStore.PlaylistSummaryKeywordSearchFavorites).ToArray());
    }

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

        workspace.CommitPlaylistSummaryKeywordSearchHistory("summary-new");
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

        string replacementJson = PlayHistoryDisplayTargetSetStore.Serialize(
        [
            new PlayHistoryDisplayTargetSet
            {
                Name = "replacement",
                Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 456 }]
            }
        ]);
        store.DisplayTargetSetsJson = replacementJson;
        viewModel.PlayHistory.RefreshDisplayTargetSetsFromSettings(
            replacementJson,
            queueRefreshWhenSelectionChanges: false);
        Assert.AreEqual("replacement", PlayHistoryDisplayTargetSetStore.Deserialize(store.DisplayTargetSetsJson)[0].Name);
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
        public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
        {
            Values.OperationModeLR2DB = operationMode;
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        public BeMusicSeeker.Properties.Settings Values { get; set; } = new();

        public List<string> Calls { get; } = [];

        public int ReloadCount { get; private set; }

        public int SaveCount { get; private set; }

        public void Reload()
        {
            ReloadCount++;
            Calls.Add("reload");
        }

        public void Save()
        {
            SaveCount++;
            Calls.Add("save");
        }
    }

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
