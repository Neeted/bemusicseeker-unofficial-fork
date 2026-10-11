using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>通常一覧の要求から構築・確定、仮想cacheの採用と失効までをWindowなしで検証します。</summary>
/// <remarks>実FSと健全性索引は警告準備の接続に使い、表示操作はMainWindowが担当します。cacheキーの構築値でなく実ownerの公開・拒否・退役を観測します。</remarks>
[TestClass]
public sealed class RegularChartViewBuildAndOrderingTests
{

    /// <summary>行準備中の入れ子要求だけを確定し、最新detail退役receiptでsourceを消して通常modeを通知することで、外側の旧結果採用と早すぎるsource破棄を検出します。</summary>
    [TestMethod]
    public void ApplyRegularView_NestedNewerRequestKeepsLatestRetirementReceipt()
    {
        var table = new MainChartListViewModel();
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            logs.Add,
            logs.Add,
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
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
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
        workspace.IsPlaylistDetailViewActive = true;
        using var owner = new RegularChartListOwner(
            table,
            workspace,
            logs.Add,
            action => action(),
            logs.Add,
            CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpChartMutationPlaybackPort(),
            new TestUiScheduler(() => null!));
        int? sourceClearVersionAtRowsNotification = null;
        bool? detailActiveAtRowsNotification = null;
        bool sourceWasRetainedAtPreparation = false;
        bool nestedStarted = false;
        RegularChartListEntryResult nestedResult = default;
        table.RowsReplacing += (_, _) =>
        {
            sourceWasRetainedAtPreparation =
                ReferenceEquals(sourceRows, viewState.Source.Rows)
                && ReferenceEquals(viewRows, viewState.View.Rows);
            if (!nestedStarted)
            {
                nestedStarted = true;
                nestedResult = owner.ApplyRegularView(
                    CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));
            }
        };
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                if (viewState.Source.Rows.Count == 0)
                {
                    sourceClearVersionAtRowsNotification = buildState.RequestVersion;
                }
                detailActiveAtRowsNotification = workspace.IsPlaylistDetailViewActive;
            }
        };

        RegularChartListEntryResult result = owner.ApplyRegularView(CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));

        Assert.IsFalse(result.WasCommitted);
        Assert.IsTrue(nestedResult.WasCommitted);
        Assert.IsTrue(sourceWasRetainedAtPreparation);
        Assert.AreEqual(3, buildState.RequestVersion);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
        Assert.IsNull(buildState.PendingRequest);
        Assert.IsNull(buildState.CurrentBuildRequest);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, table.LastAppliedColumnMode);
        Assert.AreEqual(3, sourceClearVersionAtRowsNotification);
        Assert.AreEqual(false, detailActiveAtRowsNotification);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.IsTrue(logs.Any(log => log.Contains("playlist_source_replace action=clear")));
    }


    /// <summary>退役ログが失敗しても通常mode公開を先に終えsourceを回収し、診断失敗で画面modeや退役を止める誤りを検出します。</summary>
    [TestMethod]
    public void ApplyRegularView_RetentionLogFailureStillPublishesRegularModeBeforeRetirement()
    {
        var table = new MainChartListViewModel();
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var sourceRows = new List<PlaylistDetailSourceRow>
        {
            CreatePlaylistSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        };
        var viewState = new PlaylistDetailViewState();
        viewState.Source.Rows = sourceRows;
        viewState.View.Rows = new List<object> { new PlaylistDetailRow(sourceRows[0]) };
        var publicationOrder = new List<string>();
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(
            table,
            buildState,
            viewState,
            _ =>
            {
                publicationOrder.Add("retirement-log");
                throw new InvalidOperationException("retirement log failed");
            });
        workspace.IsPlaylistDetailViewActive = true;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistDetailViewActive))
            {
                publicationOrder.Add("mode");
            }
        };
        using RegularChartListOwner owner = CreateOwner(table, workspace);

        Assert.ThrowsException<RegularChartListTerminalPublishException>(
            () => owner.ApplyRegularView(CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected)));

        CollectionAssert.AreEqual(
            new[] { "mode", "retirement-log", "retirement-log" },
            publicationOrder);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
    }


    /// <summary>通常画面の準備で旧要求を失効させ、library未接続の新経路は空行と新summaryを一回確定して、旧候補や旧件数の残留を検出します。</summary>
    [TestMethod]
    public void ApplyMainLibraryView_DelegatesMainLibraryRouteToRegularPipeline()
    {
        var seededRows = new List<object>
        {
            LibraryChartRow.FromChartFile(CreateSourceRow("Seed", "seed.bms").Chart)
        };
        var table = new MainChartListViewModel { Rows = seededRows };
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        var route = new ChartListRefreshRoute(
            ChartListRefreshRouteKind.ContinueMainLibrary,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            isPlaylistTreeActive: false,
            includeBmsonRows: false);
        table.UpdateSummaryText(0, 0);
        string expectedSummary = table.SummaryText;
        table.UpdateSummaryText(9, 3);
        workspace.SetPlaylistSummaryMode(enabled: true);
        workspace.RequestPlaylistSummaryMode(enabled: false);
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        RegularChartListBuildResult staleBuild = Build(
            owner,
            staleLease,
            [LibraryChartRow.FromChartFile(CreateSourceRow("Stale", "stale.bms").Chart)]);
        int rowsPropertyNotificationCount = 0;
        table.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                rowsPropertyNotificationCount++;
            }
        };

        owner.PrepareForMainViewRefresh();

        RegularChartListTerminalResult staleCommit = owner.TryCommit(
            staleLease,
            CreateTerminalInput(staleBuild));

        Assert.IsFalse(staleCommit.WasCommitted);
        Assert.AreSame(seededRows, table.Rows);

        RegularChartListEntryResult result = owner.ApplyMainLibraryView(
            route,
            library: null,
            parameter: null,
            treeParameter: null,
            preserveSummary: workspace.IsPlaylistSummaryModeRequested,
            Stopwatch.StartNew());

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.DefaultVirtual, result.Route);
        Assert.IsFalse(result.SortWasReset);
        Assert.AreEqual(1, rowsPropertyNotificationCount);
        Assert.AreNotSame(seededRows, table.Rows);
        Assert.AreEqual(0, table.Rows.Count);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, table.LastAppliedColumnMode);
        Assert.AreEqual(expectedSummary, table.SummaryText);
    }


    /// <summary>未構築の健全性索引を実譜面から用意して欠損警告付き仮想行を公開し、索引準備前の警告なし表示を検出します。</summary>
    [TestMethod]
    public void ApplyMainLibraryView_PreparesColdResourceHealthBeforeVirtualRows()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "resource-health-warning.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Resource health warning\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = 1, wav_files_existing = 0 }));
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new PlaylistSummaryAggregationTestSupport.RecordingDialogService());
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [file]);
            Assert.IsTrue(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));

            var table = new MainChartListViewModel();
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
            using RegularChartListOwner owner = CreateOwner(table, workspace);
            var route = new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: false);

            RegularChartListEntryResult result = owner.ApplyMainLibraryView(
                route,
                library,
                parameter: null,
                treeParameter: null,
                preserveSummary: false,
                Stopwatch.StartNew());

            Assert.IsTrue(result.WasCommitted);
            Assert.IsFalse(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));
            Assert.AreEqual(1, table.Rows.Count);
            var row = (LibraryChartRow)table.Rows[0]!;
            StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
        });
    }


    /// <summary>実WAV欠損と健全譜面を最新健全性で警告順へ整列し、同じ再要求はsource・order・健全性snapshotを再利用して古い警告順と再構築を検出します。</summary>
    [TestMethod]
    public void ApplyMainLibraryView_PreparesResourceHealthBeforeWarningSortAndReusesRows()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string missingPath = Path.Combine(root, "warning-sort-missing.bms");
            string healthyPath = Path.Combine(root, "warning-sort-healthy.bms");
            File.WriteAllText(
                missingPath,
                "#PLAYER 1\r\n#TITLE Zeta healthy-name\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            File.WriteAllText(
                healthyPath,
                "#PLAYER 1\r\n#TITLE Alpha warning-name\r\n#WAV01 present.wav\r\n#00111:01\r\n");
            File.WriteAllBytes(Path.Combine(root, "present.wav"), [1, 2, 3]);
            ChartFile missing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(missingPath));
            missing = ChartFileProjection.WithMaintenance(missing, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = missing.Path, hash = missing.Md5, wav_files_defined = 1, wav_files_existing = 0 }));
            ChartFile healthy = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(healthyPath));
            healthy = ChartFileProjection.WithMaintenance(healthy, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = healthy.Path, hash = healthy.Md5, wav_files_defined = 1, wav_files_existing = 1 }));
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new PlaylistSummaryAggregationTestSupport.RecordingDialogService());
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [missing, healthy]);
            Assert.IsTrue(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));

            var table = new MainChartListViewModel();
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
            using RegularChartListOwner owner = CreateOwner(table, workspace);
            owner.QueueSort(new MainChartListSortRequestedEventArgs(
                nameof(LibraryChartRow.WarningDigestText),
                ListSortDirection.Descending,
                MainChartListSortTarget.Regular));
            var route = new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: false);

            RegularChartListEntryResult first = owner.ApplyMainLibraryView(
                route,
                library,
                parameter: null,
                treeParameter: null,
                preserveSummary: false,
                Stopwatch.StartNew());

            Assert.IsTrue(first.WasCommitted);
            Assert.IsFalse(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));
            Assert.AreEqual(2, table.Rows.Count);
            var firstRow = (LibraryChartRow)table.Rows[0]!;
            var secondRow = (LibraryChartRow)table.Rows[1]!;
            Assert.IsTrue(firstRow.WarningDigestText.Length > secondRow.WarningDigestText.Length);
            StringAssert.Contains(firstRow.WarningDigestText, Resources.WarningDigest_ResourceMissing);
            Assert.IsFalse(secondRow.WarningDigestText.Contains(
                Resources.WarningDigest_ResourceMissing,
                StringComparison.Ordinal));
            ResourceHealthIndexSnapshot firstSnapshot =
                library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.IsTrue(firstSnapshot.GetProjection(
                (missing)).HasIssues);

            var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
            RegularVirtualNormalLibraryApplyResult second = owner.TryApplyVirtualNormalLibrary(new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                IncludeBmsonRows = false,
                TreeFilter = null,
                KeywordFilter = string.Empty,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.WarningDigestText),
                SortDirection = ListSortDirection.Descending,
                ExternalVersions = new RegularChartListExternalVersions(
                    library.ScoreSnapshotVersion,
                    library.ChartInfoIndexVersion,
                    library.MaintenanceHydrationCompletedVersion),
                ColumnSelection = new MainChartListColumnSelection(
                    settings,
                    reused: false,
                    elapsedMs: 0L,
                    MainViewUpdateMode.FolderFilterSelected,
                    Visibility.Collapsed,
                    new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FolderFilterSelected,
                Stopwatch = Stopwatch.StartNew(),
                RetireDetailSource = true,
                Reason = "warning_sort_cached"
            });

            Assert.IsTrue(second.WasCommitted);
            Assert.IsTrue(second.SourceRowsCacheHit);
            Assert.IsTrue(second.SortCacheHit);
            Assert.AreSame(second.RowsView, table.Rows);
            Assert.AreSame(firstSnapshot, library.TryGetCurrentResourceHealthIndexSnapshotForView());
            StringAssert.Contains(((LibraryChartRow)table.Rows[0]!).WarningDigestText, Resources.WarningDigest_ResourceMissing);
        });
    }


    /// <summary>保留一覧の実体行sortを適用しても保留scopeと操作sectionを保持し、sort要求を通常library操作へ誤分類する誤りを検出します。</summary>
    [TestMethod]
    public void TryApplyMaterialized_TransientSortRetainsResolvedPendingOperationContext()
    {
        var table = new MainChartListViewModel();
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(table, workspace);

        RegularMaterializedChartListApplyResult result = owner.TryApplyMaterialized(
            CreateMaterializedApplyRequest(
                [LibraryChartRow.FromChartFile(CreateSourceRow("Pending", "Alpha").Chart)],
                MainViewUpdateMode.PendingInstallFolderSelected));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(
            MainViewOperationSection.InstallPending,
            table.CurrentOperationContext.OperationSection);
        Assert.AreEqual(
            ChartOperationSourceScope.PendingPackage,
            table.CurrentOperationContext.SourceScope);
    }


    /// <summary>履歴用routeを通常一覧入口へ渡すと引数例外にし、別画面のrouteを通常一覧として実行する誤りを検出します。</summary>
    [TestMethod]
    public void ApplyMainLibraryView_RejectsNonMainLibraryRoute()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
        var route = new ChartListRefreshRoute(
            ChartListRefreshRouteKind.ApplyPlayHistoryView,
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            isPlaylistTreeActive: false,
            includeBmsonRows: false);

        Assert.ThrowsException<ArgumentException>(() => owner.ApplyMainLibraryView(
            route,
            library: null,
            parameter: null,
            treeParameter: null,
            preserveSummary: false,
            Stopwatch.StartNew()));
    }


    /// <summary>逆順の実体行をownerで構築・sort・終端確定し、Alpha/Beta順と絞込み件数を反映して、途中結果だけを返す誤りを検出します。</summary>
    [TestMethod]
    public void MaterializedApply_OwnsBuildAndTerminalPipeline()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
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
            ChartModeFilter.All,
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
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);
        Assert.AreEqual("Beta", ((LibraryChartRow)table.Rows[1]!).Title);
        Assert.AreEqual(2, result.FolderCount);
        Assert.AreEqual(2, result.ModeCount);
    }


    /// <summary>実体行の準備中に入れ子要求を確定すると外側の旧要求を拒否し、最新行だけを表示して後着旧結果の上書きを検出します。</summary>
    [TestMethod]
    public void MaterializedApply_NestedNewerRequestRejectsOuterTerminal()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
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
        Assert.AreEqual("nested.bms", ((LibraryChartRow)table.Rows[0]!).Title);
    }


    /// <summary>同tokenの仮想行を再利用し、削除token通知でowner cacheから退役させて、二重生成と削除済み行の保持を検出します。</summary>
    [TestMethod]
    public void VirtualRowCache_ReusesAndRemovesBmsOwnerRowsThroughRegularOwner()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"C:\Charts\Owner\chart.bms",
        };
        ChartFile chart = file with { Token = new OwnedChartToken() };
        var sourceRow = ChartListSourceRow.FromChartFile(chart);

        LibraryChartRow first = owner.CreateVirtualRow(null, sourceRow);
        LibraryChartRow second = owner.CreateVirtualRow(null, sourceRow);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, owner.SnapshotRows().Count);
        owner.ApplyNormalLibraryChartChanges(null, new NormalLibraryRefreshNotificationBatch(1, 1,
            LibraryChartRefreshEffects.SourceChanged, [], false, deletedTokens: [chart.Token]));
        Assert.AreEqual(0, owner.SnapshotRows().Count);
    }


    /// <summary>未版指定の変更と新しい正本版はsourceを失効し、同じ正本版の重複は失効しないことで、通知重複による再構築を検出します。</summary>
    [TestMethod]
    public void SourceInvalidation_ConsumesEachPositiveOwnedCollectionVersionOnce()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());

        Assert.IsTrue(owner.TryInvalidateSourceForOwnedCollectionVersion(0, out _));
        Assert.AreEqual(1L, owner.SourceGeneration);
        Assert.IsTrue(owner.TryInvalidateSourceForOwnedCollectionVersion(2, out _));
        Assert.AreEqual(2L, owner.SourceGeneration);
        Assert.IsFalse(owner.TryInvalidateSourceForOwnedCollectionVersion(2, out _));
        Assert.AreEqual(2L, owner.SourceGeneration);
    }


    /// <summary>sourceまたはidentity sortを失効した後の旧lookup公開は拒否し、現在lookupだけ同じsourceを再利用して、古いcacheの復活を検出します。</summary>
    [TestMethod]
    public void VirtualSourceRows_StaleLookupCannotRepopulateInvalidatedCache()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
        RegularVirtualSourceRowsLookup staleLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        var rows = new List<ChartListSourceRow> { CreateSourceRow("Folder A", "a.bms") };

        owner.InvalidateSource();
        owner.TryPublishVirtualSourceRows(staleLookup, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);

        RegularVirtualSourceRowsLookup currentLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(currentLookup, rows);
        RegularVirtualSourceRowsLookup cached = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        Assert.IsTrue(cached.CacheHit);
        CollectionAssert.AreEqual(rows, cached.Rows.ToList());
        Assert.AreSame(cached.Rows, owner.LookupVirtualSourceRows(null, includeBmsonRows: false).Rows);

        RegularVirtualSourceRowsLookup staleAfterSortChange = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.InvalidateIdentitySortKeys(clearSourceRows: true);
        owner.TryPublishVirtualSourceRows(staleAfterSortChange, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);
    }


    /// <summary>libraryを替えて予備計算を開始するとsourceとorderを失効して前library要求を取消し、別libraryへ旧orderを使う誤りを検出します。</summary>
    [TestMethod]
    public void VirtualSourceRows_LibraryIdentityChangeInvalidatesDerivedOrders()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
        var firstLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
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


    /// <summary>二つのlibraryが初期cache missを競合すると後のlibraryだけのsourceを採用し、前libraryの遅い公開による混入を検出します。</summary>
    [TestMethod]
    public void VirtualSourceRows_ConcurrentInitialLibraryMissRejectsStalePublisher()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
        var firstLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
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
        CollectionAssert.AreEqual(secondRows, cached.Rows.ToList());
        Assert.AreSame(cached.Rows, owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false).Rows);
        secondLease.Dispose();
    }


    /// <summary>別libraryへの予備計算要求は実行中leaseを取消し、同時に別予備計算を受理しないことで異なるlibraryの並行公開を検出します。</summary>
    [TestMethod]
    public void VirtualOrderPrewarm_LibrarySwitchCancelsRunningDifferentLibraryLease()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
        var firstLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(firstLibrary, out RegularChartListPrewarmLease firstLease));

        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out _));

        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        firstLease.Dispose();
    }


    /// <summary>既存sourceのタイトル予備sortを実行してa/bのorderをowner cacheへ公開し、lease完了も返して、計算漏れと終端未回収を検出します。</summary>
    [TestMethod]
    public void VirtualOrderPrewarm_BuildsOwnedOrderAndCompletesLease()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
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


    /// <summary>通常一覧のfolder絞込みとtitle降順を終端へ確定し、同条件の次要求はsourceとorderを再利用して、絞込み欠けと不要再計算を検出します。</summary>
    [TestMethod]
    public void VirtualNormalLibraryApply_OwnsSourceOrderFilterAndTerminalReuse()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
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
            ModeFilter = ChartModeFilter.All,
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


    /// <summary>欠損部分一覧をtitle降順と異なるfolder数で確定し、同条件の次要求はorderを再利用して、部分集合の順やsummaryの誤りを検出します。</summary>
    [TestMethod]
    public void VirtualChartSubsetApply_OwnsProjectionOrderFilterAndTerminalReuse()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
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
            ModeFilter = ChartModeFilter.All,
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


    /// <summary>通常一覧入口の非対応sortを解除し、タイトル昇順の仮想経路へ確定して、非対応要求の残留と行欠けを検出します。</summary>
    [TestMethod]
    public void RegularEntry_SelectsDefaultVirtualAndResetsUnsupportedSort()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            "UnsupportedColumn",
            ListSortDirection.Descending,
            MainChartListSortTarget.Regular));

        RegularChartListEntryResult result = owner.ApplyMainLibraryView(
            new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: false),
            library: null,
            parameter: null,
            treeParameter: null,
            preserveSummary: false,
            Stopwatch.StartNew());

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.DefaultVirtual, result.Route);
        Assert.IsTrue(result.SortWasReset);
        Assert.IsNull(owner.CaptureSortParameters());
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);
        Assert.AreEqual("Bravo", ((LibraryChartRow)table.Rows[1]!).Title);
    }


    /// <summary>owner入口へ渡すkeywordで仮想sourceを絞り、該当Alphaだけを確定して、検索条件を無視する誤りを検出します。</summary>
    [TestMethod]
    public void RegularEntry_AppliesOwnerFilterState()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, "Alpha", ChartModeFilter.All));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);
    }


    /// <summary>履歴列modeの確定は保留中の通常一覧要求を取消し、別画面への旧結果公開を検出します。</summary>
    [TestMethod]
    public void PlayHistoryColumnModeCommit_CancelsPendingRegularRequest()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
        RegularChartListRequestLease pending = owner.BeginRequest();

        table.CommitAppliedColumnMode(MainViewUpdateMode.PlayHistorySelected);

        Assert.IsTrue(pending.Token.IsCancellationRequested);
        owner.InvalidatePendingRequest();
    }


    /// <summary>owner入口の7keys条件で9keysを除外してSevenだけ確定し、mode条件の適用漏れを検出します。</summary>
    [TestMethod]
    public void RegularEntry_AppliesOwnerModeFilterState()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder A", "Seven", mode: 7), CreateSourceRow("Folder B", "Nine", mode: 9)]);
        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, string.Empty, ChartModeFilter._7KEYS));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Seven", ((LibraryChartRow)table.Rows[0]!).Title);
    }


    /// <summary>libraryの重複groupを通常入口から部分仮想経路へ配送し、二行を保持して別sourceの選択や行欠けを検出します。</summary>
    [TestMethod]
    public void RegularEntry_SelectsDuplicateSubsetFromLibraryOwner()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
        ChartFile bravo = CreateSourceRow("Folder B", "Bravo").Chart;
        ChartFile alpha = CreateSourceRow("Folder A", "Alpha").Chart;
        var library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        typeof(BMSLibrary)
            .GetField("catalogChartInfoOwner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(library, new CatalogChartInfoOwner(_ => { }, () => false, (_, _) => false, () => null, _ => { }));
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


    /// <summary>重複groupの特定folderだけを捕捉し、元groupを後で空にしても一致譜面を保持して、別folder混入とcaller可変入力への依存を検出します。</summary>
    [TestMethod]
    public void RegularEntry_DuplicateFolderContextSelectsOnlyMatchingCharts()
    {
        ChartFile matching = CreateSourceRow("Folder A", "Alpha").Chart;
        ChartFile other = CreateSourceRow("Folder B", "Bravo").Chart;
        DuplicateGroup[] groups = new[]
        {
            new DuplicateGroup([matching, other], [matching.Folder, other.Folder]) { Header = "Group" }
        };

        Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource(
            groups,
            DuplicateViewContext.ForFolder(System.IO.Path.Combine(@"C:\Charts", "Folder A")),
            out RegularChartListSubsetSource source));
        groups[0].ChartFiles.Clear();

        CollectionAssert.AreEqual(new[] { matching }, source.SourceCharts.ToArray());
    }


    /// <summary>score依存の通常と部分orderを公開後、score失効で両方とも退役させ、一方の旧orderだけが残る誤りを検出します。</summary>
    [TestMethod]
    public void DependencyInvalidation_PrunesDefaultAndSubsetOrderCachesTogether()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
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


    /// <summary>計算中にscore版が変わった通常orderは公開もcache採用もしないことで、古い得点順の復活を検出します。</summary>
    [TestMethod]
    public void VirtualOrder_ExternalVersionChangeRejectsStalePublish()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
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


    /// <summary>計算中に解析版が変わった部分orderは公開もcache採用もしないことで、古いnotes順の復活を検出します。</summary>
    [TestMethod]
    public void VirtualSubsetOrder_ExternalVersionChangeRejectsStalePublish()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
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


    /// <summary>警告orderは導入先変更と健全性変更のどちらでもcacheを退役し、警告の依存関係不足による旧順残留を検出します。</summary>
    [TestMethod]
    public void WarningOrderCache_IsPrunedByInstallDestinationAndMaintenanceChanges()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
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
}
