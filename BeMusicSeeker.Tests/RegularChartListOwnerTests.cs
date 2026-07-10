using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
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
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel());

        Assert.IsFalse(owner.HasFolderRows);
        Assert.IsFalse(owner.HasKeywordRows);
        Assert.IsFalse(owner.HasModeRows);
    }

    [TestMethod]
    public void TryCommit_RowsReplacingNestedRequest_LatestRequestWins()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        RegularChartListOwner owner = CreateOwner(table, workspace);
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel());
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
    public void TryCommitVirtual_NewerRequestPreventsStaleRowsFromReplacingCurrentRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel());
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        var staleRows = new List<object> { new() };
        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new(), new() };

        RegularChartListTerminalResult current = owner.TryCommitVirtual(
            currentLease,
            CreateVirtualTerminalInput(currentRows));
        int stalePrepareCount = 0;
        table.RowsReplacing += (_, _) => stalePrepareCount++;
        RegularChartListTerminalResult stale = owner.TryCommitVirtual(
            staleLease,
            CreateVirtualTerminalInput(staleRows));

        Assert.IsTrue(current.WasCommitted);
        Assert.IsFalse(stale.WasCommitted);
        Assert.AreEqual(0, stalePrepareCount);
        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentLease.RequestId, owner.LastCompletion.RequestId);
    }

    [TestMethod]
    public void VirtualSummary_CurrentRequestUpdatesCommittedRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(),
            _ => { },
            action =>
            {
                pendingUiAction = action;
                uiActionQueued.Set();
            });
        RegularChartListRequestLease lease = owner.BeginRequest();
        var rows = new List<object> { new(), new() };
        Assert.IsTrue(owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(rows)).WasCommitted);
        var key = new MainViewSummaryCacheKey(1, 1, rows.Count, includeBmsonRows: false, "test");
        ChartListSourceRow[] sourceRows =
        [
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms")
        ];

        owner.ScheduleVirtualSummary(lease, key, sourceRows, rows, "test");

        Assert.IsTrue(uiActionQueued.Wait(TimeSpan.FromSeconds(5)));
        pendingUiAction();
        Assert.AreEqual(MainChartListViewModel.FormatSummaryTextForTest(2, 2), table.SummaryText);
    }

    [TestMethod]
    public void VirtualSummary_StaleRequestCannotUpdateNewerRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(),
            _ => { },
            action =>
            {
                pendingUiAction = action;
                uiActionQueued.Set();
            });
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        var staleRows = new List<object> { new(), new() };
        Assert.IsTrue(owner.TryCommitVirtual(staleLease, CreateVirtualTerminalInput(staleRows)).WasCommitted);
        var key = new MainViewSummaryCacheKey(1, 1, staleRows.Count, includeBmsonRows: false, "test");
        owner.ScheduleVirtualSummary(
            staleLease,
            key,
            [CreateSourceRow("Folder A", "a.bms"), CreateSourceRow("Folder B", "b.bms")],
            staleRows,
            "test");
        Assert.IsTrue(uiActionQueued.Wait(TimeSpan.FromSeconds(5)));

        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new() };
        Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
        string currentSummary = table.SummaryText;
        pendingUiAction();

        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentSummary, table.SummaryText);
    }

    [TestMethod]
    public void VirtualSummary_SameKeyRefreshSharesRunningScanWithLatestRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel());
        var sourceRows = new BlockingSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        try
        {
            var key = new MainViewSummaryCacheKey(1, 1, 2, includeBmsonRows: false, "test");
            RegularChartListRequestLease firstLease = owner.BeginRequest();
            var firstRows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(firstLease, CreateVirtualTerminalInput(firstRows)).WasCommitted);
            owner.ScheduleVirtualSummary(firstLease, key, sourceRows, firstRows, "first");
            Assert.IsTrue(sourceRows.EnumerationStarted.Wait(TimeSpan.FromSeconds(5)));

            RegularChartListRequestLease currentLease = owner.BeginRequest();
            var currentRows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
            owner.ScheduleVirtualSummary(currentLease, key, sourceRows, currentRows, "current");
            sourceRows.ReleaseEnumeration.Set();

            string expectedSummary = MainChartListViewModel.FormatSummaryTextForTest(2, 2);
            Assert.IsTrue(SpinWait.SpinUntil(
                () => string.Equals(table.SummaryText, expectedSummary, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)));
            Assert.AreSame(currentRows, table.Rows);
            Assert.AreEqual(1, sourceRows.EnumerationCount);
        }
        finally
        {
            sourceRows.ReleaseEnumeration.Set();
            sourceRows.Dispose();
        }
    }

    [TestMethod]
    public void Build_AfterCommittedSortCache_ReusesOwnedCache()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel());
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel());
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel());
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel());
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel());
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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

    private static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace)
    {
        return new RegularChartListOwner(
            table,
            workspace,
            _ => { },
            action => action());
    }

    private static ChartListSourceRow CreateSourceRow(string folder, string fileName)
    {
        var chart = new ChartFile(
            ChartFileKind.Bms,
            System.IO.Path.Combine(@"C:\Charts", folder, fileName),
            md5: fileName,
            sha256: null,
            title: fileName,
            rawTitle: fileName,
            artist: string.Empty,
            genre: string.Empty,
            folder: folder,
            tag: string.Empty,
            levelText: string.Empty,
            level: null,
            mode: null,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);
        return ChartListSourceRow.FromChartFile(chart);
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

    private static RegularChartListTerminalInput CreateVirtualTerminalInput(IList rows)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return new RegularChartListTerminalInput
        {
            RowsRequest = new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.NormalCounts(rows.Count, distinctFolderCount: -1),
                Stopwatch = stopwatch
            },
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FolderFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.FolderFilterSelected,
            Stopwatch = stopwatch
        };
    }

    private sealed class BlockingSourceRows : IReadOnlyList<ChartListSourceRow>, IDisposable
    {
        private readonly IReadOnlyList<ChartListSourceRow> rows;
        private int enumerationCount;

        internal BlockingSourceRows(params ChartListSourceRow[] rows)
        {
            this.rows = rows;
        }

        internal ManualResetEventSlim EnumerationStarted { get; } = new();

        internal ManualResetEventSlim ReleaseEnumeration { get; } = new();

        internal int EnumerationCount => Volatile.Read(ref enumerationCount);

        public int Count => rows.Count;

        public ChartListSourceRow this[int index] => rows[index];

        public IEnumerator<ChartListSourceRow> GetEnumerator()
        {
            Interlocked.Increment(ref enumerationCount);
            EnumerationStarted.Set();
            if (!ReleaseEnumeration.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Summary enumeration was not released.");
            }
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Dispose()
        {
            EnumerationStarted.Dispose();
            ReleaseEnumeration.Dispose();
        }
    }
}
