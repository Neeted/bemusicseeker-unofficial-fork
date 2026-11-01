using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧ownerの画面遷移と編集要求の配送をWindowなしで確認します。</summary>
/// <remarks>実libraryを使う重複caseはgroup構築から通知までを担当し、compiledセル・treeの接続はMainWindowの実画面caseが分担します。</remarks>
[TestClass]
public sealed class RegularChartNavigationTests
{
    /// <summary>実所持編集を通常の再表示へ保持し、実FullReinitialize通知と同DBの新ownerには持ち越しません。resetを直接辞書消去で代替しません。</summary>
    [TestMethod]
    public void InstallDestinationEdit_SurvivesRedisplayButNotFullReinitializeOrNewOwner()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDb =>
        {
            string root = Path.GetDirectoryName(songDb) ?? throw new AssertFailedException();
            string source = Path.Combine(root, "source"), destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            string path = Path.Combine(source, "a.bms"), known = Path.Combine(destination, "known.bms");
            File.WriteAllText(path, "#PLAYER 1\n#TITLE A\n#BPM 120\n"); File.WriteAllText(known, "#PLAYER 1\n#TITLE Known\n#BPM 120\n");
            var scanner = CapturedChartFileScanner.FromFixture([path, known],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [source] = [], [destination] = [] }, [root]);
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true };
            var synchronizer = new ChartFileOperationSynchronizer();
            TestBmsLibrary CreateLibrary(ChartFileOperationSynchronizer admission) => new TestBmsLibrary(databaseScope, songDb, getLR2Config: null, _lr2ScoreDB: null, startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options, applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge, chartFileScanner: scanner,
                operationAdmission: admission, settings: TestBmsFactory.CreateRootSettings([root]))
            { StartupBackgroundTaskScheduler = (_, _, _, _) => false };
            TestBmsLibrary library = CreateLibrary(synchronizer);
            var table = new MainChartListViewModel(action => action());
            var pending = new PendingPackageWorkflowOwner(() => library, synchronizer, new ChartMutationActivityOwner(), new NoOpChartMutationPlaybackPort(),
                new TestUiDialogService(), () => new InstallDestinationWorkflowSettingsSnapshot(false, false), ExternalShellGatewayPolicy.Current);
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table), action => action(), pendingPackageWorkflow: pending, chartFileOperations: synchronizer);
            EventHandler<PendingPackageWorkflowChangedEventArgs> applied = (_, args) => { if (args is PendingPackageMutationAppliedEventArgs mutation) { owner.ApplyPendingDestinationProjection(mutation); } };
            pending.WorkflowChanged += applied;
            try
            {
                library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
                owner.AttachNormalLibraryRefreshSource(library);
                owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None).GetAwaiter().GetResult();
                Redisplay();
                LibraryChartRow row = table.Rows.Cast<LibraryChartRow>().Single(item => item.path == path);
                var context = new MainChartListCellEditContext(row, "instl_dst", ChartOperationSourceScope.Library, MainViewOperationSection.FullScanCheck);
                Assert.IsTrue(owner.CanBeginCellEdit(context));
                owner.CompleteCellEdit(new MainChartListCellEditEndedEventArgs(context, destination, true)).GetAwaiter().GetResult();
                Redisplay();
                Assert.AreEqual(destination, table.Rows.Cast<LibraryChartRow>().Single(item => item.path == path).instl_dst);
                Assert.IsTrue(string.IsNullOrEmpty(table.Rows.Cast<LibraryChartRow>().Single(item => item.path == known).instl_dst));
                library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.FullReinitialize);
                owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None).GetAwaiter().GetResult();
                Redisplay();
                Assert.IsTrue(string.IsNullOrEmpty(table.Rows.Cast<LibraryChartRow>().Single(item => item.path == path).instl_dst));
                var freshAdmission = new ChartFileOperationSynchronizer();
                TestBmsLibrary fresh = CreateLibrary(freshAdmission);
                try
                {
                    fresh.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
                    var nextTable = new MainChartListViewModel(action => action());
                    using RegularChartListOwner nextOwner = CreateOwner(nextTable, CreateWorkspaceForOwner(nextTable), action => action(), chartFileOperations: freshAdmission);
                    nextOwner.ApplyMainLibraryView(Route(), fresh, null, null, false, Stopwatch.StartNew());
                    Assert.IsTrue(string.IsNullOrEmpty(nextTable.Rows.Cast<LibraryChartRow>().Single(item => item.path == path).instl_dst));
                }
                finally { fresh.RequestShutdown("destination-new-owner-test"); }
            }
            finally { pending.WorkflowChanged -= applied; library.RequestShutdown("destination-lifetime-test"); }

            void Redisplay() => owner.ApplyMainLibraryView(Route(), library, null, null, false, Stopwatch.StartNew());
            static ChartListRefreshRoute Route() => new(ChartListRefreshRouteKind.ContinueMainLibrary, MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected, false, false);
        });
    }
    /// <summary>編集可否のpure probeを区分/出自で限定し、文脈なしの能力を拡大しません。別installed package・playlistと不整合scopeは拒否し、両形式の通常所持だけ全件確認で受理します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallDestinationPermission_UsesCurrentSectionAndSourceWithoutBeginningEdit(bool bmson)
    {
        ChartFile chart = ChartTestValues.Empty(bmson ? ChartFileKind.Bmson : ChartFileKind.Bms) with { Path = @"C:\owned\chart", Md5 = new string('a', 32) };
        var owned = LibraryChartRow.FromChartFile(chart);
        var pending = LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(owned, out ChartOperationTarget plain));
        Assert.IsFalse(plain.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        foreach (MainViewOperationSection section in Enum.GetValues<MainViewOperationSection>())
        {
            foreach (ChartOperationSourceScope scope in Enum.GetValues<ChartOperationSourceScope>())
            {
                Assert.AreEqual(section == MainViewOperationSection.FullScanCheck && scope == ChartOperationSourceScope.Library,
                    GridRowResolver.TryGetInstallDestinationEditTarget(owned, scope, section, out _), $"{section}/{scope}");
                Assert.AreEqual(section == MainViewOperationSection.InstallPending && scope == ChartOperationSourceScope.PendingPackage,
                    GridRowResolver.TryGetInstallDestinationEditTarget(pending, scope, section, out _), $"pending {section}/{scope}");
            }
        }
        PlaylistDetailRow playlist = new PlaylistDetailSourceRow(new BMSTableEntry { md5 = chart.Md5 }, chart).CreateViewRow();
        Assert.IsFalse(GridRowResolver.TryGetInstallDestinationEditTarget(playlist, ChartOperationSourceScope.Library, MainViewOperationSection.FullScanCheck, out _));
    }
    /// <summary>後発選択と表示失敗の診断を、MSTestが指定する結果ディレクトリへ保存します。</summary>
    public TestContext? TestContext { get; set; }

    /// <summary>通常と保留の必要導入先反映を、16/128行の同じ一対象へ実ownerから配送します。全source・順序buffer・未訪問行の生成を増やさず、現在値と既実体化の背景行を保持します。</summary>
    [TestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void InstallDestinationProjection_UpdatesOnlyDependentRows(int count, bool package)
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDb =>
        {
            var charts = Enumerable.Range(0, count).Select(index => ChartTestValues.Empty() with
            {
                Path = Path.Combine(Path.GetDirectoryName(songDb)!, index + ".bms"),
                Md5 = (index + 1).ToString("x32"),
                Title = index == 0 ? "A target" : "B background " + index
            }).ToList();
            var library = new TestBmsLibrary(databaseScope, songDb) { BmsCharts = charts };
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            PackageChartEntry? entry = null;
            var work = new List<(string Kind, int Count)>();
            if (package)
            {
                PackageChartEntry[] entries = charts.Select(PackageChartEntry.FromChart).ToArray(); entry = entries[0];
                var scores = new ChartListScoreInput(null);
                List<ChartListSourceRow> source = table.RowProjection.BuildPackageSourceRows(null, entries, false, scores);
                table.Rows = new ChartListVirtualView(source, ChartListOrder.CreateTitleAscending(source),
                    row => table.RowProjection.CreateSubsetRow(null, row, false), scoreInput: scores,
                    observeWork: (kind, amount) => work.Add((kind, amount)));
            }
            else
            {
                owner.ApplyMainLibraryView(new ChartListRefreshRoute(ChartListRefreshRouteKind.ContinueMainLibrary,
                    MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected,
                    false, false), library, null, null, false, Stopwatch.StartNew());
            }
            ChartListVirtualView view = table.Rows as ChartListVirtualView ?? throw new AssertFailedException();
            LibraryChartRow target = view[0] as LibraryChartRow ?? throw new AssertFailedException();
            LibraryChartRow background = view[1] as LibraryChartRow ?? throw new AssertFailedException();
            Assert.AreEqual(string.Empty, target.instl_dst);
            table.RowProjection.NormalInputWorkObserver = (kind, amount) => work.Add((kind, amount));
            work.Clear();
            string destination = Path.Combine(Path.GetDirectoryName(songDb)!, "installed");
            ChartFile changed;
            if (entry != null) { entry.ApplyInstallDestinationMetadata(destination, "Saved destination", "Saved artist"); changed = entry.Chart; }
            else { changed = target.Chart with { InstallDestination = destination, InstallDestinationTitle = "Saved destination", InstallDestinationArtist = "Saved artist" }; }
            owner.ApplyPendingDestinationProjection(new PendingPackageMutationAppliedEventArgs(package ? [] : [changed], true,
                projectionCharts: [changed]));
            Assert.AreEqual(destination, target.instl_dst);
            Assert.AreEqual("Saved destination", target.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, background.instl_dst);
            Assert.AreSame(view, table.Rows);
            Assert.AreSame(background, view[1]);
            Assert.AreEqual(2, view.RealizedRowCount);
            Assert.AreEqual(0, work.Where(item => item.Kind is "order_buffer" or "package_projection").Sum(item => item.Count));
            Assert.IsTrue(work.Where(item => item.Kind is "source_visit" or "projection" or "hash_dependency_visit").Sum(item => item.Count) <= 4,
                "一対象の導入先変更で背景件数に比例する入力仕事をしません。");
            table.RowProjection.NormalInputWorkObserver = null;
        });
    }

    /// <summary>全件確認の所持一譜面と保留項目の編集を同じ判定から実ownerへ配送し、出自・入力先と所持だけの一時投影通知を保ちます。実modelの適用範囲はRegroup系列へ分担します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstlDstCellEdit_UsesPendingOwnerWithExactChartTargetAndText(bool owned)
    {
        var table = new MainChartListViewModel(action => action());
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
        var store = new PendingPackageStoreTestFixture.RecordingStore([]);
        var library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var pendingOwner = new PendingPackageWorkflowOwner(
            () => library,
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpChartMutationPlaybackPort(),
            new TestUiDialogService(),
            () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: false,
                deletePendingPackageSourceAfterInstall: false),
            ExternalShellGatewayPolicy.Current,
            store);
        var workflowCompletion = new TaskCompletionSource<PendingPackageMutationAppliedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOwner.WorkflowChanged += (_, change) =>
        {
            if (change is PendingPackageMutationAppliedEventArgs mutation)
            {
                workflowCompletion.TrySetResult(mutation);
            }
        };

        using RegularChartListOwner owner = CreateOwner(
            table,
            workspace,
            action => action(),
            pendingPackageWorkflow: pendingOwner);
        MainChartListCellEditContext? beginning = null;
        table.CellEditBeginningRequested += (_, request) =>
        {
            beginning = request.Context;
            request.Accepted = owner.CanBeginCellEdit(request.Context);
        };
        Task? editTask = null;
        table.CellEditEndedRequested += (_, request) => editTask = owner.CompleteCellEdit(request);

        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"C:\wave6e-owner-chain\pending.bms",
            Md5 = "ffffffffffffffffffffffffffffffff",
            RawTitle = "Owner chain chart"
        };
        var entry = PackageChartEntry.FromChart((file));
        store.SetResult = entry.Chart;
        LibraryChartRow row = owned ? LibraryChartRow.FromChartFile(file) : LibraryChartRow.FromPackageChartEntry(entry);
        const string destination = @"C:\wave6e-owner-chain\destination";

        table.SetOperationContext(owned ? MainViewUpdateMode.FullScanAllChartsFilterSelected : MainViewUpdateMode.PendingInstallFolderSelected);
        Assert.IsTrue(table.TryBeginCellEdit(row, "instl_dst"));
        Assert.IsNotNull(beginning);
        Assert.AreSame(row, beginning.Row);
        Assert.AreEqual(owned ? MainViewOperationSection.FullScanCheck : MainViewOperationSection.InstallPending, beginning.OperationSection);
        Assert.AreEqual(owned ? ChartOperationSourceScope.Library : ChartOperationSourceScope.PendingPackage, beginning.SourceScope);

        table.NotifyCellEditStarted(row, "instl_dst");
        table.RequestCellEditEnded(row, "instl_dst", destination, commit: true);
        Task acceptedEdit = editTask ?? throw new AssertFailedException("実編集Taskが端末へ返されませんでした。");
        await TestUiDispatcherHost.AwaitNotificationAsync(workflowCompletion.Task, acceptedEdit, "pending-cell-owner-delivery");
        PendingPackageMutationAppliedEventArgs applied = await workflowCompletion.Task;
        await acceptedEdit;

        Assert.IsNotNull(store.LastPendingRequest);
        PendingInstallDestinationEditRequest savedRequest = store.LastPendingRequest ?? throw new AssertFailedException("編集要求がありません。");
        Assert.AreSame(owned ? null : entry, savedRequest.PackageEntry);
        if (owned) { Assert.AreSame(file, savedRequest.ChartFile); }
        else { Assert.AreSame(entry, savedRequest.GetOrCreateChartEntry()); }
        Assert.AreEqual(destination, store.LastDestinationDirectory);
        Assert.IsTrue(applied.InstallDestinationStateChanged);
        Assert.AreEqual(owned ? 1 : 0, applied.ChangedCharts.Count);
    }


    /// <summary>directory・artist・全体への移動で検索条件とsummary終了要求を配送し、無効種類は通知も条件変更もしないことで、旧条件残留と拒否時の変更を検出します。</summary>
    [TestMethod]
    public void NavigateTree_OwnsFilterSummaryTransitionAndRefreshRequest()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
        var presentations = new List<(RegularNormalLibraryTreeFilter? Filter, bool KeywordRefresh, MainViewUpdateMode RefreshMode)>();
        owner.TreeNavigationPresentationRequested += (_, request) => presentations.Add((
            owner.CaptureTreeFilter(enabled: true),
            request.KeywordPresentationRefreshRequired,
            request.RefreshMode));

        Assert.IsTrue(owner.NavigateTree(
            RegularChartFolderFilterKind.Directory,
            @"C:\Charts\Folder A"));
        Assert.AreEqual(1, presentations.Count);
        Assert.AreEqual(
            @"C:\Charts\Folder A" + Path.DirectorySeparatorChar,
            presentations[0].Filter?.Term);
        Assert.AreEqual(RegularChartFolderFilterKind.Directory, presentations[0].Filter?.Kind);
        Assert.IsFalse(presentations[0].KeywordRefresh);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, presentations[0].RefreshMode);

        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsTrue(owner.NavigateTree(RegularChartFolderFilterKind.Artist, "Artist A"));
        Assert.AreEqual("Artist A", presentations[1].Filter?.Term);
        Assert.AreEqual(RegularChartFolderFilterKind.Artist, presentations[1].Filter?.Kind);
        Assert.IsTrue(presentations[1].KeywordRefresh);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);

        Assert.IsTrue(owner.NavigateTree(filterKind: null));
        Assert.IsNull(presentations[2].Filter);
        Assert.IsTrue(presentations[2].KeywordRefresh);

        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsFalse(owner.NavigateTree((RegularChartFolderFilterKind)999, "invalid"));
        Assert.AreEqual(3, presentations.Count);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsNull(owner.CaptureTreeFilter(enabled: true));
    }


    /// <summary>重複以外の各保守画面について、対応するモードと対象を一度だけ配送し、非対応の要求を拒むことを確認します。重複データの準備と表示は初回解析の代表ケースが分担します。</summary>
    [TestMethod]
    public void NavigateMaintenance_OwnsSummaryTransitionAndAllMaintenanceRefreshRequests()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                DuplicateChartGroups = []
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
            owner.AttachNormalLibraryRefreshSource(library);
            var presentations = new List<RegularChartMaintenanceNavigationPresentationRequestedEventArgs>();
            owner.MaintenanceNavigationPresentationRequested += (_, request) => presentations.Add(request);
            // このケースは重複以外の保守画面への配送を確認し、重複の準備と表示は初回解析のケースが分担する。
            MainViewUpdateMode[] modes =
            [
                MainViewUpdateMode.FullScanAllChartsFilterSelected,
                MainViewUpdateMode.FileMissingFilterSelected,
                MainViewUpdateMode.FileMissingIgnoredFilterSelected,
                MainViewUpdateMode.GarbledFilterSelected,
                MainViewUpdateMode.GarbleFixedFilterSelected,
                MainViewUpdateMode.UnregisteredFilterSelected,
                MainViewUpdateMode.ZeroNoteFilterSelected,
                MainViewUpdateMode.ChartInfoParseErrorFilterSelected
            ];

            workspace.SetPlaylistSummaryMode(enabled: true);
            for (int index = 0; index < modes.Length; index++)
            {
                object? parameter = null;
                int presentationCountBefore = presentations.Count;

                Assert.IsTrue(owner.NavigateMaintenance(modes[index], parameter, "test_navigation"));

                RegularChartMaintenanceNavigationPresentationRequestedEventArgs request = presentations[^1];
                Assert.AreEqual(modes[index], request.Mode);
                Assert.AreSame(parameter, request.Parameter);
                Assert.AreEqual(1, presentations.Count - presentationCountBefore);
                Assert.IsTrue(request.KeywordPresentationRefreshRequired);
                Assert.IsTrue(request.RefreshRequested);
            }
            Assert.IsTrue(workspace.IsPlaylistSummaryMode);
            Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);

            int presentationCount = presentations.Count;
            workspace.SetPlaylistSummaryMode(enabled: true);
            Assert.IsFalse(owner.NavigateMaintenance(MainViewUpdateMode.SortUpdated));
            Assert.AreEqual(presentationCount, presentations.Count);
            Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        });
    }


    /// <summary>library未接続でも欠損・ゼロnote画面のsummary終了要求を公開し、実一覧更新は要求しないことで、未接続時の画面遷移漏れを検出します。</summary>
    [TestMethod]
    public void NavigateMaintenance_WithoutLibraryStillPublishesOwnedSummaryTransition()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
        var presentations = new List<RegularChartMaintenanceNavigationPresentationRequestedEventArgs>();
        owner.MaintenanceNavigationPresentationRequested += (_, request) => presentations.Add(request);
        workspace.SetPlaylistSummaryMode(enabled: true);

        Assert.IsTrue(owner.NavigateMaintenance(MainViewUpdateMode.FileMissingFilterSelected));

        Assert.AreEqual(1, presentations.Count);
        Assert.AreEqual(MainViewUpdateMode.FileMissingFilterSelected, presentations[0].Mode);
        Assert.IsTrue(presentations[0].KeywordPresentationRefreshRequired);
        Assert.IsFalse(presentations[0].RefreshRequested);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);

        Assert.IsTrue(owner.NavigateMaintenance(MainViewUpdateMode.ZeroNoteFilterSelected));
        Assert.AreEqual(2, presentations.Count);
        Assert.IsTrue(presentations[1].KeywordPresentationRefreshRequired);
        Assert.IsFalse(presentations[1].RefreshRequested);
    }


    /// <summary>導入済み・保留の全体と特定packageを対応parameterで配送し、非対応保守modeは通知せずsummary状態を保持して、項目取り違えや拒否時の変更を検出します。</summary>
    [TestMethod]
    public void NavigateInstall_OwnsSummaryTransitionAndPackageRefreshRequests()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
        var presentations = new List<RegularChartInstallNavigationPresentationRequestedEventArgs>();
        owner.InstallNavigationPresentationRequested += (_, request) => presentations.Add(request);
        var installedPackage = new ChartPackage { path = @"C:\Installed\Package" };
        var pendingPackage = new ChartPackage { path = @"C:\Pending\Package" };

        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsTrue(owner.NavigateInstall(MainViewUpdateMode.NewlyInstalledFolderSelected));
        Assert.AreEqual(MainViewUpdateMode.NewlyInstalledFolderSelected, presentations[0].Mode);
        Assert.IsNull(presentations[0].Parameter);
        Assert.IsTrue(presentations[0].KeywordPresentationRefreshRequired);

        Assert.IsTrue(owner.NavigateInstall(
            MainViewUpdateMode.NewlyInstalledFolderSelected,
            installedPackage));
        Assert.AreSame(installedPackage, presentations[1].Parameter);
        Assert.IsTrue(presentations[1].KeywordPresentationRefreshRequired);

        Assert.IsTrue(owner.NavigateInstall(MainViewUpdateMode.PendingInstallFolderSelected));
        Assert.AreEqual(MainViewUpdateMode.PendingInstallFolderSelected, presentations[2].Mode);
        Assert.IsNull(presentations[2].Parameter);

        Assert.IsTrue(owner.NavigateInstall(
            MainViewUpdateMode.PendingInstallFolderSelected,
            pendingPackage));
        Assert.AreSame(pendingPackage, presentations[3].Parameter);

        int presentationCount = presentations.Count;
        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsFalse(owner.NavigateInstall(MainViewUpdateMode.FileMissingFilterSelected));
        Assert.AreEqual(presentationCount, presentations.Count);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
    }

}
