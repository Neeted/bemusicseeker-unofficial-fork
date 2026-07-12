using System;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
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
        var composition = new ApplicationComposition(() => snapshot);

        Assert.AreSame(snapshot, composition.BmsLibraryOptionsProvider());
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
            () => snapshot);

        Assert.AreSame(snapshot, composition.StartupSettingsProvider());
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
            playlistUrlCompletionOptionsProvider: () => snapshot);

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
            beatorajaBmtOptionsProvider: () => snapshot);

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
            customFolderOutputSettingsProvider: () => snapshot);

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
            });

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
            saveSettings: () => saved = true);

        composition.ReloadSettings();
        composition.SaveSettings();

        Assert.IsTrue(reloaded);
        Assert.IsTrue(saved);
    }

    [TestMethod]
    public void CompositionCreatesMainTableOwnersFromOneBoundary()
    {
        var composition = new ApplicationComposition(() => new BmsLibraryOptionsSnapshot());
        MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(
            action => action(),
            _ =>
            {
            });

        PlaylistWorkspaceViewModel playlistWorkspace = composition.CreatePlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ =>
            {
            },
            _ =>
            {
            });

        Assert.IsNotNull(mainChartList);
        Assert.IsNotNull(playlistWorkspace);
    }

    [TestMethod]
    public void CompositionCreatesMainWindowChildOwnersFromOneBoundary()
    {
        var composition = new ApplicationComposition(() => new BmsLibraryOptionsSnapshot());
        MainChartListViewModel mainChartList = composition.CreateMainChartListViewModel(
            action => action(),
            _ =>
            {
            });
        PlaylistWorkspaceViewModel playlistWorkspace = composition.CreatePlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ =>
            {
            },
            _ =>
            {
            });
        MainWindowChildComposition childComposition = composition.CreateMainWindowChildComposition(
            mainChartList,
            playlistWorkspace,
            () => null,
            () => null,
            () => null,
            () => null,
            () => null,
            _ =>
            {
            },
            action => action(),
            _ =>
            {
            },
            () => [],
            (_, _, _) => 0L,
            (_, _) =>
            {
            },
            _ =>
            {
            },
            _ =>
            {
            });

        try
        {
            Assert.AreSame(mainChartList, childComposition.MainChartList);
            Assert.AreSame(playlistWorkspace, childComposition.PlaylistWorkspace);
            Assert.IsNotNull(childComposition.ProgressHub);
            Assert.IsNotNull(childComposition.PlaybackPanel);
            Assert.IsNotNull(childComposition.ChartFilters);
            Assert.IsNotNull(childComposition.RuntimeContext);
            Assert.IsNotNull(childComposition.PlayHistory);
            Assert.IsNotNull(childComposition.PlaylistSummaryColumns);
            Assert.IsNotNull(childComposition.PlaylistSummaryBmtSort);
            Assert.IsNotNull(childComposition.RegularChartListOwner);
            Assert.IsNotNull(childComposition.DropInstallQueueProcessor);
        }
        finally
        {
            childComposition.RegularChartListOwner.Dispose();
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
            saveSettings: () => saveCount++);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        Assert.AreEqual(1, reloadCount);

        bool operationMode = BeMusicSeeker.Properties.Settings.Default.OperationModeLR2DB;
        string displayTargetIdentity = BeMusicSeeker.Properties.Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        try
        {
            viewModel.settingDialog.SaveOperationModeForRestart(operationMode);

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
            settingsEditSession: session);

        try
        {
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

            Assert.AreSame(session, composition.SettingsEditSession);
            Assert.AreEqual(1, session.ReloadCount);

            viewModel.settingDialog.SaveOperationModeForRestart(operationMode);

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
            settingsEditSession: session);

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
            LR2CustomFolderOutputBaseDir = "session-output-base"
        };
        var session = new FakeSettingsEditSession { Values = values };
        var composition = new ApplicationComposition(settingsEditSession: session);

        BmsLibraryOptionsSnapshot libraryOptions = composition.BmsLibraryOptionsProvider();
        PlaylistUrlCompletionOptionsSnapshot playlistOptions = composition.PlaylistUrlCompletionOptionsProvider();
        BeatorajaBmtOptionsSnapshot beatorajaOptions = composition.BeatorajaBmtOptionsProvider();
        CustomFolderOutputSettingsSnapshot customFolderOptions = composition.CustomFolderOutputSettingsProvider();

        Assert.AreEqual(11, libraryOptions.PendingInstallEstimateMaxParallelPackages);
        Assert.AreEqual(1, libraryOptions.LR2CustomFolderAdditionalOutputBaseDirs.Count);
        Assert.AreEqual(
            Path.GetFullPath("session-output"),
            libraryOptions.LR2CustomFolderAdditionalOutputBaseDirs[0]);
        Assert.IsTrue(playlistOptions.EnablePlaylistUrlCompletion);
        Assert.IsTrue(beatorajaOptions.EnableBeatorajaBmtOutput);
        Assert.AreEqual("session-output-base", customFolderOptions.LR2CustomFolderOutputBaseDir);

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
        var composition = new ApplicationComposition(settingsEditSession: session);

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
        var composition = new ApplicationComposition(settingsEditSession: session);

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
            keywordSearchHistorySettingsStore: store);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        viewModel.CommitKeywordSearchHistory("new");
        viewModel.CommitPlaylistSummaryKeywordSearchHistory("summary-new");

        Assert.AreEqual("new", KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory)[0]);
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
            playHistoryDisplaySettingsStore: store);

        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        Assert.AreEqual("set:CUSTOM", viewModel.PlayHistory.SelectedDisplayTargetIdentity);
        viewModel.PlayHistory.SelectedDisplayTargetIdentity = "all";
        Assert.AreEqual("all", store.SelectedDisplayTargetIdentity);

        viewModel.ReplacePlayHistoryDisplayTargetSetsForTest(
        [
            new PlayHistoryDisplayTargetSet
            {
                Name = "replacement",
                Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 456 }]
            }
        ]);
        Assert.AreEqual("replacement", PlayHistoryDisplayTargetSetStore.Deserialize(store.DisplayTargetSetsJson)[0].Name);
    }

    private sealed class FakePlayHistoryDisplaySettingsStore : IPlayHistoryDisplaySettingsStore
    {
        public string SelectedDisplayTargetIdentity { get; set; } = string.Empty;

        public string DisplayTargetSetsJson { get; set; } = string.Empty;
    }

    private sealed class FakeSettingsEditSession : ISettingsEditSession
    {
        public BeMusicSeeker.Properties.Settings Values { get; set; } = BeMusicSeeker.Properties.Settings.Default;

        public int ReloadCount { get; private set; }

        public int SaveCount { get; private set; }

        public void Reload()
        {
            ReloadCount++;
        }

        public void Save()
        {
            SaveCount++;
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
