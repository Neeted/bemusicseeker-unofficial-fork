using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationCompositionTests
{
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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        Assert.AreSame(snapshot, composition.BmsLibraryOptionsProvider());
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceCreatesNewPlaylistThroughConfiguredStore()
    {
        int previousDefault = BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput;
        BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput =
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
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(),
                    Dispatcher.CurrentDispatcher)
            };
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
                new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
                new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB, dialog.OperationModeLR2DB);
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
            BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput = previousDefault;
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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        Assert.AreSame(snapshot, composition.StartupSettingsProvider());
    }

    [TestMethod]
    public void CompositionCreatesTheDefaultInternalBmsPlayer()
    {
        var composition = new ApplicationComposition(
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
            var settings = new StartupSettingsSnapshot
            {
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
    public void CompositionUsesInjectedDefaultWhenNoExternalPlayerIsSelected()
    {
        bool originalUbMplay = BeMusicSeeker.Properties.Settings.Default.UsePlayeruBMplay;
        bool originalBmi = BeMusicSeeker.Properties.Settings.Default.UsePlayerBMIIDXView;
        bool originalLr2 = BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body;
        var expected = new InternalBMSAutoPlayerSoundOnly();
        var composition = new ApplicationComposition(
            defaultBmsPlayerFactory: () => expected,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
        try
        {
            BeMusicSeeker.Properties.Settings.Default.UsePlayeruBMplay = false;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerBMIIDXView = false;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body = false;

            Assert.AreSame(expected, composition.CreateBmsPlayerForSettings(BeMusicSeeker.Properties.Settings.Default));
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.UsePlayeruBMplay = originalUbMplay;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerBMIIDXView = originalBmi;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body = originalLr2;
        }
    }

    [TestMethod]
    public void CompositionRejectsConfiguredLr2PlayerWhenExecutableIsMissing()
    {
        bool originalUbMplay = BeMusicSeeker.Properties.Settings.Default.UsePlayeruBMplay;
        bool originalBmi = BeMusicSeeker.Properties.Settings.Default.UsePlayerBMIIDXView;
        bool originalLr2 = BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body;
        string originalLr2RootPath = BeMusicSeeker.Properties.Settings.Default.LR2RootPath;
        string originalLr2ConfigPath = BeMusicSeeker.Properties.Settings.Default.LR2ConfigXmlPath;
        try
        {
            BeMusicSeeker.Properties.Settings.Default.UsePlayeruBMplay = false;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerBMIIDXView = false;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body = true;
            string missingRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            BeMusicSeeker.Properties.Settings.Default.LR2RootPath = missingRoot;
            BeMusicSeeker.Properties.Settings.Default.LR2ConfigXmlPath = Path.Combine(missingRoot, "LR2files", "Config.xml");
            var composition = new ApplicationComposition(
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => composition.CreateBmsPlayerForSettings(BeMusicSeeker.Properties.Settings.Default));

            Assert.AreEqual("Configured LR2 playback player could not be created.", exception.Message);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.UsePlayeruBMplay = originalUbMplay;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerBMIIDXView = originalBmi;
            BeMusicSeeker.Properties.Settings.Default.UsePlayerLR2body = originalLr2;
            BeMusicSeeker.Properties.Settings.Default.LR2RootPath = originalLr2RootPath;
            BeMusicSeeker.Properties.Settings.Default.LR2ConfigXmlPath = originalLr2ConfigPath;
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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        Assert.AreSame(snapshot, composition.CustomFolderOutputSettingsProvider());
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredFirstStartupLifecycleBoundary()
    {
        bool firstStartup = true;
        bool completed = false;
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            firstStartupProvider: () => firstStartup,
            completeFirstStartup: () =>
            {
                firstStartup = false;
                completed = true;
            },
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        Assert.IsTrue(composition.FirstStartupProvider());
        composition.CompleteFirstStartup();
        Assert.IsFalse(composition.FirstStartupProvider());
        Assert.IsTrue(completed);
    }

    [TestMethod]
    public void CompositionKeepsTheConfiguredSettingsPersistenceBoundary()
    {
        bool reloaded = false;
        bool saved = false;
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            reloadSettings: () => reloaded = true,
            saveSettings: () => saved = true,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        composition.ReloadSettings();
        composition.SaveSettings();

        Assert.IsTrue(reloaded);
        Assert.IsTrue(saved);
    }

    [TestMethod]
    public void CompositionCreatesMainTableOwnersFromOneBoundary()
    {
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
        MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(
            action => action(),
            _ =>
            {
            });
        var emptyPlaylistTreeSource = new Livet.DispatcherCollection<BMSTable>(
            System.Windows.Threading.Dispatcher.CurrentDispatcher);

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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
            new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
            () => new InternalBMSAutoPlayerSoundOnly(),
            () => Dispatcher.CurrentDispatcher,
            new ChartFileOperationSynchronizer(),
            _ =>
            {
            },
            action => action(),
            _ =>
            {
            },
            (_, _, _, _, _) => [],
            action => action(),
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
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>([first, second]),
                    Dispatcher.CurrentDispatcher)
            };
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
                new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
                () => new InternalBMSAutoPlayerSoundOnly(),
                () => Dispatcher.CurrentDispatcher,
                new ChartFileOperationSynchronizer(),
                _ => { },
                action => action(),
                _ => { },
                (_, _, _, _, _) => [],
                action => action(),
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
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>([first, second, third]),
                    Dispatcher.CurrentDispatcher)
            };
            var composition = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
                new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
                () => new InternalBMSAutoPlayerSoundOnly(),
                () => Dispatcher.CurrentDispatcher,
                new ChartFileOperationSynchronizer(),
                _ => { },
                action => action(),
                _ => { },
                (_, _, _, _, _) => [],
                action => action(),
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
            var playlist = new BMSPlaylist(
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
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>([first, second, third]),
                    Dispatcher.CurrentDispatcher)
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
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new Livet.DispatcherCollection<BMSTable>(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
    public void MainWindowSettingDialogUsesCompositionSettingsPersistenceDelegates()
    {
        int reloadCount = 0;
        int saveCount = 0;
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            firstStartupProvider: () => false,
            completeFirstStartup: () =>
            {
            },
            reloadSettings: () => reloadCount++,
            saveSettings: () => saveCount++,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        Assert.AreEqual(1, reloadCount);

        bool operationMode = BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB;
        string displayTargetIdentity = BeMusicSeeker.Properties.Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            viewModel.SettingDialog.SaveOperationModeForRestart(operationMode);

            Assert.AreEqual(2, reloadCount);
            Assert.AreEqual(1, saveCount);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB = operationMode;
            BeMusicSeeker.Properties.Settings.Default.PlayHistorySelectedDisplayTargetIdentity = displayTargetIdentity;
        }
    }

    [TestMethod]
    public void MainWindowCompositionRoutesPlaylistSummaryRefreshThroughShellArbiter()
    {
        RunOnStaDispatcherThread(() =>
        {
            Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
            DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var composition = new ApplicationComposition(
                    firstStartupProvider: () => false,
                    completeFirstStartup: () => { },
                    reloadSettings: () => { },
                    saveSettings: () => { },
                    initializeOwner: _ => Task.FromResult(true),
                    uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
                MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
                PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
                workspace.IsPlaylistSummaryMode = true;

                long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
                Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache([], dataGeneration));

                workspace.RequestPlaylistSummaryPresentationRefresh();

                Assert.IsFalse(workspace.HasDeferredPlaylistSummaryRefresh());
                Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
            }
            finally
            {
                DispatcherHelper.UIDispatcher = previousDispatcher;
            }
        });
    }

    [TestMethod]
    public void PlaylistTreeSelectionFromWorkerAppliesOnUiDispatcher()
    {
        RunOnStaDispatcherThread(() =>
        {
            Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
            Dispatcher uiDispatcher = Dispatcher.CurrentDispatcher;
            DispatcherHelper.UIDispatcher = uiDispatcher;
            try
            {
                var composition = new ApplicationComposition(
                    () => new BmsLibraryOptionsSnapshot(),
                    firstStartupProvider: () => false,
                    completeFirstStartup: () =>
                    {
                    },
                    uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
                MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
                int uiThreadId = Thread.CurrentThread.ManagedThreadId;
                int workerThreadId = 0;
                int propertyChangedThreadId = 0;
                int propertyChangedCount = 0;
                viewModel.PlaylistWorkspace.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode))
                    {
                        propertyChangedThreadId = Thread.CurrentThread.ManagedThreadId;
                        propertyChangedCount++;
                    }
                };

                Task worker = Task.Run(() =>
                {
                    workerThreadId = Thread.CurrentThread.ManagedThreadId;
                    viewModel.PlaylistWorkspace.RequestSummarySelection();
                });
                var frame = new DispatcherFrame();
                _ = worker.ContinueWith(
                    _ => uiDispatcher.BeginInvoke(
                        DispatcherPriority.ContextIdle,
                        (Action)(() => frame.Continue = false)),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                Dispatcher.PushFrame(frame);
                worker.GetAwaiter().GetResult();

                Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistSummaryMode);
                Assert.AreNotEqual(uiThreadId, workerThreadId);
                Assert.AreEqual(uiThreadId, propertyChangedThreadId);
                Assert.AreEqual(1, propertyChangedCount);
            }
            finally
            {
                DispatcherHelper.UIDispatcher = previousDispatcher;
            }
        });
    }

    private static void RunOnStaDispatcherThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    [TestMethod]
    public void MainWindowSettingDialogUsesInjectedEditSessionForOpenAndRestartSave()
    {
        bool operationMode = BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB;
        var session = new FakeSettingsEditSession();
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            firstStartupProvider: () => false,
            completeFirstStartup: () =>
            {
            },
            settingsEditSession: session,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
            BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB = operationMode;
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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
    public void CompositionSettingsLifecycleSharesSessionAcrossOpenEditReloadRedisplayAndShutdown()
    {
        var values = new BeMusicSeeker.Properties.Settings
        {
            OperationModeLR2DB = true,
            PlayHistorySelectedDisplayTargetIdentity = "all"
        };
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(
            firstStartupProvider: () => false,
            completeFirstStartup: () =>
            {
            },
            settingsEditSession: session,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        Assert.AreEqual(1, session.ReloadCount);
        Assert.AreSame(values, composition.SettingsEditSession.Values);

        viewModel.SettingDialog.SaveOperationModeForRestart(operationMode: false);

        Assert.IsFalse(values.OperationModeLR2DB);
        Assert.AreEqual(2, session.ReloadCount);
        Assert.AreEqual(1, session.SaveCount);

        SettingsDialogViewModel redisplayed = composition.CreateSettingDialogViewModel(
            viewModel,
            viewModel,
            viewModel,
            viewModel.LibraryFolderTree,
            viewModel,
            viewModel.Lr2SongDbSyncWorkflow,
            initializeOwner: () => Task.FromResult(true),
            reloadScoresOnly: () => Task.CompletedTask,
            reloadFileDiff: () => Task.CompletedTask,
            invalidatePlayHistoryReadCache: viewModel.InvalidatePlayHistoryReadCache);
        Assert.IsFalse(redisplayed.OperationModeLR2DB);
        Assert.AreEqual(3, session.ReloadCount);

        viewModel.SaveSettingsForShutdown();

        Assert.AreEqual(2, session.SaveCount);
        CollectionAssert.AreEqual(
            new[] { "reload", "reload", "save", "reload", "save" },
            session.Calls);
    }

    [TestMethod]
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
                uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);
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
            firstStartupProvider: () => false,
            completeFirstStartup: () =>
            {
            },
            reloadSettings: () =>
            {
            },
            saveSettings: () =>
            {
            },
            keywordSearchHistorySettingsStore: store,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        viewModel.ChartFilters.CommitKeywordSearchHistory("new");
        viewModel.PlaylistWorkspace.CommitPlaylistSummaryKeywordSearchHistory("summary-new");

        Assert.AreEqual("new", KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory)[0]);
        Assert.AreEqual("summary-new", KeywordSearchHistoryStore.Deserialize(store.PlaylistSummaryKeywordSearchHistory)[0]);
    }

    [TestMethod]
    public void PlaylistWorkspaceOwnsSummaryKeywordSearchSuggestionsAndHistory()
    {
        var store = new FakeKeywordSearchHistorySettingsStore
        {
            PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["summary-old"])
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            firstStartupProvider: () => false,
            completeFirstStartup: () =>
            {
            },
            reloadSettings: () =>
            {
            },
            saveSettings: () =>
            {
            },
            keywordSearchHistorySettingsStore: store,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        workspace.RefreshPlaylistSummaryKeywordSearchSuggestions(string.Empty, 0, forceHistory: true);

        Assert.IsTrue(workspace.IsPlaylistSummaryKeywordSearchSuggestionPopupOpen);
        Assert.IsFalse(string.IsNullOrWhiteSpace(workspace.PlaylistSummaryKeywordSearchSuggestionHeaderText));
        Assert.AreEqual(1, workspace.PlaylistSummaryKeywordSearchSuggestions.Count);
        Assert.AreEqual("summary-old", workspace.PlaylistSummaryKeywordSearchSuggestions[0].DisplayText);

        workspace.ClosePlaylistSummaryKeywordSearchSuggestions();
        Assert.IsFalse(workspace.IsPlaylistSummaryKeywordSearchSuggestionPopupOpen);

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
            firstStartupProvider: () => false,
            completeFirstStartup: () =>
            {
            },
            reloadSettings: () =>
            {
            },
            saveSettings: () =>
            {
            },
            playHistoryDisplaySettingsStore: store,
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher);

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
        viewModel.PlayHistory.ReplaceDisplayTargetSetsFromSettings(
            replacementJson,
            viewModel.PlaylistWorkspace.CapturePlaylistTreeTablesSnapshot(),
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
                () => false),
            new TestUiDialogService());
    }

    private static RankingCacheDownloadWorkflowOwner CreateDisabledRankingCacheDownloadWorkflowOwner()
    {
        return new RankingCacheDownloadWorkflowOwner(
            new BmsRankingCacheDownloadRuntime(() => null!),
            new ChartFileOperationSynchronizer(),
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

    private sealed class FakeSettingsEditSession : ISettingsEditSession
    {
        public BeMusicSeeker.Properties.Settings Values { get; set; } = BeMusicSeeker.Properties.Settings.Default;

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
            var library = new BMSLibrary(
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
