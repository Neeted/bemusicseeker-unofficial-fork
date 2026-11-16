using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.SharedPlaylistWorkspaceFixture;

namespace BeMusicSeeker.Tests;


/// <summary>workspaceの表示状態確定・世代失効・通知とBMT順保存後の選択復元を確認します。</summary>
/// <remarks>実VMケースはMainChartListとの操作mode接続を対象にし、共有composition資源のため非並列です。BMT保存失敗と復元要求は実DBと実workspaceを結び、WPF viewへの実接続はMainWindow側へ分担します。</remarks>
[TestClass]
public sealed class PlaylistWorkspacePresentationStateTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>列と詳細表示modeを通知なしで一括確定し、明示publish後に各propertyを通知して一部確定状態の公開を防ぎます。</summary>
    [TestMethod]
    public void MainTablePresentationCommit_CombinesWorkspaceStateBeforePublishingNotifications()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        var columns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var summaryColumns = new PlaylistSummaryColumnSettings();
        var selection = new MainChartListColumnSelection(
            columns,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.FolderFilterSelected,
            Visibility.Visible,
            summaryColumns);
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        PlaylistMainTablePresentationCommit commit = workspace.CommitMainTablePresentationWithoutNotification(
            selection,
            playlistDetailActive: true,
            playlistSummaryActive: false);

        Assert.AreEqual(Visibility.Visible, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(summaryColumns, workspace.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(0, propertyNames.Count);

        workspace.PublishMainTablePresentation(commit);

        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistDetailViewActive));
    }

    /// <summary>同じownerのsummaryでsort要求を適用・通知し、全名がGroupに一致する入力の未所持あり条件を初回・再利用・行更新後の表示へ反映する。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummarySortRequestDrainsOwnerPresentationState()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            if (request.Kind == PlaylistPresentationRefreshKind.SummaryData)
            {
                workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, request.RebuildAsync);
            }
            else if (request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation)
            {
                workspace.ApplyPlaylistSummaryPresentationRefresh(deferred: false);
            }
        };
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { PlaylistId = 1, Name = "Group S1", TotalCharts = 4, OwnedCharts = 1 },
                new PlaylistSummaryRow { PlaylistId = 2, Name = "Group S2", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { PlaylistId = 3, Name = "Group S3", TotalCharts = 2, OwnedCharts = 0 }
            },
            dataGeneration));
        long presentationGenerationBefore = workspace.CurrentPlaylistSummaryPresentationGeneration;
        int callerThreadId = Thread.CurrentThread.ManagedThreadId;
        int propertyChangedCount = 0;
        int propertyChangedThreadId = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummarySortParameters))
            {
                propertyChangedCount++;
                propertyChangedThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        };

        workspace.RequestPlaylistSummarySort(
            nameof(PlaylistSummaryRow.TotalCharts),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, propertyChangedCount);
        Assert.AreEqual(callerThreadId, propertyChangedThreadId);
        Assert.AreEqual(nameof(PlaylistSummaryRow.TotalCharts), workspace.PlaylistSummarySortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistSummarySortParameters.Direction);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > presentationGenerationBefore);
        Assert.AreEqual(3, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual(1, workspace.PlaylistSummaryView[0].PlaylistId);
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, workspace.PlaylistSummaryView.Skip(1).Select(row => row.PlaylistId).ToArray());
        workspace.PlaylistSummaryKeywordFilter = "Group";
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.OwnedIncomplete;
        workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.TotalCharts), System.ComponentModel.ListSortDirection.Ascending);
        CollectionAssert.AreEqual(new[] { 3, 1 }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
        workspace.RequestPlaylistSummaryPresentationRefresh();
        CollectionAssert.AreEqual(new[] { 3, 1 }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { PlaylistId = 1, Name = "Group S1", TotalCharts = 4, OwnedCharts = 4 },
                new PlaylistSummaryRow { PlaylistId = 2, Name = "Group S2", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { PlaylistId = 3, Name = "Group S3", TotalCharts = 5, OwnedCharts = 0 }
            }, workspace.BeginPlaylistSummaryDataRebuildGeneration()));
        workspace.RequestPlaylistSummaryPresentationRefresh();
        CollectionAssert.AreEqual(new[] { 3 }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
        Assert.AreEqual(5, workspace.PlaylistSummaryView[0].TotalCharts);
        Assert.AreEqual(0, workspace.PlaylistSummaryView[0].OwnedCharts);
    }

    /// <summary>summary表示中のkeyword・所持filter変更は件数と要約を更新し、非表示へ切替後は表示世代を進めず不要な表示更新を延期します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryFiltersDrainVisiblePresentationAndStayDeferredWhenHidden()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
            },
            dataGeneration));

        workspace.RequestPlaylistSummaryPresentationRefresh();
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        long initialPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = "alpha";
        long keywordPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.IsTrue(keywordPresentationGeneration > initialPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("alpha", workspace.PlaylistSummaryView[0].Name);
        Assert.AreEqual(
            string.Format(
                BeMusicSeeker.Properties.Resources.Playlist_summary_format,
                2,
                1),
            workspace.PlaylistSummaryText);

        workspace.PlaylistSummaryKeywordFilter = string.Empty;
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.OwnedComplete;
        long ownedPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.IsTrue(ownedPresentationGeneration > keywordPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("alpha", workspace.PlaylistSummaryView[0].Name);

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
        long hiddenPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = string.Empty;
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.All;
        Assert.AreEqual(hiddenPresentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
    }

    /// <summary>shell抑止中は表示と世代を維持してrefreshを延期し、抑止解除後の新filterを適用して古い延期条件を残しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryPresentationRefreshRespectsShellSuppressionGate()
    {
        bool suppressed = true;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
                request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation && suppressed);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
            },
            dataGeneration));

        suppressed = false;
        workspace.RequestPlaylistSummaryPresentationRefresh();
        suppressed = true;
        long initialPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = "alpha";
        Assert.AreEqual(initialPresentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryPresentationRefresh());

        suppressed = false;
        workspace.PlaylistSummaryKeywordFilter = "beta";
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > initialPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("beta", workspace.PlaylistSummaryView[0].Name);
        Assert.IsFalse(workspace.HasDeferredPlaylistSummaryPresentationRefresh());
    }

    /// <summary>詳細sortはcolumn・direction確定後に要求を一回通知し、同じsort再要求を省いて購読先が旧sortを読むことを防ぎます。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDetailSortRequestCommitsOwnerStateBeforeEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        int raisedCount = 0;
        MainChartListSortRequestedEventArgs? observedRequest = null;
        workspace.PlaylistDetailSortChanged += (_, request) =>
        {
            raisedCount++;
            observedRequest = request;
            Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
            Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
        };

        workspace.RequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
        Assert.IsNotNull(observedRequest);
        Assert.AreEqual(MainChartListSortTarget.Regular, observedRequest.Target);
        Assert.IsTrue(observedRequest.OwnerRevision > 0L);

        workspace.RequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
    }

    /// <summary>共通sort要求は詳細表示中だけ受理し、summary中と詳細退出後は最後の詳細sortを保持して別画面から書き換えません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceRoutesSharedDetailSortOnlyWhileDetailIsActive()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Title),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });
        int raisedCount = 0;
        workspace.PlaylistDetailSortChanged += (_, _) => raisedCount++;

        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending));
        workspace.IsPlaylistDetailViewActive = true;
        Assert.IsTrue(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);

        workspace.IsPlaylistSummaryMode = true;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Artist),
            System.ComponentModel.ListSortDirection.Ascending));
        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);

        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistDetailViewActive = false;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Artist),
            System.ComponentModel.ListSortDirection.Ascending));
    }

    /// <summary>初回に利用者sortをコピーして保持し、次の初期化候補で既に選んだsortを上書きしません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDetailSortInitializationUsesFallbackOnlyOnce()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        var initialSort = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Level),
            Direction = System.ComponentModel.ListSortDirection.Descending
        };

        workspace.InitializePlaylistDetailSort(initialSort);

        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
        Assert.AreNotSame(initialSort, workspace.PlaylistDetailSortParameters);

        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Title),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
    }

    /// <summary>24/48の複合filterを通知前に確定し、同値要求とNone鍵数を省く一方、次世代要求で旧要求を失効させて古いfilterを適用しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDetailFilterRequestCommitsOwnerStateBeforeEventAndRejectsNone()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("  title:Alpha  ", ChartModeFilter.All));
        int raisedCount = 0;
        PlaylistDetailFilterChangedEventArgs? firstRequest = null;
        PlaylistDetailFilterChangedEventArgs? secondRequest = null;
        workspace.PlaylistDetailFilterChanged += (_, request) =>
        {
            raisedCount++;
            if (raisedCount == 1)
            {
                firstRequest = request;
            }
            else
            {
                secondRequest = request;
            }
            Assert.AreEqual("  title:Beta  ", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);
        };

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.All));

        Assert.AreEqual(1, raisedCount);
        Assert.IsNotNull(firstRequest);
        Assert.AreEqual(MainViewUpdateMode.KeywordFilterUpdated, firstRequest.UpdateMode);
        Assert.IsTrue(firstRequest.OwnerRevision > 0L);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailFilterRequest(firstRequest));

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.All));
        Assert.AreEqual(1, raisedCount);

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter._24KEYS | ChartModeFilter._48KEYS));

        Assert.AreEqual(2, raisedCount);
        Assert.IsNotNull(secondRequest);
        Assert.AreEqual(MainViewUpdateMode.ModeFilterUpdated, secondRequest.UpdateMode);
        Assert.IsTrue(secondRequest.OwnerRevision > firstRequest.OwnerRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailFilterRequest(firstRequest));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailFilterRequest(secondRequest));
        Assert.AreEqual(ChartModeFilter._24KEYS | ChartModeFilter._48KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.None));

        Assert.AreEqual(2, raisedCount);
        Assert.AreEqual(ChartModeFilter._24KEYS | ChartModeFilter._48KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);
    }

    /// <summary>共通filterは詳細中だけ受けてsummary・退出中は拒否し、詳細中のNoneも既存filterと通知回数を変えません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceRoutesSharedDetailFilterOnlyWhileDetailIsActive()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("initial", ChartModeFilter.All));
        int raisedCount = 0;
        workspace.PlaylistDetailFilterChanged += (_, _) => raisedCount++;

        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("before-detail", ChartModeFilter.All)));
        Assert.AreEqual("initial", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        workspace.IsPlaylistDetailViewActive = true;
        Assert.IsTrue(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("detail", ChartModeFilter.All)));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual("detail", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        Assert.IsTrue(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("detail", ChartModeFilter.None)));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual(ChartModeFilter.All, workspace.PlaylistDetailFilterSnapshot.ModeFilter);

        workspace.IsPlaylistSummaryMode = true;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("summary", ChartModeFilter.All)));
        Assert.AreEqual("detail", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistDetailViewActive = false;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("after-detail", ChartModeFilter.All)));
    }

    /// <summary>詳細再入時は新しいkeywordと24/48の複合選択へ再同期し、同値の再初期化は通知せず旧画面のfilterを持ち越しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDetailFilterInitializationResynchronizesAcrossDetailEntries()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());

        int raisedCount = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(workspace.PlaylistDetailFilterSnapshot))
            {
                raisedCount++;
            }
        };

        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Alpha", ChartModeFilter.All));
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Beta", ChartModeFilter._24KEYS | ChartModeFilter._48KEYS));
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Beta", ChartModeFilter._24KEYS | ChartModeFilter._48KEYS));

        Assert.AreEqual(2, raisedCount);
        Assert.AreEqual("title:Beta", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);
        Assert.AreEqual(ChartModeFilter._24KEYS | ChartModeFilter._48KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);
    }

    /// <summary>summaryで利用できないmemo fieldを指定すると警告本文と表示flagを通知し、無効な検索条件を無警告で受理しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceKeywordWarning_IsOwnedByPlaylistWorkspace()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        workspace.PlaylistSummaryKeywordFilter = "memo:warning";

        StringAssert.Contains(workspace.PlaylistSummaryKeywordSearchWarningText, "memo");
        Assert.IsTrue(workspace.HasPlaylistSummaryKeywordSearchWarning);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryKeywordSearchWarningText));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.HasPlaylistSummaryKeywordSearchWarning));
    }

    /// <summary>summary開始は見出しを更新して既存要約を保持し、終了は構築tokenを取り消して見出し・要約を消し、二重終了を通知しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryModeTransitionOwnsHeaderTextAndCancellation()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);
        workspace.GridHeaderText = "stale header";
        workspace.PlaylistSummaryText = "stale summary";

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: true));
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
        Assert.AreEqual("stale summary", workspace.PlaylistSummaryText);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.GridHeaderText));

        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest request));
        propertyNames.Clear();

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
        Assert.IsTrue(request.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(string.Empty, workspace.GridHeaderText);
        Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode),
                nameof(PlaylistWorkspaceViewModel.GridHeaderText),
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText)
            },
            propertyNames);

        Assert.IsFalse(workspace.SetPlaylistSummaryMode(enabled: false));
        workspace.CompletePlaylistSummaryDataBuild(request);
    }

    /// <summary>
    /// 保証: 集計Aが判定を済ませて表示世代を開始する前に集計Bが受理・開始しても、静止後はBの行と件数を採用し、Aの結果は途中でも採用しない。
    /// 判定と開始を分けた実装で両方が拒否され、表示が古いまま止まる誤りを検出する。
    /// </summary>
    /// <remarks>窓の中で呼ばれる注入済みログを関門、UI採用の予約をFIFOの保留キューにして、時間待ちなしで重なりを作ります。</remarks>
    [TestMethod]
    public async Task SummaryDataBuilds_OverlappingRequestAdoptsLatestAcceptedBuild()
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(modelUiScheduler: new TestUiScheduler(() => null));
        fixture.Workspace.Dispose();
        var queue = new Queue<(Action Action, TaskCompletionSource<bool> Completion)>();
        bool queueMode = false;
        Task Schedule(Action action)
        {
            if (!queueMode)
            {
                action();
                return Task.CompletedTask;
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue((action, completion));
            return completion.Task;
        }

        PlaylistWorkspaceViewModel? workspaceRef = null;
        bool gateArmed = false;
        int gateFired = 0;
        var t3 = new BMSTable
        {
            playlist_id = 9003,
            name = "T3",
            last_update = new DateTime(2026, 2, 3),
            entries = [new BMSTableEntry { md5 = new string('d', 32) }, new BMSTableEntry { md5 = new string('e', 32) }, new BMSTableEntry { md5 = new string('f', 32) }]
        };
        void Gate(string message)
        {
            if (!gateArmed || !message.StartsWith("playlist_summary_build ", StringComparison.Ordinal))
            {
                return;
            }
            gateArmed = false;
            gateFired++;
            fixture.Playlist.BMSTables.Add(t3);
            workspaceRef!.RequestPlaylistSummaryDataRefresh("second", rebuildAsync: false);
        }

        using PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => fixture.Playlist,
            playlistLibraryProvider: () => fixture.Library, detailRetentionLog: Gate, restoreUiApplyScheduler: Schedule);
        workspaceRef = workspace;
        var builds = new List<(long Generation, Task Build)>();
        workspace.PlaylistSummaryBuildStarted += (generation, build) => builds.Add((generation, build));
        workspace.RequestPlaylistSummaryMode(true);
        workspace.RebuildPlaylistSummaryView(runAsync: false);
        await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        int? t1Id = fixture.Table.playlist_id;
        CollectionAssert.AreEquivalent(new int?[] { t1Id }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
        int t1Charts = workspace.PlaylistSummaryView.Single().TotalCharts;

        fixture.Playlist.BMSTables.Add(new BMSTable
        {
            playlist_id = 9002,
            name = "T2",
            last_update = new DateTime(2026, 2, 2),
            entries = [new BMSTableEntry { md5 = new string('b', 32) }, new BMSTableEntry { md5 = new string('c', 32) }]
        });
        builds.Clear();
        queueMode = true;
        gateArmed = true;
        var snapshots = new List<int?[]>();
        try
        {
            workspace.RequestPlaylistSummaryDataRefresh("first", rebuildAsync: false);
            Assert.AreEqual(1, gateFired, "集計Aの判定後・表示世代開始前の関門で集計Bを受理しています。");
        }
        finally
        {
            gateArmed = false;
            while (true)
            {
                while (queue.Count > 0)
                {
                    (Action action, TaskCompletionSource<bool> completion) = queue.Dequeue();
                    try
                    {
                        action();
                        completion.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                    snapshots.Add(workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
                }
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                if (queue.Count == 0)
                {
                    break;
                }
            }
        }

        int?[] latest = [t1Id, 9002, 9003];
        CollectionAssert.AreEquivalent(latest, workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray(), "静止後は後発の集計Bの行を採用しています。");
        Assert.AreEqual(
            string.Format(BeMusicSeeker.Properties.Resources.Playlist_summary_format, t1Charts + 2 + 3, 3),
            workspace.PlaylistSummaryText);
        int?[] older = [t1Id, 9002];
        Assert.IsFalse(
            snapshots.Any(snapshot => snapshot.Length == older.Length && !snapshot.Except(older).Any()),
            "集計Aの行はBの受理後に一度も採用されません。");
        Assert.AreEqual(builds.Max(build => build.Generation), workspace.AppliedPlaylistSummaryDataGeneration);
        foreach ((long _, Task build) in builds)
        {
            await build;
        }
    }

    /// <summary>表示・data・cacheの各旧世代と非表示modeの結果を拒否し、現行行と要約を旧結果で置換しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyRejectsStaleGenerationAndInactiveMode()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create());
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache();
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        ObservableCollection<PlaylistSummaryRow> originalRows = workspace.PlaylistSummaryView;

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

        workspace.InvalidatePlaylistSummaryCache();
        long currentCacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
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

    /// <summary>summary行通知が失敗しても確定した行と要約を保持して公開失敗を返し、失敗を未適用扱いへ巻き戻しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryPublishFailureAggregatesPropertyPublishFailure()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistWorkspaceDialogService: UndeclaredDialogs.Create())
        {
            IsPlaylistSummaryMode = true
        };
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                throw new InvalidOperationException("rows binding failed");
            }
        };
        Assert.ThrowsExactly<PlaylistSummaryPublishException>(() => workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "committed",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        CollectionAssert.AreEqual(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("committed", workspace.PlaylistSummaryText);
    }

    /// <summary>実DB triggerでBMT順保存を失敗させるとTaskへ元診断を返してrefreshを出さず、DBの二表の旧順を保持します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceBmtPersistenceFailureFaultsTaskAndSuppressesSummaryRefresh()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistWorkspacePresentationStateTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            playlist.CommitBMSTableHeadersToDB([first, second]);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.Execute(
                    "CREATE TRIGGER fail_bmt_sort_insert BEFORE INSERT ON playlist "
                    + "WHEN EXISTS (SELECT 1 FROM playlist WHERE playlist_id = NEW.playlist_id) "
                    + "BEGIN SELECT RAISE(ABORT, 'bmt sort persistence failure'); END;");
                db.Execute(
                    "CREATE TRIGGER fail_bmt_sort_update BEFORE UPDATE OF bmt_sort ON playlist "
                    + "BEGIN SELECT RAISE(ABORT, 'bmt sort persistence failure'); END;");
            }

            PlaylistSummaryBmtSortCoordinator bmtSort =
                new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort);
            int refreshRequestCount = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => refreshRequestCount++;

            Task operation = workspace.ApplyCurrentVisibleBmtOrderAsync(
            [
                new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first }
            ]);
            Exception? failure = null;
            try
            {
                await operation;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            Assert.IsTrue(
                failure!.ToString().Contains("bmt sort persistence failure", StringComparison.Ordinal));
            Assert.AreEqual(0, refreshRequestCount);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(
                    1,
                    verify.ExecuteScalar<int>(
                        "SELECT bmt_sort FROM playlist WHERE playlist_id = ?;",
                        first.playlist_id));
                Assert.AreEqual(
                    2,
                    verify.ExecuteScalar<int>(
                        "SELECT bmt_sort FROM playlist WHERE playlist_id = ?;",
                        second.playlist_id));
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>summaryの一括反映は更新直前の選択識別を旧集合で捕捉し、ResetとBinding公開後に新投影のID索引を照合します。</summary>
    [TestMethod]
    public void SummaryApply_CapturesImmediatelyBeforeReplacementAndCompletesAfterNotifications()
    {
        using PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        void Apply(params int[] ids) => Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow>(ids.Select(id => new PlaylistSummaryRow { PlaylistId = id })),
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));
        Apply(1, 2, 3, 4);
        var source = (BeMusicSeeker.Views.ICustomTableSelectionIdentitySource)workspace.PlaylistSummaryView;
        int selectedIndex = 0;
        object? captured = null;
        var notifications = new List<string>();
        source.SelectionUpdateStarting += (_, _) => { captured = source.GetSelectionIdentity(selectedIndex); notifications.Add("start"); };
        ((System.Collections.Specialized.INotifyCollectionChanged)workspace.PlaylistSummaryView).CollectionChanged += (_, _) => notifications.Add("reset");
        workspace.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(workspace.PlaylistSummaryView)) { notifications.Add("binding"); } };
        source.SelectionUpdateCompleted += (_, args) =>
        {
            Assert.AreEqual(EventArgs.Empty, args, "一般更新には別の明示選択を作りません。");
            Assert.AreEqual(3, captured);
            Assert.AreEqual(3, source.IndexOfSelectionIdentity(captured));
            Assert.AreEqual(-1, source.IndexOfSelectionIdentity(999));
            notifications.Add("complete");
        };
        var pending = new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow>(new[] { 1, 2, 4, 3 }.Select(id => new PlaylistSummaryRow { PlaylistId = id })),
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        };
        // 要求の準備後でも、反映前の選択変更を捕捉対象にします。
        selectedIndex = 2;
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(pending));
        CollectionAssert.AreEqual(new[] { "start", "reset", "binding", "complete" }, notifications);
    }

    /// <summary>行Bindingと共通選択完了通知の失敗を集約し、次の更新も開始・完了を一組として公開します。</summary>
    [TestMethod]
    public void SummaryApply_SelectionCompletionFailureAggregatesWithBindingFailure()
    {
        using PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        var source = (BeMusicSeeker.Views.ICustomTableSelectionIdentitySource)workspace.PlaylistSummaryView;
        int starts = 0, completions = 0;
        source.SelectionUpdateStarting += (_, _) => starts++;
        source.SelectionUpdateCompleted += (_, _) =>
        {
            completions++;
            throw new InvalidOperationException("selection completion failed");
        };
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(workspace.PlaylistSummaryView)) { throw new InvalidOperationException("rows binding failed"); }
        };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            long generation = workspace.BeginPlaylistSummaryPresentationGeneration();
            PlaylistSummaryPublishException failure = Assert.ThrowsExactly<PlaylistSummaryPublishException>(() =>
                workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                {
                    Rows = new ObservableCollection<PlaylistSummaryRow> { new() { PlaylistId = attempt + 1 } },
                    PresentationGeneration = generation
                }));
            var aggregate = failure.InnerException as AggregateException;
            Assert.IsNotNull(aggregate);
            CollectionAssert.AreEquivalent(new[] { "rows binding failed", "selection completion failed" }, aggregate.InnerExceptions.Select(value => value.Message).ToArray());
        }
        source.SelectionUpdateStarting += (_, _) => throw new InvalidOperationException("selection start failed");
        PlaylistSummaryPublishException startFailure = Assert.ThrowsExactly<PlaylistSummaryPublishException>(() =>
            workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
            {
                Rows = new ObservableCollection<PlaylistSummaryRow>(),
                PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
            }));
        StringAssert.Contains(startFailure.ToString(), "selection start failed");
        StringAssert.Contains(startFailure.ToString(), "selection completion failed");
        Assert.AreEqual(3, starts);
        Assert.AreEqual(3, completions);
    }

}
