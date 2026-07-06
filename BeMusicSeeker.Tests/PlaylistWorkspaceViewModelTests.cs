using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspaceViewModelTests
{
    [TestMethod]
    public void RootPlaylistSummaryState_ForwardsToPlaylistWorkspace()
    {
        var viewModel = new MainWindowViewModel();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() };
        var columns = new PlaylistSummaryColumnSettings();
        var propertyNames = new List<string>();
        viewModel.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        viewModel.PlaylistSummaryView = rows;
        viewModel.PlaylistSummaryColumnsSettings = columns;
        viewModel.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
        viewModel.GridHeaderText = "Playlist summary";
        viewModel.PlaylistSummaryKeywordFilter = "title:test";
        viewModel.PlaylistSummaryOwnedFilter = MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedComplete;

        Assert.AreSame(rows, viewModel.PlaylistWorkspace.PlaylistSummaryView);
        Assert.AreSame(columns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(Visibility.Visible, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreEqual("Playlist summary", viewModel.PlaylistWorkspace.GridHeaderText);
        Assert.AreEqual("title:test", viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter);
        Assert.AreEqual(MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedComplete, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
        CollectionAssert.Contains(propertyNames, nameof(MainWindowViewModel.PlaylistSummaryView));
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
    public void PlaylistWorkspaceSummaryApplied_RelaysThroughRootEvent()
    {
        var viewModel = new MainWindowViewModel();
        object? sender = null;
        long observedGeneration = -1;
        viewModel.PlaylistSummaryViewApplied += (s, e) =>
        {
            sender = s;
            observedGeneration = e.DataRebuildGeneration;
        };

        viewModel.PlaylistWorkspace.NotifyPlaylistSummaryViewApplied(42L);

        Assert.AreSame(viewModel, sender);
        Assert.AreEqual(42L, observedGeneration);
    }
}
