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
