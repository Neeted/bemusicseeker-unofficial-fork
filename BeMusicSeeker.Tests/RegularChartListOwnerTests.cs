using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
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
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));

        Assert.IsFalse(owner.HasFolderRows);
        Assert.IsFalse(owner.HasKeywordRows);
        Assert.IsFalse(owner.HasModeRows);
    }

    [TestMethod]
    public void ApplyRegularView_ClearsPlaylistSourceStateBeforeRegularPublish()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        workspace.IsPlaylistDetailViewActive = true;
        workspace.UseAsyncChartRowsViewBinding = false;
        var buildState = new PlaylistDetailBuildState
        {
            RequestVersion = 1,
            PendingRequest = new PlaylistBuildRequest(),
            CurrentBuildRequest = new PlaylistBuildRequest(),
            CurrentBuildCancellation = new CancellationTokenSource()
        };
        var sourceRows = new List<PlaylistDetailSourceRow> { CreatePlaylistSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") };
        var viewRows = new List<object> { new PlaylistDetailRow(sourceRows[0]) };
        var viewState = new PlaylistDetailViewState();
        viewState.Source.Rows = sourceRows;
        viewState.View.Rows = viewRows;
        var logs = new List<string>();
        var owner = new RegularChartListOwner(
            table,
            workspace,
            logs.Add,
            action => action(),
            logs.Add,
            buildState,
            viewState);
        int? sourceClearVersionAtRowsNotification = null;
        bool? detailActiveAtRowsNotification = null;
        bool? asyncBindingAtRowsNotification = null;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                if (viewState.Source.Rows.Count == 0)
                {
                    sourceClearVersionAtRowsNotification = buildState.RequestVersion;
                }
                detailActiveAtRowsNotification = workspace.IsPlaylistDetailViewActive;
                asyncBindingAtRowsNotification = workspace.UseAsyncChartRowsViewBinding;
            }
        };

        RegularChartListEntryResult result = owner.ApplyRegularView(CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(2, buildState.RequestVersion);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
        Assert.IsNull(buildState.PendingRequest);
        Assert.IsNull(buildState.CurrentBuildRequest);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, owner.LastAppliedColumnMode);
        Assert.AreEqual(2, sourceClearVersionAtRowsNotification);
        Assert.AreEqual(false, detailActiveAtRowsNotification);
        Assert.AreEqual(true, asyncBindingAtRowsNotification);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.IsTrue(workspace.UseAsyncChartRowsViewBinding);
        Assert.IsTrue(logs.Any(log => log.Contains("playlist_source_replace action=clear")));
    }

    [TestMethod]
    public void MaterializedApply_OwnsBuildAndTerminalPipeline()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        List<LibraryChartRow> rows =
        [
            LibraryChartRow.FromChartFile(CreateSourceRow("Folder A", "Beta").Chart),
            LibraryChartRow.FromChartFile(CreateSourceRow("Folder A", "Alpha").Chart)
        ];
        var refresh = new RegularChartListRefreshRequest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: false,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            RegularChartModeFilter.All,
            ChartListSortSpecification.Create(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, hasValue: true));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        RegularMaterializedChartListApplyResult result = owner.TryApplyMaterialized(
            new RegularMaterializedChartListApplyRequest
            {
                RefreshRequest = refresh,
                HasFolderRowsOverride = true,
                FolderRowsOverride = rows,
                ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
                ColumnSelection = new MainChartListColumnSelection(
                    settings,
                    reused: false,
                    elapsedMs: 0L,
                    MainViewUpdateMode.FolderFilterSelected,
                    Visibility.Collapsed,
                    new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.SortUpdated,
                Stopwatch = Stopwatch.StartNew()
            });

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]).Title);
        Assert.AreEqual("Beta", ((LibraryChartRow)table.Rows[1]).Title);
        Assert.AreEqual(2, result.FolderCount);
        Assert.AreEqual(2, result.ModeCount);
    }

    [TestMethod]
    public void MaterializedApply_NestedNewerRequestRejectsOuterTerminal()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        List<LibraryChartRow> outerRows =
        [
            LibraryChartRow.FromChartFile(CreateSourceRow("Outer", "outer.bms").Chart)
        ];
        List<LibraryChartRow> nestedRows =
        [
            LibraryChartRow.FromChartFile(CreateSourceRow("Nested", "nested.bms").Chart)
        ];
        bool nestedStarted = false;
        RegularMaterializedChartListApplyResult nestedResult = default;
        table.RowsReplacing += (_, _) =>
        {
            if (nestedStarted)
            {
                return;
            }
            nestedStarted = true;
            nestedResult = owner.TryApplyMaterialized(CreateMaterializedApplyRequest(nestedRows));
        };

        RegularMaterializedChartListApplyResult outerResult = owner.TryApplyMaterialized(
            CreateMaterializedApplyRequest(outerRows));

        Assert.IsTrue(nestedResult.WasCommitted);
        Assert.IsFalse(outerResult.WasCommitted);
        Assert.AreEqual("nested.bms", ((LibraryChartRow)table.Rows[0]).Title);
    }

    [TestMethod]
    public void VirtualRowCache_ReusesAndRemovesBmsOwnerRowsThroughRegularOwner()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var file = new BMSFile
        {
            path = @"C:\Charts\Owner\chart.bms",
        };
        ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(ChartFileProjection.FromBmsFile(file));

        LibraryChartRow first = owner.CreateVirtualRow(null, sourceRow);
        LibraryChartRow second = owner.CreateVirtualRow(null, sourceRow);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, owner.SnapshotRows().Count);
        Assert.AreEqual(1, owner.RemoveBmsRows([file]));
        Assert.AreEqual(0, owner.SnapshotRows().Count);
    }

    [TestMethod]
    public void SourceInvalidation_ConsumesEachPositiveOwnedCollectionVersionOnce()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));

        Assert.IsTrue(owner.TryInvalidateSourceForOwnedCollectionVersion(0, out _));
        Assert.AreEqual(1L, owner.SourceGeneration);
        Assert.IsTrue(owner.TryInvalidateSourceForOwnedCollectionVersion(2, out _));
        Assert.AreEqual(2L, owner.SourceGeneration);
        Assert.IsFalse(owner.TryInvalidateSourceForOwnedCollectionVersion(2, out _));
        Assert.AreEqual(2L, owner.SourceGeneration);
    }

    [TestMethod]
    public void VirtualSourceRows_StaleLookupCannotRepopulateInvalidatedCache()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        RegularVirtualSourceRowsLookup staleLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        var rows = new List<ChartListSourceRow> { CreateSourceRow("Folder A", "a.bms") };

        owner.InvalidateSource();
        owner.TryPublishVirtualSourceRows(staleLookup, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);

        RegularVirtualSourceRowsLookup currentLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(currentLookup, rows);
        RegularVirtualSourceRowsLookup cached = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        Assert.IsTrue(cached.CacheHit);
        Assert.AreSame(rows, cached.Rows);

        RegularVirtualSourceRowsLookup staleAfterSortChange = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.InvalidateIdentitySortKeys(clearSourceRows: true);
        owner.TryPublishVirtualSourceRows(staleAfterSortChange, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);
    }

    [TestMethod]
    public void VirtualSourceRows_LibraryIdentityChangeInvalidatesDerivedOrders()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var firstLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        List<ChartListSourceRow> rows = [CreateSourceRow("Folder A", "a.bms")];
        Assert.IsTrue(owner.TryBeginVirtualRequest(firstLibrary, out RegularChartListRequestLease firstLease));
        RegularVirtualSourceRowsLookup firstLookup = owner.LookupVirtualSourceRows(firstLibrary, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(firstLookup, rows);
        NormalLibrarySortCacheKey orderKey = owner.CreateVirtualOrderKey(
            firstLookup.SourceGeneration,
            firstLookup.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rows.Count,
            new RegularChartListExternalVersions(0, 0, 0));
        Assert.IsTrue(owner.TryPublishVirtualOrder(
            orderKey,
            CreateOrder(rows[0]),
            new RegularChartListExternalVersions(0, 0, 0)));

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out RegularChartListPrewarmLease secondLease));
        RegularVirtualSourceRowsLookup secondLookup = owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false);

        Assert.IsFalse(secondLookup.CacheHit);
        Assert.IsTrue(secondLookup.SourceGeneration > firstLookup.SourceGeneration);
        Assert.IsFalse(owner.TryGetVirtualOrder(orderKey, out _));
        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        secondLease.Dispose();
    }

    [TestMethod]
    public void VirtualSourceRows_ConcurrentInitialLibraryMissRejectsStalePublisher()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var firstLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        Assert.IsTrue(owner.TryBeginVirtualRequest(firstLibrary, out RegularChartListRequestLease firstLease));
        RegularVirtualSourceRowsLookup firstLookup = owner.LookupVirtualSourceRows(firstLibrary, includeBmsonRows: false);

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out RegularChartListPrewarmLease secondLease));
        RegularVirtualSourceRowsLookup secondLookup = owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(firstLookup, [CreateSourceRow("Folder A", "a.bms")]);
        Assert.IsFalse(owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false).CacheHit);
        List<ChartListSourceRow> secondRows = [CreateSourceRow("Folder B", "b.bms")];
        owner.TryPublishVirtualSourceRows(secondLookup, secondRows);

        RegularVirtualSourceRowsLookup cached = owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false);
        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        Assert.IsTrue(cached.CacheHit);
        Assert.AreSame(secondRows, cached.Rows);
        secondLease.Dispose();
    }

    [TestMethod]
    public void VirtualOrderPrewarm_LibrarySwitchCancelsRunningDifferentLibraryLease()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var firstLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(firstLibrary, out RegularChartListPrewarmLease firstLease));

        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out _));

        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        firstLease.Dispose();
    }

    [TestMethod]
    public void VirtualOrderPrewarm_BuildsOwnedOrderAndCompletesLease()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        List<ChartListSourceRow> rows =
        [
            CreateSourceRow("Folder A", "b.bms"),
            CreateSourceRow("Folder A", "a.bms")
        ];
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(lookup, rows);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        using (lease)
        {
            owner.RunVirtualOrderPrewarm(
                lease,
                library: null,
                includeBmsonRows: false,
                [new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 1)],
                "test");
        }

        NormalLibrarySortCacheKey key = owner.CreateVirtualOrderKey(
            lookup.SourceGeneration,
            lookup.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rows.Count,
            new RegularChartListExternalVersions(0, 0, 0));
        Assert.IsTrue(owner.TryGetVirtualOrder(key, out ChartListOrder order));
        CollectionAssert.AreEqual(new[] { 1, 0 }, order.Indexes.ToArray());
        Assert.IsTrue(lease.Completion.IsCompleted);
    }

    [TestMethod]
    public void VirtualNormalLibraryApply_OwnsSourceOrderFilterAndTerminalReuse()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        List<ChartListSourceRow> sourceRows =
        [
            CreateSourceRow("Folder A", "Alpha"),
            CreateSourceRow("Folder B", "Bravo"),
            CreateSourceRow("Folder A", "Charlie")
        ];
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(lookup, sourceRows);
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var request = new RegularVirtualNormalLibraryApplyRequest
        {
            IncludeBmsonRows = false,
            TreeFilter = RegularNormalLibraryTreeFilter.Create(RegularChartFolderFilterKind.Directory, @"C:\Charts\Folder A"),
            KeywordFilter = string.Empty,
            ModeFilter = RegularChartModeFilter.All,
            SortColumnName = nameof(LibraryChartRow.Title),
            SortDirection = ListSortDirection.Descending,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FolderFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.FolderFilterSelected,
            Stopwatch = Stopwatch.StartNew(),
            Reason = "test"
        };

        RegularVirtualNormalLibraryApplyResult first = owner.TryApplyVirtualNormalLibrary(request);
        RegularVirtualNormalLibraryApplyResult second = owner.TryApplyVirtualNormalLibrary(request);

        Assert.IsTrue(first.WasCommitted);
        Assert.IsTrue(second.WasCommitted);
        Assert.AreSame(second.RowsView, table.Rows);
        Assert.AreEqual(2, second.FolderCount);
        Assert.AreEqual(2, second.RowsView.Count);
        Assert.AreEqual("Charlie", ((LibraryChartRow)second.RowsView[0]).Title);
        Assert.AreEqual("Alpha", ((LibraryChartRow)second.RowsView[1]).Title);
        Assert.IsTrue(second.SourceRowsCacheHit);
        Assert.IsTrue(second.SortCacheHit);
    }

    [TestMethod]
    public void VirtualChartSubsetApply_OwnsProjectionOrderFilterAndTerminalReuse()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        List<ChartFile> charts =
        [
            CreateSourceRow("Folder A", "Alpha").Chart,
            CreateSourceRow("Folder B", "Bravo").Chart,
            CreateSourceRow("Folder A", "Charlie").Chart
        ];
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var request = new RegularVirtualChartSubsetApplyRequest
        {
            SourceCharts = charts,
            SourceProjectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
            TreeMode = MainViewUpdateMode.FileMissingFilterSelected,
            SubsetName = "file_missing",
            KeywordFilter = string.Empty,
            ModeFilter = RegularChartModeFilter.All,
            SortColumnName = nameof(LibraryChartRow.Title),
            SortDirection = ListSortDirection.Descending,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FileMissingFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.FileMissingFilterSelected,
            Stopwatch = Stopwatch.StartNew()
        };

        RegularVirtualChartSubsetApplyResult first = owner.TryApplyVirtualChartSubset(request);
        RegularVirtualChartSubsetApplyResult second = owner.TryApplyVirtualChartSubset(request);

        Assert.IsTrue(first.WasCommitted);
        Assert.IsTrue(second.WasCommitted);
        Assert.AreSame(second.RowsView, table.Rows);
        Assert.AreEqual(3, second.RowsView.Count);
        Assert.AreEqual("Charlie", ((LibraryChartRow)second.RowsView[0]).Title);
        Assert.AreEqual("Bravo", ((LibraryChartRow)second.RowsView[1]).Title);
        Assert.AreEqual("Alpha", ((LibraryChartRow)second.RowsView[2]).Title);
        Assert.IsTrue(second.SortCacheHit);
        Assert.AreEqual(2, second.DistinctFolderCount);
    }

    [TestMethod]
    public void RegularEntry_SelectsDefaultVirtualAndResetsUnsupportedSort()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
        RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected);
        owner.SetSort(ChartListSortSpecification.Create("UnsupportedColumn", ListSortDirection.Descending, hasValue: true));

        RegularChartListEntryResult result = owner.ApplyRegularView(request);

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.DefaultVirtual, result.Route);
        Assert.IsTrue(result.SortWasReset);
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]).Title);
        Assert.AreEqual("Bravo", ((LibraryChartRow)table.Rows[1]).Title);
    }

    [TestMethod]
    public void RegularEntry_AppliesOwnerFilterState()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
        owner.SetFilters("Alpha", RegularChartModeFilter.All);

        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]).Title);
    }

    [TestMethod]
    public void RegularEntry_AppliesOwnerModeFilterState()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder A", "Seven", mode: 7), CreateSourceRow("Folder B", "Nine", mode: 9)]);
        owner.SetFilters(string.Empty, RegularChartModeFilter.SevenKeys);

        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Seven", ((LibraryChartRow)table.Rows[0]).Title);
    }

    [TestMethod]
    public void RegularEntry_SelectsDuplicateSubsetFromLibraryOwner()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        ChartFile bravo = CreateSourceRow("Folder B", "Bravo").Chart;
        ChartFile alpha = CreateSourceRow("Folder A", "Alpha").Chart;
        var library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        typeof(BMSLibrary)
            .GetField("lockChartInfoIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(library, new object());
        typeof(BMSLibrary)
            .GetField("_DuplicateChartGroups", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(library, new List<DuplicateGroup> { new([bravo, alpha], [bravo.Folder, alpha.Folder]) });
        RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.DuplicateFilterSelected);
        request.Library = library;

        RegularChartListEntryResult result = owner.ApplyRegularView(request);

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.SubsetVirtual, result.Route);
        Assert.IsFalse(result.SortWasReset);
        Assert.AreEqual(2, table.Rows.Count);
    }

    [TestMethod]
    public void RegularEntry_DuplicateFolderContextSelectsOnlyMatchingCharts()
    {
        ChartFile matching = CreateSourceRow("Folder A", "Alpha").Chart;
        ChartFile other = CreateSourceRow("Folder B", "Bravo").Chart;
        var groups = new[]
        {
            new DuplicateGroup([matching, other], [matching.Folder, other.Folder]) { Header = "Group" }
        };

        Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource(
            groups,
            DuplicateViewContext.ForFolder(System.IO.Path.Combine(@"C:\Charts", "Folder A")),
            out RegularChartListSubsetSource source));
        groups[0].ChartFiles.Clear();

        CollectionAssert.AreEqual(new[] { matching }, source.SourceCharts.ToArray());
        Assert.AreEqual("duplicate_folder", source.Name);
    }

    [TestMethod]
    public void DependencyInvalidation_PrunesDefaultAndSubsetOrderCachesTogether()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var versions = new RegularChartListExternalVersions(score: 1, chartInfo: 0, maintenanceHydration: 0);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));
        NormalLibrarySortCacheKey defaultKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        VirtualChartSubsetSortCacheKey subsetKey = owner.CreateVirtualSubsetOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            treeMode: (int)MainViewUpdateMode.FileMissingFilterSelected,
            subsetName: "missing",
            sourceRowsSignature: 1,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryPublishVirtualOrder(defaultKey, order, versions));
        Assert.IsTrue(owner.TryPublishVirtualSubsetOrder(subsetKey, order, versions));

        int removed = owner.InvalidateSortCacheByDependency(MainViewDataDependency.Score, out int cacheCount);

        Assert.AreEqual(2, cacheCount);
        Assert.AreEqual(2, removed);
        Assert.IsFalse(owner.TryGetVirtualOrder(defaultKey, out _));
        Assert.IsFalse(owner.TryGetVirtualSubsetOrder(subsetKey, out _));
    }

    [TestMethod]
    public void VirtualOrder_ExternalVersionChangeRejectsStalePublish()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var initialVersions = new RegularChartListExternalVersions(score: 1, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey key = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: initialVersions);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));

        bool published = owner.TryPublishVirtualOrder(
            key,
            order,
            new RegularChartListExternalVersions(score: 2, chartInfo: 0, maintenanceHydration: 0));

        Assert.IsFalse(published);
        Assert.IsFalse(owner.TryGetVirtualOrder(key, out _));
    }

    [TestMethod]
    public void VirtualSubsetOrder_ExternalVersionChangeRejectsStalePublish()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var initialVersions = new RegularChartListExternalVersions(score: 0, chartInfo: 1, maintenanceHydration: 0);
        VirtualChartSubsetSortCacheKey key = owner.CreateVirtualSubsetOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            treeMode: (int)MainViewUpdateMode.FileMissingFilterSelected,
            subsetName: "missing",
            sourceRowsSignature: 1,
            nameof(LibraryChartRow.ChartNotes),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: initialVersions);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));

        bool published = owner.TryPublishVirtualSubsetOrder(
            key,
            order,
            new RegularChartListExternalVersions(score: 0, chartInfo: 2, maintenanceHydration: 0));

        Assert.IsFalse(published);
        Assert.IsFalse(owner.TryGetVirtualSubsetOrder(key, out _));
    }

    [TestMethod]
    public void WarningOrderCache_IsPrunedByInstallDestinationAndMaintenanceChanges()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 1);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));
        NormalLibrarySortCacheKey installKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.WarningDigestText),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryPublishVirtualOrder(installKey, order, versions));

        Assert.AreEqual(1, owner.InvalidateSortCacheByDependency(MainViewDataDependency.InstallDestination, out _));
        Assert.IsFalse(owner.TryGetVirtualOrder(installKey, out _));

        NormalLibrarySortCacheKey maintenanceKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.WarningDigestText),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryPublishVirtualOrder(maintenanceKey, order, versions));
        Assert.AreEqual(1, owner.InvalidateSortCacheByDependency(MainViewDataDependency.Maintenance, out _));
        Assert.IsFalse(owner.TryGetVirtualOrder(maintenanceKey, out _));
    }

    [TestMethod]
    public void TryCommit_RowsReplacingNestedRequest_LatestRequestWins()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(action => action());
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int canceled = 0;
        table.RowsReplacing += (_, _) => owner.InvalidatePendingRequest();
        table.RowsReplacementCanceled += (_, _) =>
        {
            canceled++;
            Task lockProbe = Task.Run(owner.InvalidatePendingRequest);
            Assert.IsTrue(lockProbe.Wait(TimeSpan.FromSeconds(5)), "RowsReplacementCanceled must run after the regular owner lock is released.");
        };

        RegularChartListTerminalResult terminal = owner.TryCommit(lease, build, CreateTerminalInput(build));

        Assert.IsFalse(terminal.WasCommitted);
        Assert.AreSame(originalRows, table.Rows);
        Assert.AreEqual(1, canceled);
    }

    [TestMethod]
    public void TryCommitVirtual_NewerRequestPreventsStaleRowsFromReplacingCurrentRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
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
            new PlaylistWorkspaceViewModel(action => action()),
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
        Assert.AreEqual("[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]", table.SummaryText);
    }

    [TestMethod]
    public void VirtualSummary_StaleRequestCannotUpdateNewerRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(action => action()),
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
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

            string expectedSummary = "[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]";
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
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var source = new List<LibraryChartRow>();
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult first = Build(owner, firstLease, source, MainViewUpdateMode.FolderFilterSelected);
        Assert.IsTrue(owner.TryCommit(firstLease, first, CreateTerminalInput(first, MainViewUpdateMode.FolderFilterSelected)).WasCommitted);
        Assert.AreEqual(1, owner.CacheCount);

        RegularChartListRequestLease secondLease = owner.BeginRequest();
        RegularChartListBuildResult second = Build(owner, secondLease, source, MainViewUpdateMode.FolderFilterSelected);

        Assert.IsTrue(second.Sort.SortReuse);
        Assert.AreSame(first.Sort.RowsView, second.Sort.RowsView);
    }

    [TestMethod]
    public void InvalidatePendingRequest_DisablesNormalSummaryFreshness()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
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
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(action => action()));
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
        var workspace = new PlaylistWorkspaceViewModel(action => action());
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
        var workspace = new PlaylistWorkspaceViewModel(action => action());
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
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        RegularChartListRequestLease lease = owner.BeginRequest();

        owner.Dispose();
        owner.Dispose();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryBeginRequest(out _));
        Assert.ThrowsException<ObjectDisposedException>(() => owner.BeginRequest());
    }

    [TestMethod]
    public void VirtualOrderPrewarm_AllowsOneRunAndCompletionAllowsNextRun()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease first));
        Assert.IsTrue(owner.IsVirtualOrderPrewarmRunning);
        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));

        first.Dispose();

        Assert.IsFalse(owner.IsVirtualOrderPrewarmRunning);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease second));
        Assert.AreEqual(first.RunId + 1, second.RunId);
        second.Dispose();
        owner.Dispose();
    }

    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualOrderPrewarm()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        Task stopTask = owner.StopAsync();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(stopTask.IsCompleted);
        lease.Dispose();
        await stopTask;
        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));
        Assert.IsFalse(owner.TryBeginRequest(out _));
    }

    [TestMethod]
    public void SortKeyInvalidation_CancelsVirtualOrderPrewarm()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        owner.InvalidateIdentitySortKeys(clearSourceRows: false);

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        lease.Dispose();
        owner.Dispose();
    }

    [TestMethod]
    public void ClearSortCache_CancelsPrewarmAndRejectsItsLatePublication()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey staleKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        owner.ClearSortCache();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryPublishVirtualOrder(
            staleKey,
            CreateOrder(CreateSourceRow("Folder A", "a.bms")),
            versions));
        lease.Dispose();
        owner.Dispose();
    }

    [TestMethod]
    public async Task StopAsync_RejectsLateVirtualCachePublication()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(action => action()));
        RegularVirtualSourceRowsLookup sourceLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        var rows = new List<ChartListSourceRow> { CreateSourceRow("Folder A", "a.bms") };
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey orderKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);

        await owner.StopAsync();

        owner.TryPublishVirtualSourceRows(sourceLookup, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);
        Assert.IsFalse(owner.TryPublishVirtualOrder(orderKey, CreateOrder(rows[0]), versions));
        Assert.IsFalse(owner.IsCurrentVirtualGeneration(orderKey.SourceGeneration, orderKey.SortKeyGeneration));
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
        var workspace = new PlaylistWorkspaceViewModel(action => action());
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

    [TestMethod]
    public void ColumnSettingOwner_LoadCommitAndReuseOwnsMainAndWorkspacePresentation()
    {
        CustomTableColumnSettings previousStandard = Settings.Default.StandardCustomTableColumnSettings;
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            var table = new MainChartListViewModel();
            var workspace = new PlaylistWorkspaceViewModel(action => action());
            RegularChartListOwner owner = CreateOwner(table, workspace);
            Settings.Default.StandardCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();

            MainChartListColumnSelection first = owner.ResolveColumnSettingForViewUpdate(
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected);
            owner.CommitColumnSetting(first);
            table.ColumnsSettings = first.ColumnsSettings;
            MainChartListColumnSelection second = owner.ResolveColumnSettingForViewUpdate(
                MainViewUpdateMode.SortUpdated,
                MainViewUpdateMode.FolderFilterSelected);

            Assert.IsFalse(first.Reused);
            Assert.IsTrue(second.Reused);
            Assert.AreSame(table.ColumnsSettings, second.ColumnsSettings);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);

            CustomTableColumnSettings beforeInit = Settings.Default.StandardCustomTableColumnSettings;
            owner.LoadAndCommitColumnSetting(MainViewUpdateMode.FolderFilterSelected);

            Assert.AreNotSame(beforeInit, Settings.Default.StandardCustomTableColumnSettings);
            Assert.AreSame(Settings.Default.StandardCustomTableColumnSettings, table.ColumnsSettings);
        }
        finally
        {
            Settings.Default.StandardCustomTableColumnSettings = previousStandard;
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryColumnSettingsCoordinator_ResetCreatesDefaultSettingsAndPublishesWorkspace()
    {
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            var workspace = new PlaylistWorkspaceViewModel(action => action());
            var oldSettings = new PlaylistSummaryColumnSettings();
            Settings.Default.PlaylistSummaryColumnsSettings = oldSettings;
            workspace.PlaylistSummaryColumnsSettings = oldSettings;
            int notificationCount = 0;
            workspace.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
                {
                    notificationCount++;
                }
            };

            var coordinator = new PlaylistSummaryColumnSettingsCoordinator(workspace);
            coordinator.ResetToDefault();

            Assert.AreNotSame(oldSettings, Settings.Default.PlaylistSummaryColumnsSettings);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            Assert.IsTrue(notificationCount > 0);
        }
        finally
        {
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
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

    private static RegularChartListEntryRequest CreateEntryRequest(MainViewUpdateMode mode)
    {
        return new RegularChartListEntryRequest
        {
            Mode = mode,
            RequestedMode = mode,
            CurrentTreeMode = mode,
            IncludeBmsonRows = false,
            Stopwatch = Stopwatch.StartNew()
        };
    }

    private static ChartListSourceRow CreateSourceRow(string folder, string fileName, int? mode = null)
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
            mode: mode,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);
        return ChartListSourceRow.FromChartFile(chart);
    }

    private static PlaylistDetailSourceRow CreatePlaylistSourceRow(string md5)
    {
        ChartFile chart = CreateSourceRow("Playlist", md5).Chart;
        return new PlaylistDetailSourceRow(
            new BMSTableEntry(chart),
            chart);
    }

    private static ChartListOrder CreateOrder(params ChartListSourceRow[] sourceRows)
    {
        Assert.IsTrue(ChartListOrder.TryCreate(
            sourceRows,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            out ChartListOrder order));
        return order;
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
            RegularChartModeFilter.All,
            default);
        return owner.Build(lease, request, new RegularChartListBuildInput
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            SortCacheGeneration = new NormalLibrarySortCacheGenerationSnapshot(1, 1, 0, 0, 0, 0, 0, 0),
            Stopwatch = Stopwatch.StartNew()
        });
    }

    private static RegularMaterializedChartListApplyRequest CreateMaterializedApplyRequest(
        IEnumerable<LibraryChartRow> rows)
    {
        var refresh = new RegularChartListRefreshRequest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: false,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            RegularChartModeFilter.All,
            ChartListSortSpecification.Create(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, hasValue: true));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return new RegularMaterializedChartListApplyRequest
        {
            RefreshRequest = refresh,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FolderFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.SortUpdated,
            Stopwatch = Stopwatch.StartNew()
        };
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
