using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧の要求から構築・確定、仮想cacheの採用と失効までをWindowなしで検証します。</summary>
/// <remarks>実FSと健全性索引は警告準備の接続に使い、表示操作はMainWindowが担当します。cacheキーの構築値でなく実ownerの公開・拒否・退役を観測します。</remarks>
[TestClass]
public sealed class RegularChartViewBuildAndOrderingTests
{

    /// <summary>未構築の健全性索引を実譜面から用意して欠損警告付き仮想行を公開し、索引準備前の警告なし表示を検出します。</summary>
    [TestMethod]
    public void ApplyMainLibraryView_PreparesColdResourceHealthBeforeVirtualRows()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "resource-health-warning.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Resource health warning\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = 1, wav_files_existing = 0 }));
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new PlaylistSummaryAggregationTestDoubles.RecordingDialogService());
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
        var databaseScope = new ApplicationDatabaseScope();

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
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new PlaylistSummaryAggregationTestDoubles.RecordingDialogService());
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

    /// <summary>新規・保留の全体/単一packageについて警告順、BMS/BMSON、検索・24/48選択と5除外・folder件数を実ownerで確認します。背景所持数だけを増やしてもpackage投影は対象entry数に限られます。</summary>
    [TestMethod]
    [DataRow(true, false, 16)]
    [DataRow(true, false, 128)]
    [DataRow(true, true, 16)]
    [DataRow(false, false, 16)]
    [DataRow(false, false, 128)]
    [DataRow(false, true, 16)]
    public void PackageViewAsync_PreservesWarningsFiltersOrderAndSubsetCounts(bool installed, bool singlePackage, int backgroundCount)
    {
        WithTemporarySongDb(path => VerifyPackageViewAsync(path, installed, singlePackage, backgroundCount).GetAwaiter().GetResult());
    }

    private static async Task VerifyPackageViewAsync(string songDbPath, bool installed, bool singlePackage, int backgroundCount)
    {
        var databaseScope = new ApplicationDatabaseScope();
        string root = Path.GetDirectoryName(songDbPath)!;
        string missingPath = Path.Combine(root, "warning.bms");
        File.WriteAllText(missingPath, "#PLAYER 1\r\n#TITLE Keep Warning\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
        ChartFile warning = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(missingPath)) with { Folder = "Warning", Mode = 24 };
        warning = ChartFileProjection.WithMaintenance(warning,
            MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance
            { path = warning.Path, hash = warning.Md5, wav_files_defined = 1, wav_files_existing = 0 }));
        if (!installed)
        {
            warning = ChartFileProjection.WithWarnings(warning,
                [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "pending estimation warning")]);
        }
        ChartFile clean = CreateSourceRow("Clean", "Keep Clean", 48).Chart with { Kind = ChartFileKind.Bmson };
        ChartFile keywordExcluded = CreateSourceRow("Keyword excluded", "Drop", 24).Chart;
        ChartFile modeExcluded = CreateSourceRow("Mode excluded", "Keep Five", 5).Chart;
        ChartFile other = CreateSourceRow("Other", "Keep Other", 48).Chart;
        ChartPackage selected = ChartPackageTestExtensions.CreatePackage(warning, clean, keywordExcluded, modeExcluded);
        ChartPackage second = ChartPackageTestExtensions.CreatePackage(other);
        var library = new TestBmsLibrary(databaseScope, songDbPath);
        OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library,
            new[] { warning }.Concat(Enumerable.Range(0, backgroundCount).Select(index => CreateSourceRow("Background", "Background " + index).Chart)).ToArray());
        if (installed) { library.ChartPackagesInstalled = [selected, second]; }
        else { library.ChartPackagesPending = [selected, second]; }
        Assert.IsTrue(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
        int projectedEntries = 0;
        table.RowProjection.NormalInputWorkObserver = (kind, count) =>
        {
            if (kind == "package_projection") { Interlocked.Add(ref projectedEntries, count); }
        };
        owner.QueueSort(new MainChartListSortRequestedEventArgs(nameof(LibraryChartRow.WarningDigestText),
            ListSortDirection.Descending, MainChartListSortTarget.Regular));
        MainViewUpdateMode mode = installed ? MainViewUpdateMode.NewlyInstalledFolderSelected : MainViewUpdateMode.PendingInstallFolderSelected;
        var filters = new ChartListFilterSnapshot("Keep", ChartModeFilter._24KEYS | ChartModeFilter._48KEYS);
        try
        {
            RegularChartListEntryResult result = await ApplyPackageViewAsync(owner, library, mode,
                singlePackage ? selected : null, filters);
            Assert.IsTrue(result.WasCommitted);
            Assert.AreEqual(RegularChartListEntryRoute.SubsetVirtual, result.Route);
            Assert.AreEqual(singlePackage ? 4 : 5, projectedEntries);
            var view = (ChartListVirtualView)table.Rows;
            Assert.AreEqual(singlePackage ? 2 : 3, view.Count);
            Assert.AreEqual(singlePackage ? 2 : 3, view.DistinctFolderCount);
            var warningRow = (LibraryChartRow)view[0];
            Assert.AreEqual("Keep Warning", warningRow.Title);
            Assert.IsTrue(warningRow.WarningDigestText.Length > 0);
            Assert.IsTrue(view.Cast<LibraryChartRow>().Any(row => row.Chart.Kind == ChartFileKind.Bmson));
            Assert.IsFalse(view.Cast<LibraryChartRow>().Any(row => row.Title is "Drop" or "Keep Five"));
            if (installed)
            {
                StringAssert.Contains(warningRow.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                Assert.IsFalse(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));
            }
            else
            {
                Assert.IsTrue(warningRow.Chart.Warnings.Any(item => item.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                Assert.IsTrue(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library), "保留表示のために全所持の健全性索引を構築しません。");
            }
            ResourceHealthIndexSnapshot health = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            owner.QueueSort(new MainChartListSortRequestedEventArgs(nameof(LibraryChartRow.Title),
                ListSortDirection.Descending, MainChartListSortTarget.Regular));
            Assert.IsTrue((await ApplyPackageViewAsync(owner, library, mode, singlePackage ? selected : null, filters)).WasCommitted);
            CollectionAssert.AreEqual(singlePackage
                ? new[] { "Keep Warning", "Keep Clean" }
                : new[] { "Keep Warning", "Keep Other", "Keep Clean" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            Assert.AreEqual((singlePackage ? 4 : 5) * 2, projectedEntries);
            Assert.AreSame(health, library.TryGetCurrentResourceHealthIndexSnapshotForView(), "同じ健全性入力をwarm表示で再利用します。");
            string adoptedSummary = table.SummaryText;
            table.UpdateSummaryText(singlePackage ? 2 : 3, singlePackage ? 2 : 3);
            Assert.AreEqual(adoptedSummary, table.SummaryText);
        }
        finally
        {
            table.RowProjection.NormalInputWorkObserver = null;
            await owner.StopAsync();
        }
    }

    /// <summary>旧package workerの入力投影をgateで止めても実Dispatcherが応答し、新要求の採用後に旧workerを解放してもRowsを上書きしません。停止は取消だけでなく実workerの終了を回収します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackageViewAsync_BlockedWorkerKeepsDispatcherResponsiveAndCannotOverwriteNewerView(bool stopWhileBlocked)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table),
            action => TestUiDispatcherHost.Dispatcher.Invoke(action), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        ChartPackage oldPackage = ChartPackageTestExtensions.CreatePackage(CreateSourceRow("Old", "Old").Chart);
        ChartPackage newPackage = ChartPackageTestExtensions.CreatePackage(CreateSourceRow("New", "New").Chart);
        Task<RegularChartListEntryResult> oldBuild = Task.FromResult(default(RegularChartListEntryResult));
        Task<RegularChartListEntryResult> newBuild = Task.FromResult(default(RegularChartListEntryResult));
        Task stop = Task.CompletedTask;
        int captured = 0;
        bool workerRanOutsideUi = false;
        table.RowProjection.NormalInputWorkObserver = (kind, _) =>
        {
            if (kind == "reference_capture" && Interlocked.Increment(ref captured) == 1)
            {
                workerRanOutsideUi = !TestUiDispatcherHost.Dispatcher.CheckAccess();
                started.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        };
        try
        {
            TestUiDispatcherHost.Invoke(() => oldBuild = ApplyPackageViewAsync(owner, null,
                MainViewUpdateMode.PendingInstallFolderSelected, oldPackage));
            await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, oldBuild, "package worker input projection");
            bool dispatcherProcessedMarker = false;
            TestUiDispatcherHost.Invoke(() => dispatcherProcessedMarker = true);
            Assert.IsTrue(dispatcherProcessedMarker);
            Assert.IsTrue(workerRanOutsideUi);
            Assert.IsFalse(oldBuild.IsCompleted);
            if (stopWhileBlocked)
            {
                stop = owner.StopAsync();
                Assert.IsFalse(stop.IsCompleted, "Stopは取消済みでも実workerがgateから戻るまで待ちます。");
            }
            else
            {
                TestUiDispatcherHost.Invoke(() => newBuild = ApplyPackageViewAsync(owner, null,
                    MainViewUpdateMode.PendingInstallFolderSelected, newPackage));
                Assert.IsTrue((await newBuild).WasCommitted);
                TestUiDispatcherHost.Invoke(() => Assert.AreEqual("New", ((LibraryChartRow)table.Rows[0]!).Title));
            }
            release.TrySetResult(true);
            Assert.IsFalse((await oldBuild).WasCommitted);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            if (!stopWhileBlocked)
            {
                TestUiDispatcherHost.Invoke(() => Assert.AreEqual("New", ((LibraryChartRow)table.Rows[0]!).Title));
            }
        }
        finally
        {
            release.TrySetResult(true);
            try { await Task.WhenAll(oldBuild, newBuild, stop); }
            finally
            {
                table.RowProjection.NormalInputWorkObserver = null;
                await owner.StopAsync();
            }
        }
    }

    /// <summary>package入口のUI予約を保留し、新しい選択のInvalidateだけを先に通します。旧予約がleaseや詳細退役を始めず、既存Rowsと派生cacheを保つことを実Dispatcherで確認します。</summary>
    [TestMethod]
    public async Task PackageViewAsync_QueuedUiEntryDoesNotBeginAfterNewSelectionInvalidation()
    {
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scheduled = 0;
        var scheduler = new ActionQueueUiScheduler(action =>
        {
            if (Interlocked.Increment(ref scheduled) == 1) { queued.TrySetResult(action); }
            else { TestUiDispatcherHost.Dispatcher.BeginInvoke(action); }
        });
        var table = new MainChartListViewModel();
        var detailBuild = new PlaylistDetailBuildState { RequestVersion = 10 };
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table, detailBuild);
        using RegularChartListOwner owner = CreateOwner(table, workspace,
            action => TestUiDispatcherHost.Dispatcher.Invoke(action), scheduler);
        ChartPackage oldPackage = ChartPackageTestExtensions.CreatePackage(CreateSourceRow("Old", "Old").Chart);
        System.Collections.IList originalRows = table.Rows;
        Task<RegularChartListEntryResult> oldBuild = Task.FromResult(default(RegularChartListEntryResult));
        bool released = false;
        int projectedEntries = 0;
        table.RowProjection.NormalInputWorkObserver = (kind, count) =>
        {
            if (kind == "package_projection") { Interlocked.Add(ref projectedEntries, count); }
        };
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Assert.IsTrue(owner.TryApplyMaterialized(CreateMaterializedApplyRequest(
                    [LibraryChartRow.FromChartFile(CreateSourceRow("Current", "Current").Chart)])).WasCommitted);
                Assert.IsTrue(owner.HasFolderRows);
                originalRows = table.Rows;
                workspace.IsPlaylistDetailViewActive = true;
            });
            // productionの非UI navigationと同じく、通常leaseを失効させてからUI入力捕捉を予約する。
            owner.PrepareForMainViewRefresh();
            oldBuild = ApplyPackageViewAsync(owner, null, MainViewUpdateMode.PendingInstallFolderSelected, oldPackage);
            await TestUiDispatcherHost.AwaitNotificationAsync(queued.Task, oldBuild, "package view UI entry scheduling");
            Action entry = await queued.Task;
            TestUiDispatcherHost.Invoke(owner.PrepareForMainViewRefresh);
            released = true;
            TestUiDispatcherHost.Invoke(entry);

            Assert.IsFalse((await oldBuild).WasCommitted);
            Assert.AreEqual(0, projectedEntries);
            Assert.AreEqual(1, scheduled, "旧予約はworkerや後続UI採用を始めません。");
            TestUiDispatcherHost.Invoke(() =>
            {
                Assert.AreSame(originalRows, table.Rows);
                Assert.IsTrue(owner.HasFolderRows, "旧予約は新しい選択の派生cacheを破棄しません。");
                Assert.IsFalse(owner.IsCurrentRegularRows(table.Rows), "Invalidate後に旧予約のregular leaseを発行しません。");
                Assert.AreEqual(10, detailBuild.RequestVersion, "新しいplaylist選択の詳細sourceを退役させません。");
                Assert.IsTrue(workspace.IsPlaylistDetailViewActive);
            });
        }
        finally
        {
            try
            {
                if (!released && queued.Task.IsCompletedSuccessfully)
                {
                    TestUiDispatcherHost.Invoke(queued.Task.GetAwaiter().GetResult());
                }
            }
            finally
            {
                try { await oldBuild; }
                finally
                {
                    table.RowProjection.NormalInputWorkObserver = null;
                    await owner.StopAsync();
                }
            }
        }
    }

    private static Task<RegularChartListEntryResult> ApplyPackageViewAsync(
        RegularChartListOwner owner, BMSLibrary? library, MainViewUpdateMode mode,
        ChartPackage? package = null, ChartListFilterSnapshot? filters = null)
        => owner.ApplyMainLibraryViewAsync(new ChartListRefreshRoute(ChartListRefreshRouteKind.ContinueMainLibrary,
            mode, mode, mode, isPlaylistTreeActive: false, includeBmsonRows: false), library,
            parameter: package, treeParameter: package, preserveSummary: false, Stopwatch.StartNew(), filters);

}
