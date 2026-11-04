using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧の要求から構築・確定、仮想cacheの採用と失効までをWindowなしで検証します。</summary>
/// <remarks>実FSと健全性索引は警告準備の接続に使い、表示操作はMainWindowが担当します。cacheキーの構築値でなく実ownerの公開・拒否・退役を観測します。</remarks>
[TestClass]
public sealed class RegularChartViewBuildAndOrderingTests
{
    /// <summary>専用subsetも最初のscore投影前のbを固定し、非表示元行を含む変更rangeを実配送後に局所反映してから採用します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VirtualSubset_ScoreBaselineCoversOriginalRangeBeforeFilter(bool filtered)
    {
        WithTemporarySongDb(path => RegularChartRefreshViewTestFixture.VerifyScoreCandidateAsync(path, filtered, subset: true).GetAwaiter().GetResult());
    }


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
            PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceFixturePorts.PlaylistStoreProvider,
            PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck);
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

        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));

        Assert.IsTrue(result.WasCommitted, "任意の退役ログ失敗で実適用を失敗へ変換しません。");
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

        Assert.ThrowsExactly<ArgumentException>(() => owner.ApplyMainLibraryView(
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

    /// <summary>通知を消費しなくても同件数の別所属・別順序を現在カタログから再現し、古い添字・検索・モード件数と別library入力の流用を検出します。</summary>
    [TestMethod]
    public void CatalogInput_ExplicitRefreshUsesSameCountReplacementAndCurrentFilters()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(path =>
        {
            var library = new TestBmsLibrary(databaseScope, path);
            CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
            ChartFile a = CreateSourceRow("Old", "A", 7).Chart;
            ChartFile b = CreateSourceRow("Old", "B", 14).Chart;
            catalog.ReplaceCharts([a, b], []);
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected);
            request.Library = library;
            Assert.IsTrue(owner.ApplyRegularView(request).WasCommitted);
            CollectionAssert.AreEqual(new[] { "A", "B" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            ChartFile c = CreateSourceRow("New", "C", 14).Chart;
            catalog.ReplaceCharts([c, a with { Path = @"C:\Charts\New\A", Folder = "New" }], []);
            request = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, "New", ChartModeFilter._7KEYS);
            request.Library = library;
            Assert.IsTrue(owner.ApplyRegularView(request).WasCommitted);
            Assert.AreEqual(1, table.Rows.Count);
            Assert.AreEqual(@"C:\Charts\New\A", (table.Rows[0] as LibraryChartRow ?? throw new AssertFailedException()).path);
            request = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected);
            request.Library = library;
            Assert.IsTrue(owner.ApplyRegularView(request).WasCommitted);
            CollectionAssert.AreEqual(new[] { "A", "C" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            var otherLibrary = new TestBmsLibrary(databaseScope, path);
            CatalogOwnedCollectionOwner otherCatalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(otherLibrary);
            otherCatalog.ReplaceCharts([CreateSourceRow("Other", "Other").Chart, CreateSourceRow("Other", "ZOther").Chart], []);
            request.Library = otherLibrary;
            Assert.IsTrue(owner.ApplyRegularView(request).WasCommitted);
            Assert.AreEqual(2, table.Rows.Count);
            Assert.AreEqual("Other", (table.Rows[0] as LibraryChartRow ?? throw new AssertFailedException()).Title);
            request.Library = library;
            Assert.IsTrue(owner.ApplyRegularView(request).WasCommitted);
            CollectionAssert.AreEqual(new[] { "A", "C" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
        });
    }

    /// <summary>同値BMSONの初回と更新後を捕捉順で表示し、path事前整列と古い候補から現在行を変更する誤りを検出します。</summary>
    [TestMethod]
    public void CatalogInput_BmsonTiesKeepCapturedOrderAndStaleCandidateCannotOverwriteRows()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(path =>
        {
            var library = new TestBmsLibrary(databaseScope, path);
            CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
            ChartFile z = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"C:\z.bmson", Md5 = "z", Title = "Same", Token = new OwnedChartToken() };
            ChartFile a = z with { Path = @"C:\a.bmson", Md5 = "a", Token = new OwnedChartToken() };
            catalog.ReplaceCharts([], [z, a]);
            var table = new MainChartListViewModel();
            using var scheduler = new ScoreDeliveryUiScheduler();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(), action => action(),
                scheduler);
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                Task initial = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                ScheduledScoreOperation initialOperation = scheduler.NextAsync().GetAwaiter().GetResult();
                initialOperation.Run(); initialOperation.Complete();
                initial.GetAwaiter().GetResult();
                RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected);
                request.Library = library;
                request.IncludeBmsonRows = true;
                Assert.IsTrue(owner.ApplyRegularView(request).WasCommitted);
                var previousRows = (ChartListVirtualView)table.Rows;
                Assert.AreEqual(z.Path, ((LibraryChartRow)previousRows[0]).path);
                CatalogChartCollectionSnapshot snapshot = library.CaptureNormalLibraryChartSnapshot();
                catalog.CommitCurrentValues([snapshot.BmsonRows[1]], (currentValue, _) => currentValue with { Title = "New" });
                owner.QueueLatestNormalLibraryRefreshNotification("bmson_current_commit", library);
                Task delivery = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                ScheduledScoreOperation operation = scheduler.NextAsync().GetAwaiter().GetResult();
                operation.Run(); operation.Complete();
                delivery.GetAwaiter().GetResult();
                LibraryChartRow current = table.Rows[0] as LibraryChartRow ?? throw new AssertFailedException();
                Assert.AreEqual("New", current.Title);
                Assert.AreEqual("Same", snapshot.BmsonRows[1].Title);
                Assert.AreEqual("New", current.Title);
                bool changed = false;
                table.RowsReplacing += (_, _) =>
                {
                    if (changed) { return; }
                    changed = true;
                    ChartFile target = library.CaptureNormalLibraryChartSnapshot().BmsonRows[0];
                    catalog.CommitCurrentValues([target], (currentValue, _) => currentValue with { Title = "Later" });
                    owner.QueueLatestNormalLibraryRefreshNotification("bmson_terminal_commit", library);
                };
                Assert.IsFalse(owner.ApplyRegularView(request).WasCommitted);
                Assert.AreSame(current, table.Rows[0]);
                Task nextDelivery = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                ScheduledScoreOperation nextOperation = scheduler.NextAsync().GetAwaiter().GetResult();
                nextOperation.Run(); nextOperation.Complete();
                nextDelivery.GetAwaiter().GetResult();
                ScheduledScoreOperation continuation = scheduler.NextAsync().GetAwaiter().GetResult();
                continuation.Run(); continuation.Complete();
                Assert.AreEqual("Later", (table.Rows[0] as LibraryChartRow ?? throw new AssertFailedException()).Title);
            }
            finally
            {
                Task stop = owner.StopAsync();
                scheduler.AbortOutstanding();
                while (scheduler.TryTake(out ScheduledScoreOperation? pending)) { pending.Run(); pending.Complete(); }
                stop.GetAwaiter().GetResult();
                library.RequestShutdown("bmson_ties_test");
            }
        });
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
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            void SeedCatalog(IEnumerable<ChartListSourceRow> rows)
            {
                CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
                catalog.ReplaceCharts(rows.Select(row => row.Chart), []);
            }

            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            List<ChartListSourceRow> rows =
            [
                CreateSourceRow("Folder A", "b.bms"),
            CreateSourceRow("Folder A", "a.bms")
            ];
            SeedCatalog(rows);
            Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(library, out RegularChartListPrewarmLease lease));

            using (lease)
            {
                owner.RunVirtualOrderPrewarm(
                    lease,
                    library: library,
                    includeBmsonRows: false,
                    [new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 1)],
                    "test");
            }

            NormalLibrarySortCacheKey key = owner.CreateVirtualOrderKey(
                owner.SourceGeneration,
                owner.SortKeyGeneration,
                nameof(LibraryChartRow.Title),
                ListSortDirection.Ascending,
                rows.Count,
                new RegularChartListExternalVersions(0, 0, 0));
            Assert.IsTrue(owner.TryGetVirtualOrder(key, out ChartListOrder order));
            CollectionAssert.AreEqual(new[] { 1, 0 }, order.Indexes.ToArray());
            Assert.IsTrue(lease.Completion.IsCompleted);

        });
    }

    /// <summary>各準備範囲の対象外列を実ownerへ要求し、昇降順の結果・cache登録再利用・カタログ更新後の失効を確認します。0ではsourceもorderも作りません。</summary>
    /// <remarks>小DBと実カタログを使い、比較規則の網羅と取消・並列上限は既存系列へ分担します。</remarks>
    [TestMethod]
    [DataRow(0, "Title")]
    [DataRow(1, "ChartNotes")]
    [DataRow(2, "mode")]
    [DataRow(3, "level")]
    public void VirtualOrderPrewarm_UnpreparedColumnsRemainDemandSortedAndInvalidated(int priority, string column)
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
            ChartFile MakeChart(string title, int value) => new(ChartFileKind.Bms, @"C:\Charts\" + title + ".bms", title, null,
                title, title, "", "", "Folder", "", value.ToString(), value, value,
                new ChartDetails { notes = value });
            catalog.ReplaceCharts([MakeChart("Bravo", 9), MakeChart("Alpha", 7)], []);
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(library, out RegularChartListPrewarmLease lease));
            using (lease)
            {
                owner.RunVirtualOrderPrewarm(lease, library, false, RegularChartListOwner.CreateVirtualOrderPrewarmDescriptors(priority), "test_range");
            }
            Assert.IsTrue(lease.Completion.IsCompleted);
            if (priority == 0) { Assert.AreEqual(0, owner.CacheCount); }
            var request = new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                IncludeBmsonRows = false,
                TreeFilter = RegularNormalLibraryTreeFilter.Create(RegularChartFolderFilterKind.Directory, string.Empty),
                KeywordFilter = string.Empty,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = column,
                SortDirection = ListSortDirection.Ascending,
                ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0L,
                    MainViewUpdateMode.FolderFilterSelected, Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FolderFilterSelected,
                Stopwatch = Stopwatch.StartNew(),
                Reason = "test_range"
            };
            foreach (ListSortDirection direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
            {
                request.SortDirection = direction;
                RegularVirtualNormalLibraryApplyResult first = owner.TryApplyVirtualNormalLibrary(request);
                Assert.IsTrue(first.WasCommitted);
                Assert.IsFalse(first.SortCacheHit);
                if (priority == 0 && direction == ListSortDirection.Ascending) { Assert.IsFalse(first.SourceRowsCacheHit); }
                CollectionAssert.AreEqual(direction == ListSortDirection.Ascending ? new[] { "Alpha", "Bravo" } : new[] { "Bravo", "Alpha" },
                    first.RowsView.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                NormalLibrarySortCacheKey key = owner.CreateVirtualOrderKey(owner.SourceGeneration, owner.SortKeyGeneration, column, direction, 2, request.ExternalVersions);
                Assert.IsTrue(owner.TryGetVirtualOrder(key, out _));
                RegularVirtualNormalLibraryApplyResult reused = owner.TryApplyVirtualNormalLibrary(request);
                Assert.IsTrue(reused.SortCacheHit);
                Assert.IsTrue(reused.SourceRowsCacheHit);
            }
            catalog.ReplaceCharts([MakeChart("Zulu", 6), MakeChart("Charlie", 4)], []);
            RegularVirtualNormalLibraryApplyResult updated = owner.TryApplyVirtualNormalLibrary(request);
            Assert.IsTrue(updated.WasCommitted);
            Assert.IsFalse(updated.SortCacheHit);
            CollectionAssert.AreEqual(new[] { "Zulu", "Charlie" }, updated.RowsView.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
        });
    }

    /// <summary>
    /// WARNING列の整列順キャッシュが、同じ事実の警告の作り直しでは保持され、警告の事実が変わったときだけ失効することを確認します。
    /// 警告を翻訳済み文字列で比較して作り直しのたびに整列を失効させる誤りと、事実の変化で古い順序を使い続ける誤りを検出します。
    /// </summary>
    [TestMethod]
    public void WarningOrderCache_RetainedForRecreatedSameFactsAndPrunedOnWarningFactChange()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(path =>
        {
            var library = new TestBmsLibrary(databaseScope, path);
            CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
            ChartFile Make(string title, params ChartWarning[] warnings) => ChartTestValues.Empty(ChartFileKind.Bms) with
            {
                Path = @"C:\Charts\" + title + ".bms",
                Md5 = title,
                Title = title,
                Folder = "Folder",
                Token = new OwnedChartToken(),
                Warnings = warnings
            };
            ChartFile[] Facts(int health) =>
            [
                Make("A"),
                Make("B", ChartWarning.Create(ChartWarningKind.DuplicateChart)),
                Make("C", ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, health, 1, 4)),
            ];
            catalog.ReplaceCharts(Facts(25), []);
            var table = new MainChartListViewModel();
            using var scheduler = new ScoreDeliveryUiScheduler();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(), action => action(), scheduler);
            void Deliver(string reason)
            {
                owner.QueueLatestNormalLibraryRefreshNotification(reason, library);
                Task delivery = owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                // 配送が必要とする予約済みのUI操作だけを実行し、予約がなければ配送の終端を待ちます。
                var timeout = Stopwatch.StartNew();
                while (!delivery.IsCompleted)
                {
                    if (scheduler.TryTake(out ScheduledScoreOperation? operation)) { operation.Run(); operation.Complete(); }
                    else if (timeout.Elapsed > TimeSpan.FromSeconds(20)) { Assert.Fail("警告の配送が終端しませんでした: " + reason); }
                    else { delivery.Wait(5); }
                }
                delivery.GetAwaiter().GetResult();
            }
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                Deliver("initial");
                var request = new RegularVirtualNormalLibraryApplyRequest
                {
                    Library = library,
                    IncludeBmsonRows = false,
                    TreeFilter = null,
                    KeywordFilter = string.Empty,
                    ModeFilter = ChartModeFilter.All,
                    SortColumnName = nameof(LibraryChartRow.WarningDigestText),
                    SortDirection = ListSortDirection.Ascending,
                    ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
                    ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0L,
                        MainViewUpdateMode.FolderFilterSelected, Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                    Mode = MainViewUpdateMode.FolderFilterSelected,
                    Stopwatch = Stopwatch.StartNew(),
                    Reason = "warning_order_cache"
                };
                Assert.IsFalse(owner.TryApplyVirtualNormalLibrary(request).SortCacheHit, "初回は整列を計算します。");
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(request).SortCacheHit, "同じ要求は整列順を再利用します。");

                // 基本値の変更と、同じ事実の警告の作り直しでは整列順キャッシュを失効させません。
                CatalogChartCollectionSnapshot snapshot = library.CaptureNormalLibraryChartSnapshot();
                ChartFile target = snapshot.BmsRows.Single(row => row.Title == "C");
                catalog.CommitCurrentValues([target], (current, _) => current with { Genre = "renamed" });
                Deliver("title_only");
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(request).SortCacheHit, "警告以外の変更は整列順を失効させません。");
                target = library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(row => row.Title == "C");
                catalog.CommitCurrentValues([target], (current, _) => current with
                {
                    // 基本値の変更と、同じ事実の別インスタンスの警告の作り直しを1つのコミットにまとめます。
                    Genre = "recreated",
                    Warnings = [ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4)]
                });
                Deliver("same_facts");
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(request).SortCacheHit, "同じ事実の作り直しは整列順を失効させません。");

                // 要約(ラベル・種類数)が変わらない事実(件数・割合)だけの変更でも失効し、再計算します。
                target = library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(row => row.Title == "C");
                catalog.CommitCurrentValues([target], (current, _) => current with
                {
                    Warnings = [ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 50, 2, 4)]
                });
                Deliver("fact_changed");
                Assert.IsFalse(owner.TryApplyVirtualNormalLibrary(request).SortCacheHit, "警告の事実が変わると整列順を再計算します。");
            }
            finally
            {
                Task stop = owner.StopAsync();
                scheduler.AbortOutstanding();
                while (scheduler.TryTake(out ScheduledScoreOperation? pending)) { pending.Run(); pending.Complete(); }
                stop.GetAwaiter().GetResult();
                library.RequestShutdown("warning_order_cache_test");
            }
        });
    }

    /// <summary>通常一覧のfolder絞込みとtitle降順を終端へ確定し、同条件の次要求はsourceとorderを再利用して、絞込み欠けと不要再計算を検出します。</summary>
    [TestMethod]
    public void VirtualNormalLibraryApply_OwnsSourceOrderFilterAndTerminalReuse()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            void SeedCatalog(IEnumerable<ChartListSourceRow> rows)
            {
                CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
                catalog.ReplaceCharts(rows.Select(row => row.Chart), []);
            }

            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            List<ChartListSourceRow> sourceRows =
            [
                CreateSourceRow("Folder A", "Alpha"),
            CreateSourceRow("Folder B", "Bravo"),
            CreateSourceRow("Folder A", "Charlie")
            ];
            SeedCatalog(sourceRows);
            var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
            var request = new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
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

        });
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
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            void SeedCatalog(IEnumerable<ChartListSourceRow> rows)
            {
                CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
                catalog.ReplaceCharts(rows.Select(row => row.Chart), []);
            }

            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            SeedCatalog(
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
                library: library,
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

        });
    }

    /// <summary>owner入口へ渡すkeywordで仮想sourceを絞り、該当Alphaだけを確定して、検索条件を無視する誤りを検出します。</summary>
    [TestMethod]
    public void RegularEntry_AppliesOwnerFilterState()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            void SeedCatalog(IEnumerable<ChartListSourceRow> rows)
            {
                CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
                catalog.ReplaceCharts(rows.Select(row => row.Chart), []);
            }

            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            SeedCatalog(
                [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
            RegularChartListEntryRequest entry = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, "Alpha", ChartModeFilter.All);
            entry.Library = library;
            RegularChartListEntryResult result = owner.ApplyRegularView(entry);

            Assert.IsTrue(result.WasCommitted);
            Assert.AreEqual(1, table.Rows.Count);
            Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);

        });
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

    /// <summary>owner入口の24/48条件で5を除外し、選択した2種の確定によってmode条件の適用漏れを検出します。</summary>
    [TestMethod]
    public void RegularEntry_AppliesOwnerModeFilterState()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            void SeedCatalog(IEnumerable<ChartListSourceRow> rows)
            {
                CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
                catalog.ReplaceCharts(rows.Select(row => row.Chart), []);
            }

            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            SeedCatalog(
                [CreateSourceRow("Folder A", "TwentyFour", mode: 24), CreateSourceRow("Folder B", "FortyEight", mode: 48), CreateSourceRow("Folder C", "Five", mode: 5)]);
            RegularChartListEntryRequest entry = CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, string.Empty, ChartModeFilter._24KEYS | ChartModeFilter._48KEYS);
            entry.Library = library;
            RegularChartListEntryResult result = owner.ApplyRegularView(entry);

            Assert.IsTrue(result.WasCommitted);
            CollectionAssert.AreEquivalent(new[] { "TwentyFour", "FortyEight" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());

        });
    }

    /// <summary>同名・空名でも所属が異なる群を実owner入口から区別し、明示rootだけ全件、消失/不明は空一覧にします。行選択scopeも同じ所属から作られます。</summary>
    [TestMethod]
    [DataRow("same")]
    [DataRow("")]
    public void RegularEntry_SelectsDuplicateSubsetFromLibraryOwner(string header)
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
            ChartFile first = CreateSourceRow("First", "first.bms").Chart;
            ChartFile second = CreateSourceRow("Second", "second.bms").Chart;
            ChartFile sibling = CreateSourceRow("Second", "unique.bms").Chart;
            ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"C:\Charts\Second\duplicate.bmson", Md5 = "bmson", Title = "bmson" };
            var library = new TestBmsLibrary(databaseScope, songDbPath) { BmsCharts = [first, second, sibling], BmsonCharts = [bmson] };
            // 検索結果の所属は所持tokenを持つ値です。登録前の入力値をcurrent投影の対象にしません。
            first = library.BmsCharts.Single(chart => chart.Path == first.Path);
            second = library.BmsCharts.Single(chart => chart.Path == second.Path);
            sibling = library.BmsCharts.Single(chart => chart.Path == sibling.Path);
            bmson = library.BmsonCharts.Single();
            var firstGroup = new DuplicateGroup([first], [@"C:\Charts\First"]) { Header = header };
            var secondGroup = new DuplicateGroup([second, sibling, bmson], [@"C:\Charts\Second"]) { Header = header };
            library.DuplicateChartGroups = [firstGroup, secondGroup];
            RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.DuplicateFilterSelected);
            request.Library = library;
            request.IncludeBmsonRows = true;
            ChartListVirtualView Apply(DuplicateViewContext context, params string[] paths)
            {
                request.TreeParameter = context;
                request.Stopwatch = Stopwatch.StartNew();
                RegularChartListEntryResult result = owner.ApplyRegularView(request);
                Assert.IsTrue(result.WasCommitted);
                Assert.AreEqual(RegularChartListEntryRoute.SubsetVirtual, result.Route);
                Assert.IsFalse(result.SortWasReset);
                CollectionAssert.AreEquivalent(paths, table.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray());
                return (ChartListVirtualView)table.Rows;
            }
            ChartListVirtualView selected = Apply(DuplicateViewContext.CaptureGroup(secondGroup), second.Path, sibling.Path, bmson.Path);
            ChartListVirtualView repeat = Apply(DuplicateViewContext.ForGroup(@"c:\charts\second"), second.Path, sibling.Path, bmson.Path);
            Assert.IsTrue(selected.SharesSelectionIdentityScope(repeat));
            ChartListVirtualView other = Apply(DuplicateViewContext.CaptureGroup(firstGroup), first.Path);
            Assert.IsFalse(selected.SharesSelectionIdentityScope(other));
            ChartListVirtualView root = Apply(DuplicateViewContext.ForRoot(), first.Path, second.Path, sibling.Path, bmson.Path);
            Assert.IsFalse(selected.SharesSelectionIdentityScope(root));
            ChartListVirtualView folder = Apply(DuplicateViewContext.ForFolder(@"C:\Charts\Second"), second.Path, sibling.Path, bmson.Path);
            Assert.IsFalse(selected.SharesSelectionIdentityScope(folder));
            var captured = DuplicateViewContext.CaptureGroup(secondGroup);
            library.DuplicateChartGroups = [firstGroup];
            Apply(captured);
            Apply(DuplicateViewContext.ForGroup(string.Empty));
            Apply(DuplicateViewContext.ForGroup(@"C:\Charts\missing"));
        });
    }

    /// <summary>folderは他群を含む子孫を保持し、group所属とは異なる集合になります。同prefix兄弟を除き、元list変更後も返却sourceを固定します。</summary>
    [TestMethod]
    public void RegularEntry_DuplicateFolderContextSelectsOnlyMatchingCharts()
    {
        ChartFile matching = CreateSourceRow("Folder A", "Alpha").Chart;
        ChartFile child = CreateSourceRow(@"Folder A\Child", "Child").Chart;
        ChartFile other = CreateSourceRow("Folder B", "Bravo").Chart;
        ChartFile prefixSibling = CreateSourceRow("Folder AB", "Sentinel").Chart;
        DuplicateGroup[] groups = [new([matching, other], [@"C:\Charts\Folder A", @"C:\Charts\Folder B"]),
            new([child, prefixSibling], [@"C:\Charts\Folder A\Child", @"C:\Charts\Folder AB"])];
        Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource(groups,
            DuplicateViewContext.ForFolder(@"C:\Charts\Folder A"), out RegularChartListSubsetSource source));
        Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource(groups,
            DuplicateViewContext.ForGroup(@"C:\Charts\Folder A"), out RegularChartListSubsetSource groupSource));
        groups[0].ChartFiles.Clear();
        groups[1].ChartFiles.Clear();
        CollectionAssert.AreEqual(new[] { matching, child }, source.SourceCharts.ToArray());
        CollectionAssert.AreEqual(new[] { matching, other }, groupSource.SourceCharts.ToArray());
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
