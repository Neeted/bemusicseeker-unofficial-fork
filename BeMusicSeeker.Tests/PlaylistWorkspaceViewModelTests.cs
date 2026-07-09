using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspaceViewModelTests
{
    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void SourceText_OwnsPlaylistPresentationStateOutsideRoot()
    {
        string rootSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string workspaceSource = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string logicalSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "MainWindow.cs");

        foreach (string rootField in new[]
        {
            "_PlaylistSummarySortParameters",
            "_PlaylistSummaryColumnsSettings",
            "_ColumnSettingsVisibilityForPlaylist",
            "_PlaylistSummaryView",
            "playlistSummaryPresentationGeneration",
            "playlistSummaryDataRebuildGeneration",
            "playlistSummaryRowsCacheGeneration",
            "previousPlaylistSummaryViewWeakReference",
            "_IsPlaylistSummaryMode",
            "_IsPlaylistDetailViewActive",
            "_UseAsyncChartRowsViewBinding",
            "_GridHeaderText",
            "_PlaylistSummaryKeywordFilter",
            "_PlaylistSummaryKeywordSearchWarningText",
            "_IsPlaylistSummaryKeywordSearchHelpOpen",
            "_PlaylistSummaryKeywordSearchSuggestions",
            "_IsPlaylistSummaryKeywordSearchSuggestionPopupOpen",
            "_PlaylistSummaryKeywordSearchSuggestionHeaderText",
            "_PlaylistSummaryOwnedFilter"
        })
        {
            Assert.AreEqual(-1, rootSource.IndexOf(rootField, StringComparison.Ordinal), rootField);
        }

        StringAssert.Contains(workspaceSource, "public sealed class PlaylistWorkspaceViewModel : ViewModel");
        StringAssert.Contains(workspaceSource, "private ObservableCollection<PlaylistSummaryRow> playlistSummaryView");
        StringAssert.Contains(workspaceSource, "private WeakReference<ObservableCollection<PlaylistSummaryRow>> previousPlaylistSummaryViewWeakReference;");
        StringAssert.Contains(workspaceSource, "private string playlistSummaryText = string.Empty;");
        StringAssert.Contains(workspaceSource, "internal bool TryApplyPlaylistSummary(PlaylistSummaryApplyRequest request)");
        StringAssert.Contains(workspaceSource, "internal long LastPlaylistSummaryBuildCompletedTimestamp");
        StringAssert.Contains(logicalSource, "public PlaylistWorkspaceViewModel PlaylistWorkspace { get; } = new();");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PropertyChanged += PlaylistWorkspacePropertyChanged;");
        Assert.AreEqual(-1, rootSource.IndexOf("public ObservableCollection<PlaylistSummaryRow> PlaylistSummaryView", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("internal event EventHandler<PlaylistSummaryViewAppliedEventArgs> PlaylistSummaryViewApplied", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.PlaylistSummaryViewApplied += MainWindowViewModel_PlaylistSummaryViewApplied;");
    }

    [TestMethod]
    public void RootPlaylistSummaryConfiguration_ForwardsToPlaylistWorkspace()
    {
        var viewModel = new MainWindowViewModel();
        var columns = new PlaylistSummaryColumnSettings();
        var propertyNames = new List<string>();
        viewModel.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        viewModel.PlaylistSummaryColumnsSettings = columns;
        viewModel.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
        viewModel.GridHeaderText = "Playlist summary";
        viewModel.PlaylistSummaryKeywordFilter = "title:test";
        viewModel.PlaylistSummaryOwnedFilter = MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedComplete;

        Assert.AreSame(columns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(Visibility.Visible, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreEqual("Playlist summary", viewModel.PlaylistWorkspace.GridHeaderText);
        Assert.AreEqual("title:test", viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter);
        Assert.AreEqual(MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedComplete, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.GridHeaderText));
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.PlaylistSummaryKeywordFilter));
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.PlaylistSummaryOwnedFilter));
    }

    [TestMethod]
    public void PlaylistWorkspaceKeywordWarning_RelaysLegacyRootProperties()
    {
        var viewModel = new MainWindowViewModel();
        var propertyNames = new List<string>();
        viewModel.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        viewModel.PlaylistWorkspace.SetPlaylistSummaryKeywordSearchWarningText("warning");

        Assert.AreEqual("warning", viewModel.PlaylistSummaryKeywordSearchWarningText);
        Assert.IsTrue(viewModel.HasPlaylistSummaryKeywordSearchWarning);
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.PlaylistSummaryKeywordSearchWarningText));
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.HasPlaylistSummaryKeywordSearchWarning));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyCommitsRowsAndTextBeforeDirectEvent()
    {
        var viewModel = new MainWindowViewModel();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        long cacheGeneration = workspace.IncrementPlaylistSummaryRowsCacheGeneration();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() { TotalCharts = 3 } };
        var notifications = new List<string>();
        object? sender = null;
        long observedGeneration = -1;
        workspace.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        workspace.PlaylistSummaryViewApplied += (s, e) =>
        {
            notifications.Add("applied");
            sender = s;
            observedGeneration = e.DataRebuildGeneration;
            Assert.AreSame(rows, workspace.PlaylistSummaryView);
            Assert.AreEqual("3 charts / 1 playlist", workspace.PlaylistSummaryText);
        };

        bool applied = workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "3 charts / 1 playlist",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        });

        Assert.IsTrue(applied);
        Assert.AreSame(workspace, sender);
        Assert.AreEqual(dataGeneration, observedGeneration);
        CollectionAssert.AreEqual(
            new[] { nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView), nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText), "applied" },
            notifications);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyRejectsStaleGenerationAndInactiveMode()
    {
        var workspace = new PlaylistWorkspaceViewModel();
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        long cacheGeneration = workspace.IncrementPlaylistSummaryRowsCacheGeneration();
        var originalRows = workspace.PlaylistSummaryView;

        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale presentation",
            PresentationGeneration = presentationGeneration - 1,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        }));

        long currentDataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale data",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        }));

        long currentCacheGeneration = workspace.IncrementPlaylistSummaryRowsCacheGeneration();
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale cache",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = currentDataGeneration,
            CacheGeneration = currentCacheGeneration - 1
        }));

        workspace.IsPlaylistSummaryMode = false;
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "inactive",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = currentDataGeneration,
            CacheGeneration = currentCacheGeneration
        }));
        Assert.AreSame(originalRows, workspace.PlaylistSummaryView);
        Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryPublishFailureStillRaisesAppliedEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel { IsPlaylistSummaryMode = true };
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() };
        bool appliedEventRaised = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                throw new InvalidOperationException("rows binding failed");
            }
        };
        workspace.PlaylistSummaryViewApplied += (_, _) => appliedEventRaised = true;

        Assert.ThrowsException<PlaylistSummaryPublishException>(() => workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "committed",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        Assert.AreSame(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("committed", workspace.PlaylistSummaryText);
        Assert.IsTrue(appliedEventRaised);
    }
}
