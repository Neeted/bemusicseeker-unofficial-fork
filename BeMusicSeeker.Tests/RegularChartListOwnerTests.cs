using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListOwnerTests
{
    [TestMethod]
    public void NewOwner_DerivedCachesAreInvalidUntilFirstCommit()
    {
        var owner = new RegularChartListOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel());

        Assert.IsFalse(owner.HasFolderRows);
        Assert.IsFalse(owner.HasKeywordRows);
        Assert.IsFalse(owner.HasModeRows);
    }

    [TestMethod]
    public void TryCommit_RowsReplacingNestedRequest_LatestRequestWins()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        var owner = new RegularChartListOwner(table, workspace);
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult firstBuild = Build(owner, firstLease, new List<LibraryChartRow>());
        RegularChartListBuildResult nestedBuild = null!;
        RegularChartListTerminalResult nestedTerminal = default;
        long nestedRequestId = 0L;
        bool nested = false;
        table.RowsReplacing += (_, _) =>
        {
            if (nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            nestedRequestId = nestedLease.RequestId;
            nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            nestedTerminal = owner.TryCommit(nestedLease, nestedBuild, CreateTerminalInput(nestedBuild));
        };

        RegularChartListTerminalResult firstTerminal = owner.TryCommit(firstLease, firstBuild, CreateTerminalInput(firstBuild));

        Assert.IsTrue(nestedTerminal.WasCommitted);
        Assert.IsFalse(firstTerminal.WasCommitted);
        Assert.AreSame(nestedBuild.Sort.RowsView, table.Rows);
        Assert.AreEqual(nestedRequestId, owner.LastCompletion.RequestId);
        Assert.IsTrue(nestedRequestId > firstLease.RequestId);
    }

    [TestMethod]
    public void TryCommit_RequestInvalidatedDuringPrepare_DoesNotReplaceRows()
    {
        var originalRows = new List<object>();
        var table = new MainChartListViewModel { Rows = originalRows };
        var owner = new RegularChartListOwner(table, new PlaylistWorkspaceViewModel());
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int canceled = 0;
        table.RowsReplacing += (_, _) => owner.InvalidatePendingRequest();
        table.RowsReplacementCanceled += (_, _) => canceled++;

        RegularChartListTerminalResult terminal = owner.TryCommit(lease, build, CreateTerminalInput(build));

        Assert.IsFalse(terminal.WasCommitted);
        Assert.AreSame(originalRows, table.Rows);
        Assert.AreEqual(1, canceled);
    }

    [TestMethod]
    public void Build_AfterCommittedSortCache_ReusesOwnedCache()
    {
        var owner = new RegularChartListOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel());
        var source = new List<LibraryChartRow>();
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult first = Build(owner, firstLease, source, MainViewUpdateMode.FolderFilterSelected);
        Assert.IsTrue(owner.TryCommit(firstLease, first, CreateTerminalInput(first, MainViewUpdateMode.FolderFilterSelected)).WasCommitted);
        Assert.AreEqual(1, owner.SortCacheCount);

        RegularChartListRequestLease secondLease = owner.BeginRequest();
        RegularChartListBuildResult second = Build(owner, secondLease, source, MainViewUpdateMode.FolderFilterSelected);

        Assert.IsTrue(second.Sort.SortReuse);
        Assert.AreSame(first.Sort.RowsView, second.Sort.RowsView);
    }

    [TestMethod]
    public void InvalidatePendingRequest_DisablesNormalSummaryFreshness()
    {
        var table = new MainChartListViewModel();
        var owner = new RegularChartListOwner(table, new PlaylistWorkspaceViewModel());
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        Assert.IsTrue(owner.TryCommit(lease, build, CreateTerminalInput(build)).WasCommitted);
        Assert.IsTrue(owner.IsCurrentRegularRows(table.Rows));

        owner.InvalidatePendingRequest();

        Assert.IsFalse(owner.IsCurrentRegularRows(table.Rows));
    }

    [TestMethod]
    public void TryCommit_RowsNotificationObservesCommittedCompletionAndColumnMode()
    {
        var table = new MainChartListViewModel();
        var owner = new RegularChartListOwner(table, new PlaylistWorkspaceViewModel());
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        long completionAtNotification = 0L;
        MainViewUpdateMode? columnModeAtNotification = null;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                completionAtNotification = owner.LastCompletion.RequestId;
                columnModeAtNotification = owner.LastAppliedColumnMode;
            }
        };

        Assert.IsTrue(owner.TryCommit(lease, build, CreateTerminalInput(build)).WasCommitted);

        Assert.AreEqual(lease.RequestId, completionAtNotification);
        Assert.AreEqual(MainViewUpdateMode.UpdatedNone, columnModeAtNotification);
    }

    [TestMethod]
    public void TryCommit_RowsPublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var owner = new RegularChartListOwner(table, new PlaylistWorkspaceViewModel());
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        RegularChartListBuildResult nestedBuild = null!;
        long nestedRequestId = 0L;
        int summaryNotifications = 0;
        bool nested = false;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.SummaryText))
            {
                summaryNotifications++;
            }
            if (e.PropertyName != nameof(MainChartListViewModel.Rows) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            nestedRequestId = nestedLease.RequestId;
            nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedBuild, CreateTerminalInput(nestedBuild)).WasCommitted);
        };

        Assert.IsTrue(owner.TryCommit(outerLease, outerBuild, CreateTerminalInput(outerBuild)).WasCommitted);

        Assert.AreSame(nestedBuild.Sort.RowsView, table.Rows);
        Assert.AreEqual(nestedRequestId, owner.LastCompletion.RequestId);
        Assert.AreEqual(0, summaryNotifications);
    }

    [TestMethod]
    public void TryCommit_WorkspacePublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        var owner = new RegularChartListOwner(table, workspace);
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        PlaylistSummaryColumnSettings nestedSummarySettings = null!;
        int summaryNotifications = 0;
        bool nested = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                summaryNotifications++;
            }
            if (e.PropertyName != nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            RegularChartListBuildResult nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            RegularChartListTerminalInput nestedInput = CreateTerminalInput(nestedBuild);
            nestedSummarySettings = new PlaylistSummaryColumnSettings();
            nestedInput.ColumnSelection = new MainChartListColumnSelection(
                nestedInput.ColumnSelection.ColumnsSettings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                nestedSummarySettings);
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedBuild, nestedInput).WasCommitted);
        };
        RegularChartListTerminalInput outerInput = CreateTerminalInput(outerBuild);
        outerInput.ColumnSelection = new MainChartListColumnSelection(
            outerInput.ColumnSelection.ColumnsSettings,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.UpdatedNone,
            Visibility.Visible,
            new PlaylistSummaryColumnSettings());

        Assert.IsTrue(owner.TryCommit(outerLease, outerBuild, outerInput).WasCommitted);

        Assert.IsTrue(nested);
        Assert.AreSame(nestedSummarySettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, summaryNotifications);
    }

    [TestMethod]
    public void TryCommit_NoOpNestedPresentation_DoesNotSuppressOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        var owner = new RegularChartListOwner(table, workspace);
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        var sharedSummarySettings = new PlaylistSummaryColumnSettings();
        int visibilityNotifications = 0;
        int summaryNotifications = 0;
        bool nested = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist))
            {
                visibilityNotifications++;
            }
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                summaryNotifications++;
            }
        };
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainChartListViewModel.Rows) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            RegularChartListBuildResult nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            RegularChartListTerminalInput nestedInput = CreateTerminalInput(nestedBuild);
            nestedInput.ColumnSelection = new MainChartListColumnSelection(
                nestedInput.ColumnSelection.ColumnsSettings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Visible,
                sharedSummarySettings);
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedBuild, nestedInput).WasCommitted);
        };
        RegularChartListTerminalInput outerInput = CreateTerminalInput(outerBuild);
        outerInput.ColumnSelection = new MainChartListColumnSelection(
            outerInput.ColumnSelection.ColumnsSettings,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.UpdatedNone,
            Visibility.Visible,
            sharedSummarySettings);

        Assert.IsTrue(owner.TryCommit(outerLease, outerBuild, outerInput).WasCommitted);

        Assert.IsTrue(nested);
        Assert.AreEqual(1, visibilityNotifications);
        Assert.AreEqual(1, summaryNotifications);
    }

    [TestMethod]
    public void Dispose_CancelsCurrentRequestAndRejectsNewRequests()
    {
        var owner = new RegularChartListOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel());
        RegularChartListRequestLease lease = owner.BeginRequest();

        owner.Dispose();
        owner.Dispose();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryBeginRequest(out _));
        Assert.ThrowsException<ObjectDisposedException>(() => owner.BeginRequest());
    }

    [TestMethod]
    public void PrepareRowsApply_RowsReplacingThrows_CancelsPreparation()
    {
        var table = new MainChartListViewModel { Rows = new List<object>() };
        int canceled = 0;
        table.RowsReplacing += (_, _) => throw new InvalidOperationException("prepare failed");
        table.RowsReplacementCanceled += (_, _) => canceled++;

        Assert.ThrowsException<InvalidOperationException>(() => table.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = new List<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.Preserve(),
            Stopwatch = Stopwatch.StartNew()
        }));
        Assert.AreEqual(1, canceled);
    }

    [TestMethod]
    public void TryCommit_MainRowsPublishThrows_StillPublishesColumnPresentation()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        var owner = new RegularChartListOwner(table, workspace);
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int workspaceNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                throw new InvalidOperationException("rows publish failed");
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        RegularChartListTerminalInput input = CreateTerminalInput(build);
        input.ColumnSelection = new MainChartListColumnSelection(
            input.ColumnSelection.ColumnsSettings,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.FolderFilterSelected,
            Visibility.Visible,
            new PlaylistSummaryColumnSettings());

        Assert.ThrowsException<RegularChartListTerminalPublishException>(() => owner.TryCommit(lease, build, input));
        Assert.AreSame(build.Sort.RowsView, table.Rows);
        Assert.IsTrue(workspaceNotifications > 0);
    }

    private static RegularChartListBuildResult Build(
        RegularChartListOwner owner,
        RegularChartListRequestLease lease,
        IEnumerable<LibraryChartRow> rows,
        MainViewUpdateMode mode = MainViewUpdateMode.UpdatedNone)
    {
        var request = new RegularChartListRefreshRequest(
            mode,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: true,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            MainWindowViewModel.ModeFilterType.All,
            sortParameters: null);
        return owner.Build(lease, request, new RegularChartListBuildInput
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            SortCacheGeneration = new NormalLibrarySortCacheGenerationSnapshot(1, 1, 0, 0, 0, 0, 0, 0),
            Stopwatch = Stopwatch.StartNew()
        });
    }

    private static RegularChartListTerminalInput CreateTerminalInput(
        RegularChartListBuildResult build,
        MainViewUpdateMode mode = MainViewUpdateMode.UpdatedNone)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return new RegularChartListTerminalInput
        {
            RowsRequest = new MainChartListRowsApplyRequest
            {
                Rows = build.Sort.RowsView,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.NormalRows(build.Sort.RowsView),
                Stopwatch = stopwatch
            },
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                mode,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = mode,
            Stopwatch = stopwatch
        };
    }
}
