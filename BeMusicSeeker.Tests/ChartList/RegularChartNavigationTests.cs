using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>通常一覧ownerの画面遷移と編集要求の配送をWindowなしで確認します。</summary>
/// <remarks>実libraryを使う重複caseはgroup構築から通知までを担当し、compiledセル・treeの接続はMainWindowの実画面caseが分担します。</remarks>
[TestClass]
public sealed class RegularChartNavigationTests
{
    /// <summary>保留セル編集の受理から完了まで実pending ownerへ元package項目と入力先を配送し、導入先だけの変更通知を返して別譜面編集や余分なライブラリ変更を検出します。</summary>
    [TestMethod]
    public async Task InstlDstCellEdit_UsesPendingOwnerWithExactChartTargetAndText()
    {
        var table = new MainChartListViewModel(action => action());
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
        var store = new PendingPackageWorkflowOwnerTests.RecordingStore([]);
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
        table.CellEditEndedRequested += (_, request) => owner.CompleteCellEdit(request);

        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"C:\wave6e-owner-chain\pending.bms",
            Md5 = "ffffffffffffffffffffffffffffffff",
            RawTitle = "Owner chain chart"
        };
        var entry = PackageChartEntry.FromChart((file));
        store.SetResult = entry.Chart;
        var row = LibraryChartRow.FromPackageChartEntry(entry);
        const string destination = @"C:\wave6e-owner-chain\destination";

        table.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
        Assert.IsTrue(table.TryBeginCellEdit(row, "instl_dst"));
        Assert.IsNotNull(beginning);
        Assert.AreSame(row, beginning.Row);
        Assert.AreEqual(MainViewOperationSection.InstallPending, beginning.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, beginning.SourceScope);

        table.NotifyCellEditStarted(row, "instl_dst");
        table.RequestCellEditEnded(row, "instl_dst", destination, commit: true);
        PendingPackageMutationAppliedEventArgs applied = await workflowCompletion.Task;

        Assert.IsNotNull(store.LastPendingRequest);
        Assert.AreSame(entry, store.LastPendingRequest!.PackageEntry);
        Assert.AreSame(entry, store.LastPendingRequest.GetOrCreateChartEntry());
        Assert.AreEqual(destination, store.LastDestinationDirectory);
        Assert.IsTrue(applied.InstallDestinationStateChanged);
        Assert.AreEqual(0, applied.ChangedCharts.Count);
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


    /// <summary>列挙した保守画面へ対応modeとparameterを渡し、重複画面だけの準備通知と検索更新区分を保持して、別保守画面への配送や非対応sort受理を検出します。</summary>
    [TestMethod]
    public void NavigateMaintenance_OwnsSummaryTransitionAndAllMaintenanceRefreshRequests()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                DuplicateChartGroups = []
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
            owner.AttachNormalLibraryRefreshSource(library);
            var presentations = new List<RegularChartMaintenanceNavigationPresentationRequestedEventArgs>();
            owner.MaintenanceNavigationPresentationRequested += (_, request) => presentations.Add(request);
            // 重複だけはcache準備を別通知し、他の保守画面は一回の更新要求です。
            MainViewUpdateMode[] modes =
            [
                MainViewUpdateMode.FullScanAllChartsFilterSelected,
                MainViewUpdateMode.FileMissingFilterSelected,
                MainViewUpdateMode.FileMissingIgnoredFilterSelected,
                MainViewUpdateMode.DuplicateFilterSelected,
                MainViewUpdateMode.GarbledFilterSelected,
                MainViewUpdateMode.GarbleFixedFilterSelected,
                MainViewUpdateMode.UnregisteredFilterSelected,
                MainViewUpdateMode.ZeroNoteFilterSelected,
                MainViewUpdateMode.ChartInfoParseErrorFilterSelected
            ];

            workspace.SetPlaylistSummaryMode(enabled: true);
            for (int index = 0; index < modes.Length; index++)
            {
                object? parameter = index == 3 ? "duplicate-folder" : null;
                int presentationCountBefore = presentations.Count;

                Assert.IsTrue(owner.NavigateMaintenance(modes[index], parameter, "test_navigation"));

                RegularChartMaintenanceNavigationPresentationRequestedEventArgs request = presentations[^1];
                Assert.AreEqual(modes[index], request.Mode);
                Assert.AreSame(parameter, request.Parameter);
                Assert.AreEqual(index == 3 ? 2 : 1, presentations.Count - presentationCountBefore);
                Assert.AreEqual(index != 3, request.KeywordPresentationRefreshRequired);
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


    /// <summary>実libraryの重複一覧移動ではgroup構築後に更新要求を公開し、受信側が未構築cacheを読む順序逆転を検出します。</summary>
    [TestMethod]
    public void NavigateMaintenance_DuplicateModeBuildsCacheBeforeRefreshPresentation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts =
                [
                    CreateTestableBmsFile(@"C:\Charts\duplicate-a.bms"),
                    CreateTestableBmsFile(@"C:\Charts\duplicate-b.bms")
                ]
            };
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            var cacheWasReadyAtPresentation = new List<bool>();
            owner.MaintenanceNavigationPresentationRequested += (_, request) =>
            {
                if (request.RefreshRequested)
                {
                    cacheWasReadyAtPresentation.Add(library.DuplicateChartGroups != null);
                }
            };

            Assert.IsTrue(owner.NavigateMaintenance(
                MainViewUpdateMode.DuplicateFilterSelected,
                parameter: "duplicate-folder",
                reason: "test_duplicate_navigation"));

            Assert.IsNotNull(library.DuplicateChartGroups);
            Assert.AreEqual(1, library.DuplicateChartGroups.Count);
            CollectionAssert.AreEqual(new[] { true }, cacheWasReadyAtPresentation);
        });
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
