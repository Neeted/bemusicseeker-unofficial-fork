using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>詳細要求の失効・終端・表示変換と行操作Resolverの判断を確認します。</summary>
/// <remarks>実VMを使う終端ケースはworkspaceとMainChartListの確定・通知接続、再生headerは実PlaybackPanelへの配送が対象です。共有構成資源のケースのみ非並列にします。画面menuの実接続はMainWindow側へ分担します。</remarks>
[TestClass]
public sealed class PlaylistViewPipelineTests
{
    /// <summary>タイトル昇順で二行をhashとともに並べ替え、絞込なしの件数を保ち、並替で行を欠落させません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_SortKeepsAllRowsVisible()
    {
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 7);
        var sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow };
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: ChartModeFilter.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, result.Select(row => row.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "22222222222222222222222222222222", "11111111111111111111111111111111" }, result.Select(row => row.hash).ToArray());
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
    }

    /// <summary>memo検索と7鍵絞込を直接適用し、該当一行と段階件数・計測結果を返してroot依存で検索を迂回しません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_AppliesKeywordModeAndSortWithoutRoot()
    {
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 5);
        PlaylistDetailSourceRow bravoRow = CreateSourceRow("33333333333333333333333333333333", "Bravo", 7, memo: "target");
        var sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow, bravoRow };
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: "target",
            modeFilter: ChartModeFilter._7KEYS,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long keywordStageMs,
            out long modeStageMs,
            out long sortStageMs,
            out long viewMaterializeMs);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Bravo", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
        Assert.IsTrue(keywordStageMs >= 0);
        Assert.IsTrue(modeStageMs >= 0);
        Assert.IsTrue(sortStageMs >= 0);
        Assert.IsTrue(viewMaterializeMs >= 0);
    }

    /// <summary>古い詳細要求は行置換準備を一回取り消し、現行行・列表示・summary設定を維持して旧結果を適用しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistDetailTerminal_StaleRequestCancelsPreparationWithoutApplyingRows()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        var oldRows = new List<object>();
        var candidateRows = new List<object> { new object() };
        viewModel.MainChartList.Rows = oldRows;
        viewModel.MainChartList.ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist = System.Windows.Visibility.Collapsed;
        PlaylistSummaryColumnSettings oldSummaryColumns = viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings;
        PlaylistRequestIdentity identity = CreatePlaylistIdentity("stale-terminal");
        var request = new PlaylistBuildRequest
        {
            RequestVersion = 1,
            Mode = MainViewUpdateMode.PlaylistFilterSelected,
            RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
            Identity = identity
        };
        int preparingCount = 0;
        int canceledCount = 0;
        viewModel.MainChartList.RowsReplacing += (_, _) => preparingCount++;
        viewModel.MainChartList.RowsReplacementCanceled += (_, _) => canceledCount++;

        PlaylistDetailTerminalCommitResult result = viewModel.PlaylistWorkspace.ApplyDetailTerminal(
            CreatePlaylistTerminalRequest(candidateRows, requestVersion: 1));

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, viewModel.MainChartList.Rows);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(oldSummaryColumns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, preparingCount);
        Assert.AreEqual(1, canceledCount);
    }

    /// <summary>summary移行の退役登録で要求世代を進め、summary適用前にも古い詳細終端が現行行を置換できません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryRetirement_InvalidatesOlderDetailTerminalBeforeSummaryApply()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        var oldRows = new List<object>();
        viewModel.MainChartList.Rows = oldRows;
        viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion = 1;
        PlaylistSourceRetirementRequest retirement =
            viewModel.PlaylistWorkspace.PrepareDetailSourceRetirementForRegularView();
        Assert.IsTrue(
            viewModel.PlaylistWorkspace.RegisterPlaylistSummaryDetailSourceRetirement(retirement));
        viewModel.PlaylistWorkspace.RequestPlaylistSummaryMode(enabled: true);

        PlaylistDetailTerminalCommitResult result = viewModel.PlaylistWorkspace.ApplyDetailTerminal(
            CreatePlaylistTerminalRequest(
                new List<object> { new object() },
                requestVersion: 1));

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, viewModel.MainChartList.Rows);
        Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
        Assert.AreEqual(2, viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion);
    }

    /// <summary>新しい退役要求を先に登録した後の古い要求を拒否し、遅れて届いた世代で退役状態を巻き戻しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryRetirementRegistration_KeepsNewestRequestVersion()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        var older = new PlaylistSourceRetirementRequest(1, buildCancellation: null);
        var newer = new PlaylistSourceRetirementRequest(2, buildCancellation: null);

        Assert.IsTrue(
            viewModel.PlaylistWorkspace.RegisterPlaylistSummaryDetailSourceRetirement(newer));
        Assert.IsFalse(
            viewModel.PlaylistWorkspace.RegisterPlaylistSummaryDetailSourceRetirement(older));
    }

    /// <summary>現行sourceから詳細表示を構築して実MainChartListへ同じ最終行を渡し、source世代を保持し、列通知時にbuild/viewロックを残しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistWorkspace_CurrentSourceEntryBuildsAndCommitsMainTableRows()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlaylistDetailSourceRow sourceRow = CreateSourceRow(
            "12121212121212121212121212121212",
            "Current source",
            7);
        viewModel.PlaylistWorkspace.DetailViewState.Source.Rows = [sourceRow];
        viewModel.PlaylistWorkspace.DetailViewState.Source.GenerationId = 4;
        viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion = 1;
        bool buildLockHeldDuringColumnModeEvent = false;
        bool viewLockHeldDuringColumnModeEvent = false;
        viewModel.MainChartList.AppliedColumnModeCommitted += _ =>
        {
            buildLockHeldDuringColumnModeEvent = Monitor.IsEntered(
                viewModel.PlaylistWorkspace.DetailBuildState.SyncRoot);
            viewLockHeldDuringColumnModeEvent = Monitor.IsEntered(
                viewModel.PlaylistWorkspace.DetailViewState.SyncRoot);
        };
        var buildRequest = new PlaylistBuildRequest
        {
            RequestVersion = 1,
            Mode = MainViewUpdateMode.PlaylistFilterSelected,
            RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
            Identity = CreatePlaylistIdentity("workspace-current", libraryIndexVersion: 0)
        };

        PlaylistDetailTerminalApplyResult result = viewModel.PlaylistWorkspace.ApplyDetailFromCurrentSource(
            new PlaylistDetailPresentationRequest
            {
                BuildRequest = buildRequest,
                Mode = MainViewUpdateMode.PlaylistFilterSelected,
                KeywordFilter = string.Empty,
                ModeFilter = ChartModeFilter.All,
                SortParameters = null,
                ColumnSettingMode = MainViewUpdateMode.PlaylistFilterSelected,
                CurrentTreeMode = MainViewUpdateMode.PlaylistFilterSelected,
                Stopwatch = Stopwatch.StartNew()
            });

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(1, result.ViewApply.ViewCount);
        Assert.AreSame(result.ViewApply.FinalRows, viewModel.MainChartList.Rows);
        Assert.AreEqual(4L, viewModel.PlaylistWorkspace.DetailViewState.Source.GenerationId);
        Assert.AreEqual(1L, viewModel.PlaylistWorkspace.DetailViewState.View.GenerationId);
        Assert.IsFalse(buildLockHeldDuringColumnModeEvent);
        Assert.IsFalse(viewLockHeldDuringColumnModeEvent);
    }

    /// <summary>古い要求の取消通知中に別Taskがbuildロックを取得でき、通知をロック内で同期配送する誤りを検出します。</summary>
    [TestMethod]
    public void PlaylistDetailTerminal_StaleCancellationPublishesAfterBuildLockIsReleased()
    {
        var buildState = new PlaylistDetailBuildState { RequestVersion = 2 };
        var viewState = new PlaylistDetailViewState();
        var table = new MainChartListViewModel
        {
            Rows = new List<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)
        };
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            _ => { },
            _ => { },
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
        table.RowsReplacementCanceled += (_, _) =>
        {
            var lockProbe = Task.Run(() =>
            {
                lock (buildState.SyncRoot)
                {
                }
            });
            Assert.IsTrue(lockProbe.Wait(TimeSpan.FromSeconds(5)), "RowsReplacementCanceled must run after the playlist build lock is released.");
        };

        PlaylistDetailTerminalCommitResult result = workspace.ApplyDetailTerminal(
            CreatePlaylistTerminalRequest(new List<object> { new object() }, requestVersion: 1));

        Assert.IsFalse(result.Applied);
    }

    /// <summary>現行世代でもtoken取消済みなら詳細終端を拒否し、候補行をMainChartListへ適用しません。</summary>
    [TestMethod]
    public void PlaylistDetailTerminal_CanceledRequestCannotCommitAtTerminalBoundary()
    {
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var viewState = new PlaylistDetailViewState();
        var table = new MainChartListViewModel { Rows = new List<object>() };
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            _ => { },
            _ => { },
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
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        PlaylistDetailTerminalRequest request = CreatePlaylistTerminalRequest(
            new List<object> { new object() },
            requestVersion: 1);
        request.CancellationToken = cancellation.Token;

        PlaylistDetailTerminalCommitResult result = workspace.ApplyDetailTerminal(request);

        Assert.IsFalse(result.Applied);
        Assert.AreEqual(0, table.Rows.Count);
    }

    /// <summary>行確定後にMainChartList通知が失敗しても候補行・列modeを保持し、旧行を一回解放してworkspace通知も継続します。</summary>
    [TestMethod]
    public void PlaylistDetailTerminal_PostCommitFailureStillDisposesAndPublishesAllOwners()
    {
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var viewState = new PlaylistDetailViewState();
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            _ => { },
            _ => { },
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
        var oldRow = new TrackingDisposableRow();
        var oldRows = new List<object> { oldRow };
        var candidateRows = new List<object> { new object() };
        table.Rows = oldRows;
        viewState.View.Rows = oldRows;
        int tableNotifications = 0;
        int workspaceNotifications = 0;
        MainViewUpdateMode? columnModeAtRowsNotification = null;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                tableNotifications++;
                columnModeAtRowsNotification = table.LastAppliedColumnMode;
                throw new InvalidOperationException("table publish failed");
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        PlaylistDetailTerminalPublishException exception = Assert.ThrowsException<PlaylistDetailTerminalPublishException>(
            () => workspace.ApplyDetailTerminal(
                CreatePlaylistTerminalRequest(candidateRows, requestVersion: 1)));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.IsNotNull(exception.TerminalCommitResult);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, exception.TerminalCommitResult.AppliedColumnMode);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, columnModeAtRowsNotification);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, table.LastAppliedColumnMode);
        Assert.AreSame(candidateRows, table.Rows);
        Assert.AreEqual(1, oldRow.DisposeCount);
        Assert.IsTrue(tableNotifications > 0);
        Assert.IsTrue(workspaceNotifications > 0);
    }

    /// <summary>旧行のDisposeが失敗しても後続旧行の解放とtable/workspace通知を続け、確定済み候補と所有権移譲を保持します。</summary>
    [TestMethod]
    public void PlaylistDetailTerminal_DisposeFailureDoesNotSuppressRemainingPublishers()
    {
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var viewState = new PlaylistDetailViewState();
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            _ => { },
            _ => { },
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
        var oldRow = new TrackingDisposableRow(throwOnDispose: true);
        var laterRow = new TrackingDisposableRow();
        var oldRows = new List<object> { oldRow, laterRow };
        var candidateRows = new List<object> { new object() };
        table.Rows = oldRows;
        viewState.View.Rows = oldRows;
        int tableNotifications = 0;
        int workspaceNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                tableNotifications++;
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        PlaylistDetailTerminalPublishException exception = Assert.ThrowsException<PlaylistDetailTerminalPublishException>(
            () => workspace.ApplyDetailTerminal(
                CreatePlaylistTerminalRequest(candidateRows, requestVersion: 1)));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.IsNotNull(exception.TerminalCommitResult);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, exception.TerminalCommitResult.AppliedColumnMode);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, table.LastAppliedColumnMode);
        Assert.AreSame(candidateRows, table.Rows);
        Assert.AreEqual(1, oldRow.DisposeCount);
        Assert.AreEqual(1, laterRow.DisposeCount);
        Assert.IsTrue(tableNotifications > 0);
        Assert.IsTrue(workspaceNotifications > 0);
    }

    /// <summary>source退役を確定して編集・世代・保留要求を消した後にtoken取消を公開し、既存MainTable行を重複解放しません。</summary>
    [TestMethod]
    public void PlaylistDetailTerminal_SourceClearCommitsStateBeforePublishingCancellation()
    {
        var pendingRequest = new PlaylistBuildRequest { RequestVersion = 7 };
        using var buildCancellation = new CancellationTokenSource();
        var buildState = new PlaylistDetailBuildState
        {
            RequestVersion = 7,
            PendingRequest = pendingRequest,
            CurrentBuildRequest = pendingRequest,
            CurrentBuildCancellation = buildCancellation
        };
        var sourceRows = new List<PlaylistDetailSourceRow> { CreateSourceRow("11111111111111111111111111111111", "Source", 7) };
        var viewRow = new TrackingDisposableRow();
        var viewRows = new List<object> { viewRow };
        var viewState = new PlaylistDetailViewState
        {
            CurrentOpenInteraction = new PlaylistOpenInteractionState { RequestVersion = 7 }
        };
        viewState.Source.Rows = sourceRows;
        viewState.Source.GenerationId = 11;
        viewState.Source.CurrentFolderName = "folder";
        viewState.Source.LastBuiltLibraryIndexVersion = 3;
        viewState.Source.IsPlaylistCellEditing = true;
        viewState.View.Rows = viewRows;
        viewState.View.GenerationId = 13;
        viewState.View.LastAppliedCount = 1;
        PlaylistSourceClearCommitResult commit = buildState.CommitSourceClear(viewState);

        Assert.AreEqual(8, buildState.RequestVersion);
        Assert.IsNull(buildState.PendingRequest);
        Assert.IsNull(buildState.CurrentBuildRequest);
        Assert.IsFalse(buildCancellation.IsCancellationRequested);
        Assert.AreSame(sourceRows, commit.SourceRows);
        Assert.AreSame(viewRows, commit.ViewRows);
        Assert.AreEqual(11, commit.PreviousGenerationId);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
        Assert.AreEqual(0, viewState.Source.GenerationId);
        Assert.AreEqual(0, viewState.View.GenerationId);
        Assert.AreEqual(0, viewState.View.LastAppliedCount);
        Assert.IsNull(viewState.Source.CurrentFolderName);
        Assert.AreEqual(0, viewState.Source.LastBuiltLibraryIndexVersion);
        Assert.IsFalse(viewState.Source.IsPlaylistCellEditing);
        Assert.IsNull(viewState.CurrentOpenInteraction);
        Assert.AreEqual(0, viewRow.DisposeCount, "Source clear must not duplicate main-table row disposal.");

        buildState.PublishSourceClear(commit);

        Assert.IsTrue(buildCancellation.IsCancellationRequested);
        Assert.AreEqual(0, viewRow.DisposeCount);
    }

    /// <summary>退役済みtokenが先にDisposeされてもsource取消の公開を失敗させず、後片付け競合を許容します。</summary>
    [TestMethod]
    public void PlaylistDetailTerminal_SourceClearPublishIgnoresDisposedCancellation()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Dispose();
        var buildState = new PlaylistDetailBuildState();
        var commit = new PlaylistSourceClearCommitResult(null, null, 0, cancellation);

        buildState.PublishSourceClear(commit);
    }

    /// <summary>並替済み仮想viewは索引取得まで表示行を生成せず、同じ索引の再取得は生成済み行を再利用します。</summary>
    [TestMethod]
    public void ApplyPlaylistVirtualViewFromSource_DoesNotMaterializeRowsUntilIndexed()
    {
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 7);
        var sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow };
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        PlaylistDetailVirtualView view = PlaylistDetailPresentationService.ApplyVirtualViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: ChartModeFilter.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long viewMaterializeMs);

        Assert.AreEqual(2, view.Count);
        Assert.AreEqual(2, view.RowCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
        Assert.IsTrue(viewMaterializeMs >= 0);

        var first = (PlaylistDetailRow)view[0];

        Assert.AreEqual("Alpha", first.Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.IsTrue(ReferenceEquals(first, view[0]));
        Assert.AreEqual(1, view.RealizedRowCount);
    }

    /// <summary>BMS・bmsonの既存表示行と重複行は最新再生状態を読む一方、未所持は未送信印だけを保ち、編集値・scoreを再生成で失いません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlaylistDetailOwnedRowsReadCurrentPlaybackStatusAndPreserveEdits(bool bmson)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            PlaylistDetailSourceRow source = CreateOwnedBmsSourceRow(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Owned", 7, out ChartFile file);
            if (bmson)
            {
                source = new PlaylistDetailSourceRow(source.Entry,
                    (ChartTestValues.Empty(ChartFileKind.Bmson) with
                    {
                        Path = "Owned.bmson",
                        RawTitle = "Owned",
                        ModeHint = "beat-7k"
                    }), scoreSnapshot: new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true });
            }
            ChartFileStatus currentStatus = ChartFileStatus.NONE;
            source.PlaybackStatusProvider = _ => currentStatus;
            var missingEntry = new TestablePlaylistEntry();
            missingEntry.SetTitle("Missing");
            var missing = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null,
                scoreSnapshot: new BMSScore { IsLr2IrScoreUnsent = true });
            var view = new PlaylistDetailVirtualView([source, source, missing]);
            try
            {
                var first = (PlaylistDetailRow)view[0];
                var duplicate = (PlaylistDetailRow)view[1];
                var absent = (PlaylistDetailRow)view[2];
                first.comment = "edited comment";
                first.memo = "edited memo";
                first.Level = "9";
                foreach (ChartFileStatus status in new[] { ChartFileStatus.LOADING, ChartFileStatus.PLAY, ChartFileStatus.PAUSE, ChartFileStatus.PLAY, ChartFileStatus.NONE })
                {
                    currentStatus = status;
                    Assert.AreEqual(status | ChartFileStatus.SCORE_UNSENT, first.status);
                    Assert.AreEqual(status | ChartFileStatus.SCORE_UNSENT, duplicate.status);
                    Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, absent.status);
                    Assert.AreSame(first, view[0]);
                    Assert.AreEqual(3, view.RealizedRowCount);
                }
                Assert.AreEqual(212, first.score);
                Assert.AreEqual("edited comment", first.comment);
                Assert.AreEqual("edited memo", first.memo);
                Assert.AreEqual("9", first.Level);
                Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, file.Status);
            }
            finally
            {
                view.DisposeRealizedRows();
            }
        });
    }

    /// <summary>BMS・bmsonの未実体化行を後から生成しても現在のPause状態とscoreを読み、構築時のPlay状態を固定しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlaylistDetailVirtualView_UnrealizedRowUsesLatestPlaybackStatusWhenCreated(bool bmson)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            PlaylistDetailSourceRow source = CreateOwnedBmsSourceRow(
                "cccccccccccccccccccccccccccccccc", "Unrealized", 7, out ChartFile file);
            if (bmson)
            {
                source = new PlaylistDetailSourceRow(source.Entry,
                    (ChartTestValues.Empty(ChartFileKind.Bmson) with
                    {
                        Path = "Unrealized.bmson",
                        RawTitle = "Unrealized",
                        ModeHint = "beat-7k"
                    }), scoreSnapshot: new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true });
            }
            ChartFileStatus currentStatus = ChartFileStatus.PLAY;
            source.PlaybackStatusProvider = _ => currentStatus;
            var view = new PlaylistDetailVirtualView([source, source]);
            try
            {
                var first = (PlaylistDetailRow)view[0];
                Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.SCORE_UNSENT, first.status);
                currentStatus = ChartFileStatus.PAUSE;
                Assert.AreEqual(1, view.RealizedRowCount);
                var late = (PlaylistDetailRow)view[1];
                Assert.AreEqual(2, view.RealizedRowCount);
                Assert.AreEqual(ChartFileStatus.PAUSE | ChartFileStatus.SCORE_UNSENT, first.status);
                Assert.AreEqual(first.status, late.status);
                Assert.AreEqual(212, late.score);
                currentStatus = ChartFileStatus.NONE;
                Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, late.status);
            }
            finally
            {
                view.DisposeRealizedRows();
            }
        });
    }

    /// <summary>大文字検索語をmemoに照合して該当行だけを返し、譜面タイトルだけの検索でplaylist固有memoを落としません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_KeywordFilterMatchesPlaylistMemoAndComment()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow("44444444444444444444444444444444", "Filtered", 7, comment: "ordinary");
        var sourceRows = new PlaylistDetailSourceRow[] { matchedRow, filteredRow };
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: "SPECIAL",
            modeFilter: ChartModeFilter.All,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
    }

    /// <summary>title・memo・MD5・SHA256の複数指定をANDで評価し、memoだけ一致する別行を誤って採りません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_KeywordFilterSupportsAndAndHashFields()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow(
            "33333333333333333333333333333333",
            "Matched",
            7,
            memo: "special memo",
            sha256: "abababababababababababababababababababababababababababababababab");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow(
            "44444444444444444444444444444444",
            "Filtered",
            7,
            memo: "special memo",
            sha256: "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
        var sourceRows = new PlaylistDetailSourceRow[] { matchedRow, filteredRow };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: "title:Matched memo:special md5:333333 sha256:abab",
            modeFilter: ChartModeFilter.All,
            sortParameters: new ChartListSortParameters { ColumnsName = nameof(ChartFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
    }

    /// <summary>未知の検索fieldは一致なしとして各段階件数をゼロにし、未知fieldを通常文字列として曖昧受理しません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_UnknownFieldQueryDoesNotMatch()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            [matchedRow],
            keywordFilter: "unknown:Matched",
            modeFilter: ChartModeFilter.All,
            sortParameters: new ChartListSortParameters { ColumnsName = nameof(ChartFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(0, keywordCount);
        Assert.AreEqual(0, modeCount);
    }

    /// <summary>引用memo・否定comment・hashのOR・title正規表現を組み合わせ、全条件を満たす一行だけを返します。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_KeywordFilterSupportsQuoteNegationOrAndRegex()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow(
            "33333333333333333333333333333333",
            "Matched Alpha",
            7,
            memo: "special memo",
            comment: "safe comment",
            sha256: "abababababababababababababababababababababababababababababababab");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow(
            "44444444444444444444444444444444",
            "Filtered Alpha",
            7,
            memo: "special memo",
            comment: "ordinary comment",
            sha256: "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            [matchedRow, filteredRow],
            keywordFilter: "memo:\"special memo\" -comment:ordinary md5:333333|555555 sha256:abab|efef title:re:^matched",
            modeFilter: ChartModeFilter.All,
            sortParameters: new ChartListSortParameters { ColumnsName = nameof(ChartFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched Alpha", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
    }

    /// <summary>7鍵・14鍵のsourceから14鍵だけを選び、keyword段階の二件とmode段階の一件を区別して返します。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_ModeFilterRecomputesFromSourceRows()
    {
        PlaylistDetailSourceRow sevenKeysRow = CreateSourceRow("55555555555555555555555555555555", "SevenKeys", 7);
        PlaylistDetailSourceRow fourteenKeysRow = CreateSourceRow("66666666666666666666666666666666", "FourteenKeys", 14);
        var sourceRows = new PlaylistDetailSourceRow[] { sevenKeysRow, fourteenKeysRow };
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: ChartModeFilter._14KEYS,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("FourteenKeys", result[0].Title);
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(1, modeCount);
    }

    /// <summary>同じsourceを二回適用しても各表示行を独立生成し、前回の編集可能な表示snapshotを次回へ共有しません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_RebuildsDetachedSnapshotsForEachApply()
    {
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("77777777777777777777777777777777", "Alpha", 7);
        var sourceRows = new PlaylistDetailSourceRow[] { alphaRow };
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> first = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: ChartModeFilter.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);
        List<PlaylistDetailRow> second = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: ChartModeFilter.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.IsFalse(ReferenceEquals(first[0], second[0]));
    }

    /// <summary>playlistのlevel 2.5・3・12を数値昇順へ並べ、文字列順や譜面側levelで小数の順序を誤りません。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_LevelSortUsesPlaylistEntryDoubleValueNumerically()
    {
        PlaylistDetailSourceRow entryLevelTwelve = CreateSourceRow("88888888888888888888888888888888", "Twelve", 7, entryLevel: 12);
        PlaylistDetailSourceRow entryLevelTwoPointFive = CreateSourceRow("99999999999999999999999999999999", "TwoPointFive", 7, entryLevel: 2.5);
        PlaylistDetailSourceRow entryLevelThree = CreateSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Three", 7, entryLevel: 3);
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Level),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            [entryLevelTwelve, entryLevelTwoPointFive, entryLevelThree],
            keywordFilter: null,
            modeFilter: ChartModeFilter.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);

        CollectionAssert.AreEqual(new[] { "2.5", "3", "12" }, result.Select(row => row.Level).ToArray());
        CollectionAssert.AreEqual(new[] { "TwoPointFive", "Three", "Twelve" }, result.Select(row => row.Title).ToArray());
    }

    /// <summary>要求作成でfolder前後空白と検索語の空白・大小文字を正規化し、同じ検索意図を別要求へ分割しません。</summary>
    [TestMethod]
    public void CreatePlaylistRequestIdentity_NormalizesKeywordAndFolderForDedup()
    {
        var table = new BMSTable();
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };

        PlaylistRequestIdentity left = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, " FolderA ", PlaylistDetailFilter.PlaylistFilter, " keyword ", ChartModeFilter._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, scoreSnapshotVersion: 30, chartInfoIndexVersion: 40, hasResolvedSelection: true);
        PlaylistRequestIdentity right = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "FolderA", PlaylistDetailFilter.PlaylistFilter, "KEYWORD", ChartModeFilter._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, scoreSnapshotVersion: 30, chartInfoIndexVersion: 40, hasResolvedSelection: true);

        Assert.AreEqual(left, right);
    }

    /// <summary>同じ選択でもplaylist revisionが進んだ要求を区別し、更新前のsourceと同一視しません。</summary>
    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentPlaylistRevisionBreaksDedup()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, null, PlaylistDetailFilter.PlaylistNotOwnedFilterSelected, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, null, PlaylistDetailFilter.PlaylistNotOwnedFilterSelected, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 5, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    /// <summary>score snapshot世代だけ進んだ要求も区別し、古いscore表示を同じ要求として省略しません。</summary>
    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentScoreSnapshotVersionBreaksDedup()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 6, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    /// <summary>chartInfo世代を通常要求では区別し、chartInfoだけを無視する比較では等価として差分patchの判断を支えます。</summary>
    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentChartInfoIndexVersionBreaksDedup()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
        Assert.IsTrue(before.SourceIdentity.EqualsIgnoringChartInfoIndex(after.SourceIdentity));
    }

    /// <summary>検索語・鍵数・並替の変更は表示identityだけを変え、同じsourceの不必要な再構築と表示要求の誤dedupを防ぎます。</summary>
    [TestMethod]
    public void PlaylistIdentity_KeywordModeAndSortOnlyChangePresentationIdentity()
    {
        var table = new BMSTable();
        var titleAscending = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Ascending
        };
        var titleDescending = new ChartListSortParameters
        {
            ColumnsName = nameof(ChartFile.Title),
            Direction = ListSortDirection.Descending
        };

        PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, "alpha", ChartModeFilter.All, titleAscending, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, "beta", ChartModeFilter._7KEYS, titleDescending, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreEqual(before.SourceIdentity, after.SourceIdentity);
        Assert.AreNotEqual(before.PresentationIdentity, after.PresentationIdentity);
        Assert.AreNotEqual(before, after);
    }

    /// <summary>library世代の変更はsource identityだけを変え、表示条件が同じことを理由に古いsourceを再利用しません。</summary>
    [TestMethod]
    public void PlaylistIdentity_SourceVersionsOnlyChangeSourceIdentity()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, "keyword", ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, "keyword", ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 4, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before.SourceIdentity, after.SourceIdentity);
        Assert.AreEqual(before.PresentationIdentity, after.PresentationIdentity);
        Assert.AreNotEqual(before, after);
    }

    /// <summary>score世代だけの失効をscore由来として分類し、別の失効理由へ誤記録しません。</summary>
    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenOnlyScoreSnapshotChanges_ReturnsScoreSnapshotVersion()
    {
        string reason = PlaylistRequestFactory.DetermineSourceInvalidationReason(
            selectionChanged: false,
            libraryIndexInvalidated: false,
            playlistRevisionInvalidated: false,
            scoreSnapshotInvalidated: true,
            chartInfoIndexInvalidated: false,
            sourceMissing: false);

        Assert.AreEqual("score_snapshot_version", reason);
    }

    /// <summary>chartInfo世代だけの失効をmetadata由来として分類し、score変更と混同しません。</summary>
    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenOnlyChartInfoIndexChanges_ReturnsChartInfoIndex()
    {
        string reason = PlaylistRequestFactory.DetermineSourceInvalidationReason(
            selectionChanged: false,
            libraryIndexInvalidated: false,
            playlistRevisionInvalidated: false,
            scoreSnapshotInvalidated: false,
            chartInfoIndexInvalidated: true,
            sourceMissing: false);

        Assert.AreEqual("chart_info_index", reason);
    }

    /// <summary>選択と全依存が同時に変わった場合は選択変更を優先理由とし、依存の変化で主因を隠しません。</summary>
    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenSelectionChanges_TakesPrecedence()
    {
        string reason = PlaylistRequestFactory.DetermineSourceInvalidationReason(
            selectionChanged: true,
            libraryIndexInvalidated: true,
            playlistRevisionInvalidated: true,
            scoreSnapshotInvalidated: true,
            chartInfoIndexInvalidated: true,
            sourceMissing: true);

        Assert.AreEqual("selection_changed", reason);
    }

    /// <summary>検索語と鍵数だけが変わる要求はsourceを再構築せず表示だけを適用し、表示変更をsource失効と混同しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenOnlyPresentationChanges_AppliesViewOnly()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, "alpha", ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, "beta", ChartModeFilter._7KEYS, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.ApplyViewOnly, decision.Action);
        Assert.IsTrue(decision.PresentationIdentityChanged);
        Assert.IsNull(decision.SourceInvalidationReason);
    }

    /// <summary>score世代だけ進んだ場合はsourceを再構築し、旧score世代と失効理由を返して古いscoreを再利用しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenScoreSnapshotChanges_RebuildsSource()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 6, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.ScoreSnapshotInvalidated);
        Assert.AreEqual("score_snapshot_version", decision.SourceInvalidationReason);
        Assert.AreEqual(5, decision.LastBuiltScoreSnapshotVersion);
    }

    /// <summary>chartInfoだけの世代変更はsource全再構築へ進まずpatch後表示を選び、metadata更新を無視しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenOnlyChartInfoIndexChanges_PatchesThenAppliesView()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.PatchChartInfoThenApplyView, decision.Action);
        Assert.IsTrue(decision.ChartInfoIndexInvalidated);
        Assert.IsNull(decision.SourceInvalidationReason);
    }

    /// <summary>空名folderでも同じ選択のchartInfo更新はpatch後表示とし、空名を選択欠損と誤判定しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenEmptyFolderChartInfoOnlyChanges_PatchesThenAppliesView()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, string.Empty, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, string.Empty, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.PatchChartInfoThenApplyView, decision.Action);
        Assert.IsFalse(decision.SelectionChanged);
        Assert.IsTrue(decision.ChartInfoIndexInvalidated);
    }

    /// <summary>別playlist選択とchartInfo更新が重なる場合は選択変更としてsourceを再構築し、旧表へのpatchで済ませません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenSelectionAndChartInfoChange_RebuildsSource()
    {
        var currentTable = new BMSTable();
        var requestTable = new BMSTable();
        PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(currentTable, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(requestTable, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.SelectionChanged);
        Assert.AreEqual("selection_changed", decision.SourceInvalidationReason);
    }

    /// <summary>解決済み選択の空sourceは表示だけを適用し、ゼロ件を未構築と誤判定して再構築を繰り返しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenResolvedSourceHasNoRows_DoesNotTreatSourceAsMissing()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(identity),
            CreatePlaylistDetailBuildStateSnapshot(identity, sourceRowCount: 0));

        Assert.AreEqual(PlaylistDetailBuildAction.ApplyViewOnly, decision.Action);
        Assert.IsFalse(decision.SourceMissing);
    }

    /// <summary>source未保持のsnapshotは未構築として再構築を選び、解決済みゼロ件と混同しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenSnapshotRowsAreNull_RebuildsAsSourceMissing()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, "Folder", PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(identity),
            CreatePlaylistDetailBuildStateSnapshot(identity, hasSourceRows: false, sourceRowCount: 0));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.SourceMissing);
        Assert.AreEqual("source_missing", decision.SourceInvalidationReason);
    }

    /// <summary>初回要求を受けてworkerを開始し、世代を付けた同じ要求と前score世代を取り出せるため、受付だけで仕事を落としません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueueRegister_WhenNewRequest_EnqueuesAndStartsWorker()
    {
        var state = new PlaylistDetailBuildState();
        PlaylistRequestIdentity identity = CreatePlaylistIdentity("Folder");
        PlaylistBuildRequest request = CreatePlaylistBuildRequest(identity);

        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            request,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 5,
            isShutdownRequested: false);

        Assert.IsTrue(result.Enqueued);
        Assert.IsTrue(result.StartWorker);
        Assert.AreEqual(1, request.RequestVersion);
        Assert.AreEqual(5, result.LastBuiltScoreSnapshotVersion);
        Assert.IsTrue(state.WorkerRunning);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(state, out PlaylistBuildRequest dequeued));
        Assert.AreSame(request, dequeued);
        Assert.AreSame(request, state.CurrentBuildRequest);
    }

    /// <summary>保留なしでworkerを停止後、次の明示要求はenqueueとworker開始を行い、停止状態に仕事を置き去りにしません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueue_WhenNoPendingRequest_StopsAtomicallyAndNextRegisterStartsWorker()
    {
        var state = new PlaylistDetailBuildState { WorkerRunning = true };

        Assert.IsFalse(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(state, out _));
        Assert.IsFalse(state.WorkerRunning);

        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            CreatePlaylistBuildRequest(CreatePlaylistIdentity("next")),
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);

        Assert.IsTrue(result.Enqueued);
        Assert.IsTrue(result.StartWorker);
    }

    /// <summary>終了取消後にworkerが停止しても後続要求を拒否し、呼出元のshutdown引数がfalseでもqueueを再開しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueue_StopAfterShutdown_DoesNotReopenQueue()
    {
        var state = new PlaylistDetailBuildState { WorkerRunning = true };
        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(state);

        Assert.IsFalse(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(state, out _));
        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            CreatePlaylistBuildRequest(CreatePlaylistIdentity("late")),
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);

        Assert.IsTrue(state.ShutdownCancellationRequested);
        Assert.IsFalse(result.Enqueued);
        Assert.AreEqual("shutdown", result.DeduplicatedTarget);
        Assert.IsFalse(state.WorkerRunning);
    }

    /// <summary>worker失敗時に保留があればworker所有を次の実行へ残し、保留要求を捨てて停止しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueue_FailureWithPendingRequest_TransfersWorkerOwnership()
    {
        var state = new PlaylistDetailBuildState
        {
            WorkerRunning = true,
            PendingRequest = CreatePlaylistBuildRequest(CreatePlaylistIdentity("pending"))
        };

        bool restart = PlaylistDetailBuildQueueCoordinator.FinishWorkerAfterFailure(state);

        Assert.IsTrue(restart);
        Assert.IsTrue(state.WorkerRunning);
        Assert.IsNotNull(state.PendingRequest);
    }

    /// <summary>現行表示と同じ要求はenqueueせず保留もworkerも作らず、同一意図を再構築しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueueRegister_WhenCurrentViewMatches_DeduplicatesNoop()
    {
        var state = new PlaylistDetailBuildState();
        PlaylistRequestIdentity identity = CreatePlaylistIdentity("Folder");
        PlaylistBuildRequest request = CreatePlaylistBuildRequest(identity);

        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            request,
            currentViewIdentity: identity,
            lastBuiltScoreSnapshotVersion: 5,
            isShutdownRequested: false);

        Assert.IsFalse(result.Enqueued);
        Assert.AreEqual("current_view", result.DeduplicatedTarget);
        Assert.AreEqual("noop_same_view", result.IgnoredReason);
        Assert.IsFalse(state.WorkerRunning);
        Assert.IsNull(state.PendingRequest);
    }

    /// <summary>実行Xから保留Yを経て最新Xへ戻るとYを新Xへ置換して実行tokenを取り消し、古い保留Yを最終意図として残しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueueRegister_WhenLatestIntentReturnsToRunningIdentity_ReplacesPendingRequest()
    {
        var state = new PlaylistDetailBuildState();
        PlaylistBuildRequest runningX = CreatePlaylistBuildRequest(CreatePlaylistIdentity("X"));
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            runningX,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(state, out _));
        using var runningCancellation = new CancellationTokenSource();
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryBeginIteration(
            state,
            runningX,
            runningCancellation,
            isShutdownRequested: false));
        PlaylistBuildRequest pendingY = CreatePlaylistBuildRequest(CreatePlaylistIdentity("Y"));
        PlaylistBuildQueueRegisterResult pendingResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            pendingY,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        pendingResult.PreviousCancellation?.Cancel();
        PlaylistBuildRequest latestX = CreatePlaylistBuildRequest(runningX.Identity);

        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            latestX,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        result.PreviousCancellation?.Cancel();

        Assert.IsTrue(result.Enqueued);
        Assert.AreSame(latestX, state.PendingRequest);
        Assert.AreNotSame(pendingY, state.PendingRequest);
        Assert.IsTrue(runningCancellation.IsCancellationRequested);
    }

    /// <summary>終了時は保留を消して実行と共通tokenを取り消し、受付済み詳細構築を終了後へ残しません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueueCancelForShutdown_ClearsPendingAndCancelsTokens()
    {
        var state = new PlaylistDetailBuildState();
        PlaylistRequestIdentity identity = CreatePlaylistIdentity("Folder");
        PlaylistBuildRequest request = CreatePlaylistBuildRequest(identity);
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            request,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 5,
            isShutdownRequested: false);
        using var activeCancellation = new CancellationTokenSource();
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryBeginIteration(state, request, activeCancellation, isShutdownRequested: false));

        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(state);

        Assert.IsNull(state.PendingRequest);
        Assert.IsTrue(state.ShutdownCancellationRequested);
        Assert.IsTrue(activeCancellation.IsCancellationRequested);
        Assert.IsTrue(state.Cancellation.IsCancellationRequested);
    }

    /// <summary>既にDisposeされた実行・共通tokenでも終了状態を確定し、後片付け競合で終了取消を失敗させません。</summary>
    [TestMethod]
    public void PlaylistDetailBuildQueueCancelForShutdown_ToleratesAlreadyDetachedDisposalRace()
    {
        var state = new PlaylistDetailBuildState { WorkerRunning = true };
        var disposedCancellation = new CancellationTokenSource();
        state.CurrentBuildCancellation = disposedCancellation;
        state.Cancellation = disposedCancellation;
        disposedCancellation.Dispose();

        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(state);

        Assert.IsTrue(state.ShutdownCancellationRequested);
    }

    /// <summary>表全体のroot選択と空名folder選択を別要求とし、空文字の正規化で選択範囲を失いません。</summary>
    [TestMethod]
    public void CreatePlaylistRequestIdentity_DistinguishesRootPlaylistAndEmptyFolderNode()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity rootPlaylist = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        PlaylistRequestIdentity emptyFolder = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.Folder, string.Empty, PlaylistDetailFilter.PlaylistFilter, null, ChartModeFilter.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(rootPlaylist, emptyFolder);
    }

    /// <summary>通常rootと通常folder全体は別sourceと判定して再構築し、同じ表・空folder名でも選択scopeを同一視しません。</summary>
    [TestMethod]
    public void CreatePlaylistRequestIdentity_DistinguishesOrdinaryRootAndOverallNormalFolderScope()
    {
        var table = new BMSTable();
        PlaylistRequestIdentity ordinaryRoot = PlaylistRequestFactory.CreateIdentity(
            table,
            PlaylistDetailSelectionScope.OrdinaryRoot,
            folderName: null,
            PlaylistDetailFilter.PlaylistFilter,
            keywordFilter: null,
            ChartModeFilter.All,
            sortParameters: null,
            libraryIndexVersion: 3,
            playlistRevision: 4,
            scoreSnapshotVersion: 5,
            chartInfoIndexVersion: 6,
            hasResolvedSelection: true);
        PlaylistRequestIdentity overallNormalFolders = PlaylistRequestFactory.CreateIdentity(
            table,
            PlaylistDetailSelectionScope.OverallNormalFolders,
            folderName: null,
            PlaylistDetailFilter.PlaylistFilter,
            keywordFilter: null,
            ChartModeFilter.All,
            sortParameters: null,
            libraryIndexVersion: 3,
            playlistRevision: 4,
            scoreSnapshotVersion: 5,
            chartInfoIndexVersion: 6,
            hasResolvedSelection: true);

        Assert.AreNotEqual(ordinaryRoot, overallNormalFolders);
        Assert.AreNotEqual(ordinaryRoot.SourceIdentity, overallNormalFolders.SourceIdentity);
        Assert.IsFalse(ordinaryRoot.SourceIdentity.EqualsIgnoringChartInfoIndex(overallNormalFolders.SourceIdentity));

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(overallNormalFolders),
            CreatePlaylistDetailBuildStateSnapshot(ordinaryRoot));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.SelectionChanged);
        Assert.AreEqual("selection_changed", decision.SourceInvalidationReason);
    }

    /// <summary>表示行のmemo・comment・小数level・両URL編集をsourceへ同期し、検索文字列に新memoと新commentを含めることを確認します。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_SynchronizeEditableSnapshot_UpdatesEditableFieldsAndSearchText()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Editable", 7, memo: "old memo", comment: "old comment", entryLevel: 3);
        PlaylistDetailRow editedRow = sourceRow.CreateViewRow();

        editedRow.comment = "new comment";
        editedRow.memo = "new memo";
        editedRow.Level = "12.5";
        editedRow.Url = new Uri("https://example.com/original");
        editedRow.Url_diff = new Uri("https://example.com/diff");

        sourceRow.SynchronizeEditableSnapshot(editedRow);

        Assert.AreEqual("new comment", sourceRow.comment);
        Assert.AreEqual("new memo", sourceRow.memo);
        Assert.AreEqual("12.5", sourceRow.Level);
        Assert.AreEqual(12.5, sourceRow.EntryLevelSortKey);
        Assert.AreEqual(new Uri("https://example.com/original"), sourceRow.Url);
        Assert.AreEqual(new Uri("https://example.com/diff"), sourceRow.Url_diff);
        StringAssert.Contains(sourceRow.SearchText, "NEW MEMO");
        StringAssert.Contains(sourceRow.SearchText, "NEW COMMENT");
    }

    /// <summary>sourceから表示行と外部操作Resolverまで同じSHA256を引き渡し、表示実体化で外部操作用hashを落としません。</summary>
    [TestMethod]
    public void PlaylistDetailRow_PreservesSha256SnapshotAcrossViewMaterialization()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "ShaVisible", 7, sha256: "abababababababababababababababababababababababababababababababab");

        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("abababababababababababababababababababababababababababababababab", sourceRow.sha256);
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", row.sha256);
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", GridRowResolver.GetSha256(row));
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", GridRowResolver.GetExternalActionSha256(row));
    }

    /// <summary>譜面にSHA256がないBMS表示行はchartInfo providerのhashを外部操作へ渡し、譜面側欠損だけで操作情報を失いません。</summary>
    [TestMethod]
    public void GridRowResolver_GetExternalActionSha256_UsesBmsChartRowChartInfoFallback()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "ChartInfoSha" + ".bms", Title = "ChartInfoSha", RawTitle = "ChartInfoSha", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(new string('d', 64), file.Md5);
        var row = LibraryChartRow.FromChartFile(file);
        row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        Assert.AreEqual(new string('d', 64), GridRowResolver.GetExternalActionSha256(row));
    }

    /// <summary>BMSの結合タイトル・分割subtitle・artistを表示helperへ渡し、結合文字列からsubtitleを誤って取り出しません。</summary>
    [TestMethod]
    public void GridRowResolver_BmsPlayerDisplayHelpers_ReadRawBmsFile()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "PlayerTitle" + ".bms", Title = "PlayerTitle", RawTitle = "PlayerTitle", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Subtitle = "[PlayerSubtitle]", RawSubtitle = "[PlayerSubtitle]", Title = "PlayerTitle [PlayerSubtitle]" };
        file = file with { Artist = "PlayerArtist", RawArtist = "PlayerArtist" };

        Assert.AreEqual("PlayerTitle [PlayerSubtitle]", GridRowResolver.GetDisplayTitle(file));
        Assert.AreEqual("[PlayerSubtitle]", GridRowResolver.GetDisplaySubtitle(file));
        Assert.AreEqual("PlayerArtist", GridRowResolver.GetDisplayArtist(file));
    }

    /// <summary>実PlaybackPanelのheader設定はBMSのtitle・subtitle・artistを各表示経路へ渡し、subtitleをtitleへ重ねません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SetBmsPlayerHeader_UsesSplitBmsMetadata()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "ouroVoros" + ".bms", Title = "ouroVoros", RawTitle = "ouroVoros", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Subtitle = "[LAST BOSS]" };
        file = file with { Artist = "Nepentropy Movie:Vogeln obj:sak", RawArtist = "Nepentropy Movie:Vogeln obj:sak" };

        PlaybackPanelViewModel panel = viewModel.PlaybackPanel;
        panel.SetBmsPlayerHeader(file);

        Assert.AreSame(file, panel.DisplayedChart);
        Assert.AreEqual("ouroVoros", panel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[LAST BOSS]", panel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("Nepentropy Movie:Vogeln obj:sak", panel.BmsPlayerHeaderArtist);
        Assert.AreEqual("ouroVoros", panel.PlayerHeaderTitle);
        Assert.AreEqual("[LAST BOSS]", panel.PlayerHeaderSubtitle);
        Assert.AreEqual("Nepentropy Movie:Vogeln obj:sak", panel.PlayerHeaderArtist);
    }

    /// <summary>再生開始の入口も表示中譜面と両headerへtitle・subtitle・artistを同期し、前譜面のheaderを残しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void BeginPlayback_SynchronizesPlayerHeader()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "NextTitle" + ".bms", Title = "NextTitle", RawTitle = "NextTitle", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Subtitle = "[NextSubtitle]" };
        file = file with { Artist = "NextArtist", RawArtist = "NextArtist" };

        PlaybackPanelViewModel panel = viewModel.PlaybackPanel;
        panel.BeginPlayback(file, 0);

        Assert.AreSame(file, panel.DisplayedChart);
        Assert.AreEqual("NextTitle", panel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[NextSubtitle]", panel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("NextArtist", panel.BmsPlayerHeaderArtist);
        Assert.AreEqual("NextTitle", panel.PlayerHeaderTitle);
        Assert.AreEqual("[NextSubtitle]", panel.PlayerHeaderSubtitle);
        Assert.AreEqual("NextArtist", panel.PlayerHeaderArtist);
    }

    /// <summary>所有tokenのない解決済みBMS snapshotでも所持行としてplayer・操作targetへ同じパスを渡し、token欠損で実譜面を未所持扱いしません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_ImmutableResolvedBmsKeepsPlayerOwner()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Immutable BMS" + ".bms", Title = "Immutable BMS", RawTitle = "Immutable BMS", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var resolvedRef = LibraryChartRef.FromImmutableSnapshot(LibraryChartRef.FromChartFile((file)));
        ChartFile resolvedChart = resolvedRef.ToChartFileIdentity();
        var entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            resolvedChartRef: resolvedRef).CreateViewRow();

        Assert.IsTrue(row.IsOwned);
        Assert.IsNull(row.Chart.Token);
        Assert.AreEqual(file.Path, row.Chart.Path);
        Assert.IsTrue(GridRowResolver.TryGetBmsChart(row, out ChartFile playerFile));
        Assert.AreEqual(file.Path, playerFile.Path);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(file.Path, target.Chart.Path);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
    }

    /// <summary>解決済みimmutable BMS行はchartInfo providerのlevel・notesを表示へ採り、snapshot側にmetadataがないために欠損表示しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_ImmutableResolvedBmsUsesChartInfoProjection()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Immutable BMS" + ".bms", Title = "Immutable BMS", RawTitle = "Immutable BMS", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Sha256 = new string('b', 64) };
        var resolvedRef = LibraryChartRef.FromImmutableSnapshot(LibraryChartRef.FromChartFile((file)));
        ChartFile resolvedChart = resolvedRef.ToChartFileIdentity();
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(file.Sha256, file.Md5, level: 12, notes: 2000);
        var entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo),
            resolvedChartRef: resolvedRef).CreateViewRow();

        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual(12d, row.ChartLevelSortKey);
        Assert.AreEqual(2000, row.ChartNotes);
    }

    /// <summary>source・chartInfoともSHA256がない行は外部操作用hashを返さず、MD5をSHA256として代用しません。</summary>
    [TestMethod]
    public void GridRowResolver_GetExternalActionSha256_ReturnsNullWhenMissing()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "NoSha", 7);

        Assert.IsNull(GridRowResolver.GetExternalActionSha256(sourceRow.CreateViewRow()));
    }

    /// <summary>bmsonの結合タイトル・subtitle・artist・mode・パス・SHA256をsource/view/Resolverへ渡し、BMS専用経路へ誤変換しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedChart_UsesBmsonMetadata()
    {
        var entry = new TestablePlaylistEntry
        {
            comment = "comment",
            memo = "memo"
        };
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Bmson",
            Title = "Bmson [Another]",
            Subtitle = "[Another]",
            RawArtist = "Artist",
            Artist = "Artist",
            Genre = "Genre",
            Level = 11,
            Mode = 7,
            ModeHint = "beat-7k",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64)
        };
        bmson = bmson with { Score = ChartScoreSnapshot.NoScore(bmson.Path) };
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);

        var sourceRow = new PlaylistDetailSourceRow(entry, (bmson));
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.IsTrue(sourceRow.IsOwned);
        Assert.IsNull(row.Chart.Token);

        Assert.AreSame(sourceRow.Chart, row.Chart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(sourceRow, out ChartFile sourceChart));
        Assert.AreSame(sourceRow.Chart, sourceChart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(row, out ChartFile rowChart));
        Assert.AreSame(row.Chart, rowChart);
        Assert.AreEqual(ChartFileKind.Bmson, rowChart.Kind);

        Assert.AreEqual("Bmson [Another]", row.Title);
        Assert.AreEqual("Artist", row.Artist);
        Assert.AreEqual("[Another]", GridRowResolver.GetDisplaySubtitle(row));
        Assert.AreEqual("Genre", row.genre);
        Assert.AreEqual(7, row.mode);
        Assert.AreEqual(bmson.Path, row.path);
        Assert.AreEqual(bmson.Sha256, row.sha256);
        Assert.AreEqual(bmson.Sha256, GridRowResolver.GetExternalActionSha256(row));
        Assert.AreEqual(ClearType.NO_PLAY, row.clear);
        Assert.AreEqual(RankType.INVALID, row.rank);
        Assert.IsNull(row.score);
        Assert.AreEqual(ChartFileStatus.NONE, row.status);
        Assert.IsFalse(GridRowResolver.TryGetBmsChart(row, out _));
    }

    /// <summary>bmson所持行へ解決済みscoreを渡すとlamp・rank・EXscore・notes・combo・BPを表示し、譜面の未取得scoreで上書きしません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedWithResolvedScore_UsesScoreSnapshot()
    {
        var entry = new TestablePlaylistEntry();
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            RawTitle = "Bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64)
        };
        var score = new BMSScore
        {
            hash = bmson.Md5,
            clear = ClearType.HARD,
            rank = RankType.AA,
            perfect = 800,
            great = 100,
            totalnotes = 1000,
            maxcombo = 900,
            minbp = 7
        };
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);

        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            (bmson),
            scoreSnapshot: score);

        Assert.AreEqual(ClearType.HARD, sourceRow.clear);
        Assert.AreEqual(RankType.AA, sourceRow.rank);
        Assert.AreEqual(1700, sourceRow.score);
        Assert.AreEqual(1000, sourceRow.totalnotes);
        Assert.AreEqual(900, sourceRow.maxcombo);
        Assert.AreEqual(7, sourceRow.minbp);
    }

    /// <summary>bmsonの参照表snapshotを表示と検索へ映し、参照表更新後の新sourceだけを更新して旧sourceの表示を変えません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedChart_UsesPlaylistReferenceProjection()
    {
        var entry = new TestablePlaylistEntry();
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            RawTitle = "Bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64)
        };
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);
        var table = new BMSTable
        {
            name = "Bmson Playlist",
            symbol = "BMSN",
            entries = [entry]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));

        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            (bmson),
            playlistReferenceDisplayProvider: chart => index.Find(chart));
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("BMSN", sourceRow.RefTablesSymbols);
        Assert.AreEqual("Bmson Playlist", sourceRow.RefTablesNames);
        Assert.AreEqual("BMSN", row.RefTablesSymbols);
        Assert.AreEqual("Bmson Playlist", row.RefTablesNames);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("BMSN").MatchesPlaylistDetail(sourceRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Bmson Playlist\"").MatchesPlaylistDetail(sourceRow));

        table.symbol = "REPLACED";
        table.name = "Replaced Bmson Playlist";
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));

        var refreshedSourceRow = new PlaylistDetailSourceRow(
            entry,
            (bmson),
            playlistReferenceDisplayProvider: chart => index.Find(chart));
        Assert.AreEqual("REPLACED", refreshedSourceRow.RefTablesSymbols);
        Assert.AreEqual("Replaced Bmson Playlist", refreshedSourceRow.RefTablesNames);
        Assert.AreEqual("BMSN", sourceRow.RefTablesSymbols);
        Assert.AreEqual("Bmson Playlist", sourceRow.RefTablesNames);
    }

    /// <summary>playlistの所持bmson行をBMS player境界から分離し、所持targetからlibrary参照へbmsonのパスとMD5を渡します。</summary>
    [TestMethod]
    public void ChartOperationTarget_PlaylistOwnedBmson_IsOwnedWithBmsonChartFile()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Bmson",
            RawArtist = "Artist",
            Level = 11,
            ModeHint = "beat-7k",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, (bmson)).CreateViewRow();

        Assert.IsFalse(GridRowResolver.TryGetBmsChart(row, out _));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreSame(row.Chart, target.Chart);
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.IsNull(target.Chart.Token);

        var libraryRef = target.ToLibraryChartRef();
        Assert.AreEqual(ChartFileKind.Bmson, libraryRef.Kind);
        Assert.AreEqual(bmson.Path, libraryRef.Path);
        Assert.AreEqual(bmson.Md5, libraryRef.Md5);
    }

    /// <summary>所持bmson・未所持playlist・非譜面で欠損用menuを選び分け、BMS取得可否を所持判定へ流用しません。</summary>
    [TestMethod]
    public void PlaylistMissingContextMenuPolicy_UsesChartOperationTargetOwnership()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Owned Bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64)
        };
        var ownedBmsonEntry = new TestablePlaylistEntry();
        ownedBmsonEntry.SetMd5(bmson.Md5);
        ownedBmsonEntry.SetSha256(bmson.Sha256);
        PlaylistDetailRow ownedBmsonRow = new PlaylistDetailSourceRow(ownedBmsonEntry, (bmson)).CreateViewRow();

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetTitle("Missing");
        missingEntry.SetMd5("abababababababababababababababab");
        PlaylistDetailRow missingRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null).CreateViewRow();

        Assert.IsFalse(GridRowResolver.TryGetBmsChart(ownedBmsonRow, out _));
        Assert.IsFalse(MainWindow.ShouldUsePlaylistMissingContextMenu(ownedBmsonRow, ChartOperationSourceScope.PlaylistOwned));
        Assert.IsTrue(MainWindow.ShouldUsePlaylistMissingContextMenu(missingRow, ChartOperationSourceScope.PlaylistOwned));
        Assert.IsFalse(MainWindow.ShouldUsePlaylistMissingContextMenu(new object(), ChartOperationSourceScope.PlaylistOwned));
    }


    /// <summary>解決済み投影のtitle・artist・genre・mode・level・導入先・警告を優先し、元譜面やentryの古いmetadataへ戻しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_UsesResolvedChartProjectionBeforeStorageOwnerMetadata()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Storage Title",
            RawArtist = "Storage Artist",
            Genre = "Storage Genre",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64),
            Level = 3,
            ModeHint = "beat-7k"
        };
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("Entry Title");
        entry.SetArtist("Entry Artist");
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);
        ChartFile projectedChart = new(
            ChartFileKind.Bmson,
            bmson.Path,
            bmson.Md5,
            bmson.Sha256,
            "Projected Title",
            "Projected Raw Title",
            "Projected Artist",
            "Projected Genre",
            "Projected Folder",
            "Projected Tag",
            "12",
            12d,
            14,
            chartInfo: null,
            installDestination: "C:\\Installed\\Bmson",
            warnings: [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "projected warning")]);

        var sourceRow = new PlaylistDetailSourceRow(entry, projectedChart);
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("Projected Title", row.Title);
        Assert.AreEqual("Projected Artist", row.Artist);
        Assert.AreEqual("Projected Genre", row.genre);
        Assert.AreEqual("Projected Tag", row.tag);
        Assert.AreEqual(14, row.mode);
        Assert.AreEqual("12", row.Level);
        Assert.AreEqual("C:\\Installed\\Bmson", row.instl_dst);
        Assert.AreEqual("projected warning", row.DisplayWarning);
        Assert.IsTrue(row.HasHighlightedWarning);
        Assert.AreSame(projectedChart, sourceRow.Chart);
        Assert.AreSame(projectedChart, row.Chart);
    }


    /// <summary>bmson source更新では新titleと最新導入先・警告を採り、資源あり/なし両条件で旧rowに保持した資源snapshotを失いません。</summary>
    [TestMethod]
    public void LibraryChartRow_BmsonChartProjectionUsesUpdatedStorageSongAfterSourceSongReplacement()
    {
        ChartFile oldSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Old Bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64)
        };
        ChartFile newSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = oldSong.Path,
            Folder = oldSong.Folder,
            RawTitle = "New Bmson",
            Md5 = oldSong.Md5,
            Sha256 = oldSong.Sha256
        };
        oldSong = oldSong with { Resources = TestChartResources.Create(audio: [@"..\audio\foo.v2.flac"], banner: @".\banner.jpeg") };
        newSong = newSong with { Resources = TestChartResources.Create(audio: ["different.wav"]) };
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            (newSong),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        foreach (bool includeResources in new[] { false, true })
        {
            var row = LibraryChartRow.FromChartFile(oldSong with
            { Resources = includeResources ? oldSong.Resources : null });
            ChartFile previous = row.Chart;
            int transientStateLookupCount = 0;
            row.SetChartTransientStateProvider((chart, includeWarningSnapshot) =>
            {
                transientStateLookupCount++;

                return ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot);
            });

            row.UpdateSourceProjection(newSong
                with
            { Token = previous.Token, Resources = previous.Resources });
            ChartFile chart = row.Chart;

            Assert.AreEqual("New Bmson", chart.RawTitle);
            Assert.AreSame(includeResources ? oldSong.Resources : null, chart.Resources);
            Assert.AreEqual("C:\\Installed\\Bmson", chart.InstallDestination);
            Assert.IsTrue(chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            Assert.IsTrue(transientStateLookupCount > 0);
        }
    }


    /// <summary>所持BMS行のsubtitleを表示し、操作targetからlibrary参照へ種類・パス・MD5を引き渡して別譜面を指定しません。</summary>
    [TestMethod]
    public void ChartOperationTarget_PlaylistOwnedBms_PreservesSubtitleAndLibraryReference()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Owned Bms" + ".bms", Title = "Owned Bms", RawTitle = "Owned Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Sha256 = new string('a', 64) };
        file = file with { Subtitle = "Another" };
        var entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, (file)).CreateViewRow();

        Assert.AreEqual("Another", GridRowResolver.GetDisplaySubtitle(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.AreEqual(file.Path, target.Chart.Path);
        var libraryRef = target.ToLibraryChartRef();
        Assert.AreEqual(ChartFileKind.Bms, libraryRef.Kind);
        Assert.AreEqual(file.Path, libraryRef.Path);
        Assert.AreEqual(file.Md5, libraryRef.Md5);
    }


    /// <summary>通常bmson表示行から操作targetとlibrary参照へ種類・パス・MD5を渡し、bmsonをBMS参照へ誤変換しません。</summary>
    [TestMethod]
    public void ChartOperationTarget_LibraryChartRowBmson_CreatesLibraryReference()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\lazy.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Lazy Bmson",
            RawArtist = "Artist",
            Md5 = "56565656565656565656565656565656",
            Sha256 = new string('5', 64)
        };
        var row = LibraryChartRow.FromChartFile((bmson));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));

        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.IsNull(target.Chart.Token);


        var libraryRef = target.ToLibraryChartRef();

        Assert.AreEqual(ChartFileKind.Bmson, libraryRef.Kind);
        Assert.AreEqual(bmson.Path, libraryRef.Path);
        Assert.AreEqual(bmson.Md5, libraryRef.Md5);
    }

    /// <summary>tokenなしBMS・bmsonとパスだけの参照で種類・パス・MD5を保ち、実譜面identityへの変換で別種類や別パスにしません。</summary>
    [TestMethod]
    public void LibraryChartRef_PreservesKindPathAndHashForDetachedCharts()
    {
        ChartFile bms = ChartTestValues.Empty();
        bms = bms with { Md5 = "abababababababababababababababab", Path = "BMS" + ".bms", Title = "BMS", RawTitle = "BMS", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        bms = bms with { Sha256 = new string('a', 64) };
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Bmson",
            Md5 = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
            Sha256 = new string('c', 64)
        };

        var bmsRef = LibraryChartRef.FromChartFile((bms));
        var bmsonRef = LibraryChartRef.FromChartFile((bmson));
        var pathOnlyRef = LibraryChartRef.FromPath(
            ChartFileKind.Bmson,
            "C:\\Songs\\Missing\\missing.bmson",
            "efefefefefefefefefefefefefefefef",
            new string('e', 64));

        Assert.AreEqual(bms.Path, bmsRef.Path);
        Assert.AreEqual(bms.Md5, bmsRef.Md5);
        Assert.AreEqual(ChartFileKind.Bms, bmsRef.Kind);
        Assert.IsNull(bmsRef.ToChartFileIdentity().Token);
        Assert.AreEqual(ChartFileKind.Bmson, bmsonRef.Kind);
        Assert.AreEqual(bmson.Path, bmsonRef.Path);
        Assert.AreEqual(bmson.Md5, bmsonRef.Md5);
        Assert.IsNull(bmsonRef.ToChartFileIdentity().Token);
        Assert.AreEqual(ChartFileKind.Bmson, pathOnlyRef.Kind);
        Assert.IsNull(pathOnlyRef.Token);
        Assert.AreEqual(pathOnlyRef.Path, pathOnlyRef.ToChartFileIdentity().Path);
    }

    /// <summary>bmsonの操作targetからpackage項目を生成するとbmson種類を維持し、BMS専用譜面として取り出しません。</summary>
    [TestMethod]
    public void ChartOperationTarget_ToPackageChartEntry_PreservesBmsonKindWithoutBmsChart()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\loose-entry.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Loose Entry Bmson",
            RawArtist = "Artist",
            Md5 = "68686868686868686868686868686868"
        };
        var target = new ChartOperationTarget(
            (bmson),
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        var entry = target.ToPackageChartEntry();

        Assert.IsNotNull(entry);
        Assert.IsNull(entry.GetBmsChartForTest());
        Assert.AreEqual(ChartFileKind.Bmson, entry.Chart.Kind);
    }

    /// <summary>BMS targetからpackage項目へ元のBMS譜面そのものを渡し、別snapshotへ置換して後続の所有者を失いません。</summary>
    [TestMethod]
    public void ChartOperationTarget_ToPackageChartEntry_UsesBmsStorageOwner()
    {
        ChartFile file = CreateBmsFile("C:\\Songs\\Bms\\chart.bms", "BMS", "Artist", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var target = new ChartOperationTarget(
            (file),
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        var entry = target.ToPackageChartEntry();

        Assert.IsNotNull(entry);
        Assert.AreSame(file, entry.GetBmsChartForTest());
        Assert.AreSame(file.Token, entry.Chart.Token);
    }

    /// <summary>実LibraryChartRowのbmsonをResolver経由で自動rename要求に受理し、一対象の要求を作ってbmsonだけを誤拒否しません。</summary>
    [TestMethod]
    public void ChartFolderAutoRenameRequest_AcceptsBmsonRow()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\auto-rename.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Auto Rename Bmson",
            RawArtist = "Artist",
            Md5 = "67676767676767676767676767676767",
            Sha256 = new string('6', 64)
        };
        var row = LibraryChartRow.FromChartFile((bmson));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(ChartFolderAutoRenameRequest.TryCreate([target], out ChartFolderAutoRenameRequest request));

        Assert.AreEqual(1, request.Charts.Count);
        Assert.IsTrue(request.HasTargets);

    }

    /// <summary>実bmson行から移動要求へ種類・パス・MD5と新親directoryを渡し、対象譜面や移動先を取り違えません。</summary>
    [TestMethod]
    public void ChartLibraryMoveRequest_PreservesBmsonIdentityAndNewParent()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\move.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Move Bmson",
            RawArtist = "Artist",
            Md5 = "68686868686868686868686868686868",
            Sha256 = new string('8', 64)
        };
        var row = LibraryChartRow.FromChartFile((bmson));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(ChartLibraryMoveRequest.TryCreate([target], "D:\\Songs", out ChartLibraryMoveRequest request));

        Assert.AreEqual(1, request.Charts.Count);
        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual("D:\\Songs", request.NewParentDirectory);
        Assert.AreEqual(bmson.Path, request.Charts[0].Path);
        Assert.AreEqual(bmson.Md5, request.Charts[0].Md5);
        Assert.AreEqual(ChartFileKind.Bmson, request.Charts[0].Kind);
    }

    /// <summary>導入先なしのloose bmsonは修復項目をmaterializeしても導入先なしを保ち、存在しない導入先を生成しません。</summary>
    [TestMethod]
    public void RepairInstalledLocationRequest_LooseBmsonKeepsNoInstallDestinationAfterMaterialization()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\repair.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Repair Bmson",
            RawArtist = "Artist",
            Md5 = "68686868686868686868686868686868",
            Sha256 = new string('8', 64)
        };
        var row = LibraryChartRow.FromChartFile((bmson));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.IsFalse(request.HasInstallDestination);
        Assert.AreEqual(string.Empty, request.RepairCharts[0].InstallDestination);

        request.MaterializeRepairEntries();

        Assert.IsFalse(request.HasInstallDestination);
    }

    /// <summary>playlist所持bmsonでも元譜面に導入先がなければ修復先を空のまま保ち、別scopeの導入先で補いません。</summary>
    [TestMethod]
    public void RepairInstalledLocationRequest_PlaylistBmsonWithoutDestinationKeepsRepairDestinationEmpty()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\repair-playlist.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Repair Playlist Bmson",
            RawArtist = "Artist",
            Md5 = "70707070707070707070707070707070",
            Sha256 = new string('a', 64)
        };
        ChartFile staleChart = (bmson);
        var target = new ChartOperationTarget(
            staleChart,
            playlistEntry: null,
            ChartOperationSourceScope.PlaylistOwned,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RepairInstalledLocation);

        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest request));

        Assert.IsFalse(request.HasInstallDestination);
        Assert.AreEqual(string.Empty, request.RepairCharts[0].InstallDestination);

    }

    /// <summary>同じMD5の別パス二譜面へ異なる導入先を持たせると各パスの導入先を採り、hash一致だけで片方へ統合しません。</summary>
    [TestMethod]
    public void RepairInstalledLocationRequest_RepairChartsOverlayUsesPathBeforeHash()
    {
        string md5 = "71717171717171717171717171717171";
        ChartFile firstBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\duplicate-a.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Duplicate A",
            Md5 = md5
        };
        ChartFile secondBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\duplicate-b.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Duplicate B",
            Md5 = md5
        };
        ChartFile firstChart = ChartFileProjection.WithPackageState(
            (firstBmson),
            "C:\\Installed\\A",
            string.Empty,
            string.Empty,
            []);
        ChartFile secondChart = ChartFileProjection.WithPackageState(
            (secondBmson),
            "C:\\Installed\\B",
            string.Empty,
            string.Empty,
            []);
        var firstTarget = new ChartOperationTarget(
            firstChart,
            null,
            ChartOperationSourceScope.PlaylistOwned,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RepairInstalledLocation);
        var secondTarget = new ChartOperationTarget(
            secondChart,
            null,
            ChartOperationSourceScope.PlaylistOwned,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RepairInstalledLocation);

        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [firstTarget, secondTarget],
            out RepairInstalledLocationRequest request));

        Assert.AreEqual("C:\\Installed\\A", request.RepairCharts[0].InstallDestination);
        Assert.AreEqual("C:\\Installed\\B", request.RepairCharts[1].InstallDestination);
    }

    /// <summary>導入先を持つ実bmson表示行から修復要求へその導入先を引き渡し、bmsonだけ導入先なしと誤判定しません。</summary>
    [TestMethod]
    public void RepairInstalledLocationRequest_UsesExistingBmsonInstallDestination()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\repair-existing.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Repair Existing Bmson",
            RawArtist = "Artist",
            Md5 = "69696969696969696969696969696969",
            Sha256 = new string('9', 64)
        };
        var row = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            (bmson),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            []));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.IsTrue(request.HasInstallDestination);
        Assert.AreEqual("C:\\Installed\\Bmson", request.RepairCharts[0].InstallDestination);
    }

    /// <summary>package項目のない保留bmsonはloose対象として受理し、明示materialize後にbmson項目を返して対象を欠落させません。</summary>
    [TestMethod]
    public void PendingInstallDestinationClearRequest_MaterializesLooseBmsonEntryOnRequest()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\pending-loose.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Pending Loose Bmson",
            RawArtist = "Artist",
            Md5 = "69696969696969696969696969696969",
            Sha256 = new string('9', 64)
        };
        ChartFile chart = (bmson);
        var target = new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
            [target],
            out PendingInstallDestinationClearRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual(0, request.PackageTargets.Count);
        Assert.AreEqual(1, request.LooseTargets.Count);

        request.MaterializeLooseEntries();

        Assert.AreEqual(1, request.LooseEntries.Count);
        Assert.AreEqual(ChartFileKind.Bmson, request.LooseEntries[0].Chart.Kind);
        Assert.IsNull(request.LooseEntries[0].GetBmsChartForTest());
    }

    /// <summary>package項目のない保留bmsonから編集項目を取得し、導入先と表示titleを適用してBMS専用経路へ流しません。</summary>
    [TestMethod]
    public void PendingInstallDestinationEditRequest_AppliesDestinationToLooseBmsonEntry()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\pending-edit.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Pending Edit Bmson",
            RawArtist = "Artist",
            Md5 = "70707070707070707070707070707070",
            Sha256 = new string('a', 64)
        };
        ChartFile chart = (bmson);
        var target = new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(
            target,
            out PendingInstallDestinationEditRequest request));

        Assert.IsTrue(request.HasTarget);
        Assert.IsNull(request.PackageEntry);
        Assert.IsNotNull(request.ChartFile);
        Assert.AreEqual(ChartFileKind.Bmson, request.ChartFile.Kind);

        PackageChartEntry editEntry = request.GetOrCreateChartEntry();
        editEntry.ApplyInstallDestinationMetadata("C:\\Installed\\Bmson", "Installed Bmson", "Installed Artist");

        Assert.IsNull(editEntry.GetBmsChartForTest());
        Assert.AreEqual("C:\\Installed\\Bmson", editEntry.Chart.InstallDestination);
        Assert.AreEqual("Installed Bmson", editEntry.Chart.InstallDestinationTitle);
    }

    /// <summary>実LibraryChartRowの所持bmsonをfolder編集Resolver経由でrename要求へ受理し、BMSだけの条件で拒否しません。</summary>
    [TestMethod]
    public void RenameChartFolderRequest_AcceptsBmsonRow()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\rename.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Rename Bmson",
            RawArtist = "Artist",
            Md5 = "71717171717171717171717171717171",
            Sha256 = new string('b', 64)
        };
        var row = LibraryChartRow.FromChartFile((bmson));

        Assert.IsTrue(GridRowResolver.TryGetFolderEditChartOperationTarget(row, ChartOperationSourceScope.Library, out ChartOperationTarget target));
        Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));

        Assert.IsTrue(request.HasTarget);

    }

    /// <summary>bmsonの最新maintenance encoding・healthと導入先・警告を同じ表示譜面へ合成し、一方の投影が他方を落としません。動画0件は対象外のnullを維持します。</summary>
    [TestMethod]
    public void LibraryChartRow_BmsonChartCombinesFreshSongMaintenanceWithTransientState()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\maintenance.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Maintenance Bmson",
            RawArtist = "Artist",
            Md5 = "67676767676767676767676767676767",
            Sha256 = new string('6', 64),
            ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { hash = "67676767676767676767676767676767", wav_files_defined = 10, wav_files_existing = 10, bga_files_defined = 8, bga_files_existing = 8, movie_files_defined = 0, encoding = "utf-8" })
        };
        bmson = bmson with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { hash = "67676767676767676767676767676767", wav_files_defined = 10, wav_files_existing = 10, bga_files_defined = 8, bga_files_existing = 8, movie_files_defined = 0, encoding = "utf-16" }) };
        bmson = ChartFileProjection.WithMaintenance(bmson, bmson.ResourceHealthMaintenanceSnapshot);
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            (bmson),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        var row = LibraryChartRow.FromChartFile(statefulChart);

        ChartFile chart = row.Chart;

        Assert.AreEqual(100, chart.WAVHealth);
        Assert.AreEqual(100, chart.BGAHealth);
        Assert.IsNull(chart.MovieHealth);
        Assert.AreEqual("utf-16", chart.EncodingName);
        Assert.AreEqual("C:\\Installed\\Bmson", chart.InstallDestination);
        Assert.IsTrue(chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    /// <summary>通常行とplaylist行のtarget再解決・source再作成でも導入先と警告詳細を保持し、修復対象の一時状態を失いません。</summary>
    [TestMethod]
    public void ChartOperationTarget_BmsonRowsUseChartTransientStateForInstallLocationRepair()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Bmson",
            RawArtist = "Artist",
            Md5 = "34343434343434343434343434343434",
            Sha256 = new string('3', 64)
        };
        ChartFile libraryChart = ChartFileProjection.WithPackageState(
            (bmson),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        var libraryRow = LibraryChartRow.FromChartFile(libraryChart);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(libraryRow, out ChartOperationTarget firstLibraryTarget));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(libraryRow, out ChartOperationTarget secondLibraryTarget));

        Assert.AreEqual(firstLibraryTarget.Chart.Kind, secondLibraryTarget.Chart.Kind);
        Assert.AreEqual(firstLibraryTarget.Chart.Path, secondLibraryTarget.Chart.Path);
        Assert.AreEqual(firstLibraryTarget.Chart.PrimaryLookupHash, secondLibraryTarget.Chart.PrimaryLookupHash);
        Assert.AreSame(firstLibraryTarget.Chart.Token, secondLibraryTarget.Chart.Token);
        Assert.AreEqual("C:\\Installed\\Bmson", secondLibraryTarget.Chart.InstallDestination);
        Assert.IsTrue(secondLibraryTarget.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        Assert.AreEqual("C:\\Installed\\Bmson", libraryRow.instl_dst);
        Assert.IsTrue(libraryRow.HasHighlightedWarning);
        StringAssert.Contains(libraryRow.WarningTooltipText, "ambiguous install destination");

        var rebuiltWarningRow = LibraryChartRow.FromChartFile(libraryChart);
        Assert.IsTrue(rebuiltWarningRow.HasHighlightedWarning);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltWarningRow.Chart.InstallDestination);
        StringAssert.Contains(rebuiltWarningRow.WarningTooltipText, "ambiguous install destination");

        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);
        ChartFile playlistChart = ChartFileProjection.WithPackageState(
            (bmson),
            "C:\\Installed\\PlaylistBmson",
            "Installed Playlist Bmson",
            "Installed Playlist Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "playlist ambiguous install destination")]);
        PlaylistDetailRow playlistRow = new PlaylistDetailSourceRow(
            entry,
            playlistChart).CreateViewRow();
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(playlistRow, out ChartOperationTarget firstPlaylistTarget));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(playlistRow, out ChartOperationTarget secondPlaylistTarget));

        Assert.AreSame(firstPlaylistTarget.Chart, secondPlaylistTarget.Chart);
        Assert.AreEqual("C:\\Installed\\PlaylistBmson", secondPlaylistTarget.Chart.InstallDestination);

        var rebuiltPlaylistSourceRow = new PlaylistDetailSourceRow(
            entry,
            playlistChart);
        Assert.AreEqual("C:\\Installed\\PlaylistBmson", rebuiltPlaylistSourceRow.instl_dst);
        Assert.AreEqual("C:\\Installed\\PlaylistBmson", rebuiltPlaylistSourceRow.Chart.InstallDestination);
        Assert.AreEqual("Installed Playlist Bmson", rebuiltPlaylistSourceRow.InstallDestinationTitle);
        Assert.AreEqual("Installed Playlist Artist", rebuiltPlaylistSourceRow.InstallDestinationArtist);
        Assert.IsTrue(rebuiltPlaylistSourceRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        StringAssert.Contains(rebuiltPlaylistSourceRow.WarningTooltipText, "playlist ambiguous install destination");
    }

    /// <summary>通常bmson sourceを再構築してもprovider由来の導入先・title・artist・警告をviewへ渡し、再構築で修復状態を消しません。</summary>
    [TestMethod]
    public void ChartListSourceRow_BmsonUsesTransientStateForInstallRepairState()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Bmson",
            RawArtist = "Artist",
            Md5 = "45454545454545454545454545454545",
            Sha256 = new string('4', 64)
        };
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            (bmson),
            "C:\\Installed\\Bmson",
            "Installed Bmson",
            "Installed Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);

        ChartListSourceRow firstSourceRow = ChartListSourceRow.BuildStandardLibraryRows(
            [(bmson)],
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot)).Single();

        ChartListSourceRow rebuiltSourceRow = ChartListSourceRow.BuildStandardLibraryRows(
            [(bmson)],
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot)).Single();
        var rebuiltViewRow = LibraryChartRow.FromChartFile(rebuiltSourceRow.Chart);

        Assert.AreEqual("C:\\Installed\\Bmson", firstSourceRow.InstallDestination);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltSourceRow.InstallDestination);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltSourceRow.Chart.InstallDestination);
        Assert.AreEqual("Installed Bmson", rebuiltSourceRow.InstallDestinationTitle);
        Assert.AreEqual("Installed Artist", rebuiltSourceRow.InstallDestinationArtist);
        Assert.IsTrue(rebuiltSourceRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        StringAssert.Contains(rebuiltSourceRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationAmbiguous);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltViewRow.instl_dst);
        Assert.IsTrue(rebuiltViewRow.HasHighlightedWarning);
        StringAssert.Contains(rebuiltViewRow.WarningTooltipText, "ambiguous install destination");
    }

    /// <summary>通常bmson行をdrop用譜面へ解決し、パスとtitleを保ってMD5・SHA256両方のplaylist項目を生成します。</summary>
    [TestMethod]
    public void WorkspaceDropChartResolution_LibraryBmsonRowCreatesPlaylistEntryWithBothHashes()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Bmson",
            Title = "Bmson",
            RawArtist = "Artist",
            Level = 12,
            Md5 = "12121212121212121212121212121212",
            Sha256 = new string('1', 64)
        };
        var row = LibraryChartRow.FromChartFile((bmson));

        ChartFile chart = PlaylistWorkspaceViewModel.ResolveDropChart(row);
        var entry = BMSTableEntry.CreateForPlaylistDrop(chart);

        Assert.IsNotNull(chart);
        Assert.AreEqual(ChartFileKind.Bmson, chart.Kind);
        Assert.IsNull(chart.Token);

        Assert.AreEqual(bmson.Path, chart.Path);
        Assert.AreEqual(bmson.RawTitle, chart.Title);
        Assert.AreEqual(bmson.Md5, entry.md5);
        Assert.AreEqual(bmson.Sha256, entry.sha256);
        Assert.AreEqual(0, entry.Org_md5.Count);
    }

    /// <summary>BMS drop項目の生成時に両hashと指定した元譜面MD5群を採り、元譜面情報を推測値で置換しません。</summary>
    [TestMethod]
    public void CreateForPlaylistDrop_BmsChartUsesProvidedOrgMd5s()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "BMS" + ".bms", Title = "BMS", RawTitle = "BMS", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 8 };
        file = file with { Sha256 = new string('e', 64) };
        ChartFile chart = (file);

        var entry = BMSTableEntry.CreateForPlaylistDrop(chart, ["cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"]);

        Assert.AreEqual(file.Md5, entry.md5);
        Assert.AreEqual(file.Sha256, entry.sha256);
        CollectionAssert.AreEqual(new[] { "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" }, entry.Org_md5);
    }

    /// <summary>所有tokenのないbmsonもdrop項目へtitle・両hash・指定元譜面MD5を渡し、所有者なしで情報を欠落させません。</summary>
    [TestMethod]
    public void CreateForPlaylistDrop_OwnerlessBmsonChartPreservesBothHashesAndOrgMd5()
    {
        var chart = new ChartFile(ChartFileKind.Bmson, path: "C:\\Songs\\ownerless.bmson", md5: "abababababababababababababababab", sha256: new string('f', 64), title: "Ownerless bmson", rawTitle: "Ownerless bmson", artist: "Artist", genre: string.Empty, folder: "Folder", tag: string.Empty, levelText: "12", level: 12, mode: null, chartInfo: null);

        var entry = BMSTableEntry.CreateForPlaylistDrop(chart, ["cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"]);

        Assert.AreEqual(chart.Md5, entry.md5);
        Assert.AreEqual(chart.Sha256, entry.sha256);
        Assert.AreEqual("Ownerless bmson", entry.title);
        CollectionAssert.AreEqual(new[] { "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" }, entry.Org_md5);
    }

    /// <summary>実VMの外部同期表memo保存では解決bmsonのMD5を元entryへ補わず、DBもSHA256だけを保って外部由来identityを変えません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task CommitPlaylistRow_ExternalSyncEntryDoesNotBackfillHashesFromResolvedChart()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistViewPipelineTests", Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 9101,
                name = "External",
                is_external_sync = true
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
            }
            var entry = new TestablePlaylistEntry
            {
                parent = table,
                folder = string.Empty,
                memo = "memo"
            };
            entry.SetSha256(new string('9', 64));
            entry.SetTitle("External Bmson");
            ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = "C:\\Songs\\Bmson\\external.bmson",
                RawTitle = "External Bmson",
                Md5 = "99999999999999999999999999999999",
                Sha256 = entry.sha256
            };
            PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, (bmson)).CreateViewRow();
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
            BMSPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, MainWindowViewModelTestFactory.CreateIsolatedSettings());
            table.entries = [entry];
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            typeof(MainWindowViewModel).GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, playlist);

            var context = new MainChartListCellEditContext(
                row,
                nameof(PlaylistDetailRow.memo),
                ChartOperationSourceScope.PlaylistOwned,
                MainViewOperationSection.Playlist);
            Task commitTask = viewModel.PlaylistWorkspace.CompleteDetailEdit(
                new MainChartListCellEditEndedEventArgs(context, "updated memo", commit: true));
            await commitTask;

            using LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            BMSTableEntry stored = verifyDb.Table<BMSTableEntry>().Single(dbRow => dbRow.playlist_id == table.playlist_id);
            Assert.IsNull(entry.md5);
            Assert.IsNull(stored.md5);
            Assert.AreEqual(entry.sha256, stored.sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>memo編集取消は未保存で元memoを保持し、編集中に延期されたscore世代を含むrefresh要求の公開内容と保留状態の解放を確認します。</summary>
    [TestMethod]
    public async Task PlaylistDetailEditing_UsesStartedLifecycleAndReleasesDeferredRefreshOnCancel()
    {
        var entry = new TestablePlaylistEntry
        {
            parent = new BMSTable { is_external_sync = false },
            memo = "before"
        };
        var sourceRow = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        PlaylistDetailRow row = sourceRow.CreateViewRow();
        var state = new PlaylistDetailViewState();
        state.Source.Rows = [sourceRow];
        state.Source.LastBuiltScoreSnapshotVersion = 3;
        state.Source.PendingScoreSnapshotRefreshVersion = 5;
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(),
            new PlaylistDetailBuildState(),
            state,
            _ => { },
            _ => { },
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
            () => throw new AssertFailedException("cancel must not persist"),
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
        PlaylistDetailScoreSnapshotRefreshRequestedEventArgs? refresh = null;
        workspace.PlaylistDetailScoreSnapshotRefreshRequested += (_, request) => refresh = request;
        var context = new MainChartListCellEditContext(
            row,
            nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned,
            MainViewOperationSection.Playlist);

        Assert.IsTrue(workspace.CanBeginDetailEdit(context));
        workspace.BeginDetailEdit(context);
        Assert.IsTrue(state.Source.IsPlaylistCellEditing);

        await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "after", commit: false));

        Assert.IsFalse(state.Source.IsPlaylistCellEditing);
        Assert.AreEqual("before", row.memo);
        Assert.IsNotNull(refresh);
        Assert.AreEqual(5, refresh!.ScoreSnapshotVersion);
        Assert.AreEqual(3, refresh.LastBuiltVersion);
        Assert.IsTrue(refresh.DeferredByEdit);
        Assert.IsTrue(refresh.RefreshRequired);
        Assert.AreEqual(0, state.Source.PendingScoreSnapshotRefreshVersion);
    }

    /// <summary>保存先取得が失敗したmemo編集は元例外をTaskへ返し、live sourceのmemoを保存成功のように同期しません。</summary>
    [TestMethod]
    public async Task PlaylistDetailEditing_DurableFailureDoesNotSynchronizeLiveSource()
    {
        var entry = new TestablePlaylistEntry
        {
            parent = new BMSTable { is_external_sync = false },
            memo = "before"
        };
        var sourceRow = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        PlaylistDetailRow row = sourceRow.CreateViewRow();
        PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => throw new InvalidOperationException("durable detail failure"));
        workspace.DetailViewState.Source.Rows = [sourceRow];
        var context = new MainChartListCellEditContext(
            row,
            nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned,
            MainViewOperationSection.Playlist);

        workspace.BeginDetailEdit(context);
        Task commitTask = workspace.CompleteDetailEdit(
            new MainChartListCellEditEndedEventArgs(context, "after", commit: true));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => commitTask);

        Assert.AreEqual("durable detail failure", exception.Message);
        Assert.AreEqual("before", sourceRow.memo);
    }

    /// <summary>保留packageのbmsonから表示・target・library参照へ種類とパス・MD5を渡し、導入先・警告・healthも表示へ保持します。</summary>
    [TestMethod]
    public void LibraryChartRow_FromPendingBmson_PreservesPendingInstallState()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Bmson\\chart.bmson",
            Folder = "C:\\Pending\\Bmson",
            RawTitle = "Pending Bmson",
            RawArtist = "Artist",
            Level = 9,
            ModeHint = "beat-7k",
            Md5 = "34343434343434343434343434343434",
            Sha256 = new string('3', 64)
        };
        bmson = bmson with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmson.Path, hash = bmson.Md5, encoding = "utf-8" }) };
        bmson = ChartFileProjection.WithMaintenance(bmson, bmson.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 4 });
        bmson = ChartFileProjection.WithMaintenance(bmson, bmson.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 1 });

        var entry = PackageChartEntry.FromChart((bmson));
        entry.SetWarning(ChartWarningKind.AlreadyInstalled, "installed chart warning");
        entry.ApplyInstallDestinationMetadata("C:\\Library\\Destination", string.Empty, string.Empty);
        var row = LibraryChartRow.FromPackageChartEntry(entry);

        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreEqual(bmson.Path, row.Chart.Path);

        Assert.IsFalse(GridRowResolver.TryGetBmsChart(row, out _));
        Assert.IsTrue(row.DisplayWarning.Contains("installed chart warning"));
        Assert.AreEqual("C:\\Library\\Destination", row.instl_dst);
        Assert.AreEqual(bmson.ResourceHealthMaintenanceSnapshot.GetWavHealth(), row.WAVHealth);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, isPendingSection: true, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);


        var chartRef = LibraryChartRef.FromChartFile(row.Chart);
        Assert.AreEqual(ChartFileKind.Bmson, chartRef.Kind);
        Assert.AreEqual(bmson.Path, chartRef.Path);
        Assert.AreEqual(bmson.Md5, chartRef.Md5);
    }

    /// <summary>保留のpackage表示行は導入先の設定前後とも資源不足の要約と詳細を保持し、設定済みだけで不足を隠す誤りを検出します。</summary>
    [TestMethod]
    public void LibraryChartRow_FromPackageChartEntry_PreservesResourceHealthDigestWhenInstallDestinationSetAndKeepsTooltip()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Bmson\\resource-missing.bmson",
            Folder = "C:\\Pending\\Bmson",
            RawTitle = "Resource Missing",
            RawArtist = "Artist",
            Md5 = "45454545454545454545454545454545",
            Sha256 = new string('4', 64)
        };
        var entry = PackageChartEntry.FromChart((bmson));
        entry.SetWarning(ChartWarningKind.ResourceWavMissing, "WAV missing detail");

        var unresolvedRow = LibraryChartRow.FromPackageChartEntry(entry);
        StringAssert.Contains(unresolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);

        entry.ApplyInstallDestinationMetadata("C:\\Library\\Destination", string.Empty, string.Empty);
        var resolvedRow = LibraryChartRow.FromPackageChartEntry(entry);

        StringAssert.Contains(resolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(unresolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(resolvedRow.WarningTooltipText, "WAV missing detail");
    }

    /// <summary>保留のpackage sourceでも導入先の設定前後とも資源不足の要約と譜面警告の詳細を保持し、生成済み入力行の表示固定を検出します。</summary>
    [TestMethod]
    public void ChartListSourceRow_FromPackageChartEntry_PreservesResourceHealthDigestWhenInstallDestinationSetAndKeepsTooltip()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Bmson\\source-resource-missing.bmson",
            Folder = "C:\\Pending\\Bmson",
            RawTitle = "Source Resource Missing",
            RawArtist = "Artist",
            Md5 = "67676767676767676767676767676767",
            Sha256 = new string('6', 64)
        };
        var entry = PackageChartEntry.FromChart((bmson));
        entry.SetWarning(ChartWarningKind.ResourceWavMissing, "source WAV missing detail");

        var unresolvedRow = ChartListSourceRow.FromPackageChartEntry(entry);
        StringAssert.Contains(unresolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);

        entry.ApplyInstallDestinationMetadata("C:\\Library\\Destination", string.Empty, string.Empty);
        var resolvedRow = ChartListSourceRow.FromPackageChartEntry(entry);

        StringAssert.Contains(resolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(unresolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(resolvedRow.Chart.Warnings), "source WAV missing detail");
    }

    /// <summary>保留bmsonの現在packageパスとPending scopeをtargetへ渡し、元libraryパスや所持扱いへ戻しません。</summary>
    [TestMethod]
    public void ChartOperationTarget_PendingBmson_PreservesPendingPathAndScope()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Original\\Bmson\\chart.bmson",
            Folder = "C:\\Original\\Bmson",
            RawTitle = "Pending Bmson",
            RawArtist = "Artist",
            Md5 = "56565656565656565656565656565656",
            Sha256 = new string('5', 64)
        };
        bmson = bmson with { Path = "C:\\Pending\\Package\\chart.bmson" };
        bmson = bmson with { Folder = "C:\\Pending\\Package" };
        var entry = PackageChartEntry.FromChart((bmson));
        var row = LibraryChartRow.FromPackageChartEntry(entry);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, target.SourceScope);
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.AreEqual(bmson.Path, target.Chart.Path);
        Assert.IsFalse(target.IsOwned);
        Assert.IsTrue(target.IsPending);
    }

    /// <summary>保留・導入済み・playlist・全件検査・解析失敗・履歴・library modeを操作sectionとscopeへ配送し、異なる操作経路を混同しません。</summary>
    [TestMethod]
    public void MainViewOperationContext_MapsViewModeToRowOperationScope()
    {
        AssertMainViewOperationContext(
            MainViewUpdateMode.PendingInstallFolderSelected,
            MainViewOperationSection.InstallPending,
            ChartOperationSourceScope.PendingPackage);
        AssertMainViewOperationContext(
            MainViewUpdateMode.NewlyInstalledFolderSelected,
            MainViewOperationSection.InstallInstalled,
            ChartOperationSourceScope.NewlyInstalledPackage);
        AssertMainViewOperationContext(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewOperationSection.Playlist,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.PlaylistNotOwnedFilterSelected,
            MainViewOperationSection.Playlist,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FullScanAllChartsFilterSelected,
            MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FileMissingFilterSelected,
            MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FileMissingIgnoredFilterSelected,
            MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.ChartInfoParseErrorFilterSelected,
            MainViewOperationSection.ChartInfoParseError,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewOperationSection.PlayHistory,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewOperationSection.Library,
            ChartOperationSourceScope.Library);
    }

    /// <summary>MainChartListの保留mode選択をResolverへ渡すと通常BMS行もPending scopeとなり、画面modeから操作scopeへの接続を落としません。</summary>
    [TestMethod]
    public void MainViewOperationContext_PendingModeMakesLibraryChartRowPendingScoped()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Pending Bms" + ".bms", Title = "Pending Bms", RawTitle = "Pending Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var row = LibraryChartRow.FromChartFile(file);
        var mainChartList = new MainChartListViewModel();
        mainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
        ChartOperationSourceScope sourceScope = mainChartList.CurrentOperationContext.SourceScope;

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, target.SourceScope);

    }

    /// <summary>導入済みpackage対象はlibrary削除経路へ同じ対象を渡し、保留packageの取消経路へ誤配送しません。</summary>
    [TestMethod]
    public void DeleteTargetResolver_NewlyInstalledRoutesToLibrary()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.NewlyInstalledPackage, ChartOperationCapabilities.RemoveFromLibrary);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [target],
            target,
            MainViewOperationSection.InstallInstalled);

        Assert.AreEqual(ChartDeleteRoute.Library, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
        Assert.IsFalse(resolution.UsedContextFallback);
    }

    /// <summary>保留対象はpending削除経路へ同じ対象を渡し、未導入譜面をlibraryの物理削除へ配送しません。</summary>
    [TestMethod]
    public void DeleteTargetResolver_PendingRoutesToPending()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.PendingPackage, ChartOperationCapabilities.UpdateInstallDestination);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [target],
            target,
            MainViewOperationSection.InstallPending);

        Assert.AreEqual(ChartDeleteRoute.Pending, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
    }

    /// <summary>選択が空ならcontext対象をlibrary削除へ補い、右クリック対象を選択なしとして失いません。</summary>
    [TestMethod]
    public void DeleteTargetResolver_UsesContextFallbackWhenSelectionIsEmpty()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.Library, ChartOperationCapabilities.RemoveFromLibrary);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [],
            target,
            MainViewOperationSection.Library);

        Assert.AreEqual(ChartDeleteRoute.Library, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
        Assert.IsTrue(resolution.UsedContextFallback);
    }

    /// <summary>未所持playlist対象は物理削除経路も対象列も空にし、hash参照だけの行をファイル削除へ送信しません。</summary>
    [TestMethod]
    public void DeleteTargetResolver_PlaylistMissingIsNotFileDeleteTarget()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.PlaylistMissing, ChartOperationCapabilities.None);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [target],
            target,
            MainViewOperationSection.Playlist);

        Assert.AreEqual(ChartDeleteRoute.None, resolution.Route);
        Assert.AreEqual(0, resolution.Targets.Count);
    }

    /// <summary>libraryとpendingの混在選択ではcontextのpendingだけを配送し、除外件数を数えて別scope対象を巻き込みません。</summary>
    [TestMethod]
    public void DeleteTargetResolver_ContextScopeFiltersMixedSelection()
    {
        ChartOperationTarget libraryTarget = CreateDeleteTarget(ChartOperationSourceScope.Library, ChartOperationCapabilities.RemoveFromLibrary);
        ChartOperationTarget pendingTarget = CreateDeleteTarget(ChartOperationSourceScope.PendingPackage, ChartOperationCapabilities.UpdateInstallDestination);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [libraryTarget, pendingTarget],
            pendingTarget,
            MainViewOperationSection.InstallPending);

        Assert.AreEqual(ChartDeleteRoute.Pending, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(pendingTarget, resolution.Targets[0]);
        Assert.AreEqual(1, resolution.MixedScopeDroppedCount);
    }

    /// <summary>導入済みbmsonは導入後のパスと所持・非保留scopeをtargetと参照へ渡し、導入前packageパスを残しません。</summary>
    [TestMethod]
    public void ChartOperationTarget_NewlyInstalledBmson_PreservesInstalledPathAndScope()
    {
        ChartFile original = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Package\\chart.bmson",
            Folder = "C:\\Pending\\Package",
            RawTitle = "Installed Bmson",
            RawArtist = "Artist",
            Md5 = "67676767676767676767676767676767",
            Sha256 = new string('6', 64)
        };
        ChartFile installed = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Library\\Package\\chart.bmson",
            Folder = "C:\\Library\\Package",
            RawTitle = original.RawTitle,
            RawArtist = original.RawArtist,
            Md5 = original.Md5,
            Sha256 = original.Sha256
        };
        var row = LibraryChartRow.FromChartFile((installed));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.NewlyInstalledPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.NewlyInstalledPackage, target.SourceScope);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPending);
        Assert.AreEqual(installed.Path, target.Chart.Path);

        Assert.AreEqual(installed.Path, LibraryChartRef.FromChartFile(target.Chart).Path);
    }

    /// <summary>
    /// BMS・bmsonの所持・保留・新規導入・プレイリスト行で操作権限を判断し、
    /// BMS専用処理の誤許可、保留や未所持への移動・削除など所持向け操作の誤許可、URLの有無に合わない起動を防ぎます。
    /// </summary>
    /// <remarks>能力の条件網羅はResolverに集約します。正確な譜面・項目・scopeの受渡しと実メニュー接続は別ケースで確認します。</remarks>
    [TestMethod]
    public void ChartOperationTarget_CapabilityMatrix_SeparatesBmsOnlyAndBmsonCommonOperations()
    {
        const ChartOperationCapabilities localCommon = ChartOperationCapabilities.OpenFile
            | ChartOperationCapabilities.OpenFolder | ChartOperationCapabilities.RunResourceHealthCheck;
        const ChartOperationCapabilities ownedCommon = localCommon | ChartOperationCapabilities.ConvertToAudio
            | ChartOperationCapabilities.MoveInLibrary | ChartOperationCapabilities.RemoveFromLibrary
            | ChartOperationCapabilities.RepairInstalledLocation;
        const ChartOperationCapabilities bmsHashes = ChartOperationCapabilities.UseLr2Ir
            | ChartOperationCapabilities.UseScoreViewer | ChartOperationCapabilities.UpdateRanking;
        const ChartOperationCapabilities bmsLocal = ChartOperationCapabilities.RunBmsEncodingCheck
            | ChartOperationCapabilities.RunBmsEncodingFix | ChartOperationCapabilities.RunZeroNoteCheck
            | ChartOperationCapabilities.RenameInvalidExtension;

        // 保留はファイル・フォルダを開く操作と保守・導入先編集を許可し、移動・削除・音声変換・所持先修復は拒否します。未所持はハッシュとURL操作だけです。
        (ChartOperationSourceScope Scope, ChartFileKind Kind, ChartOperationCapabilities Expected)[] cases =
        [
            (ChartOperationSourceScope.Library, ChartFileKind.Bms, ownedCommon | bmsHashes | bmsLocal),
            (ChartOperationSourceScope.Library, ChartFileKind.Bmson, ownedCommon),
            (ChartOperationSourceScope.PendingPackage, ChartFileKind.Bms, localCommon | bmsHashes | bmsLocal | ChartOperationCapabilities.UpdateInstallDestination),
            (ChartOperationSourceScope.PendingPackage, ChartFileKind.Bmson, localCommon | ChartOperationCapabilities.UpdateInstallDestination),
            (ChartOperationSourceScope.NewlyInstalledPackage, ChartFileKind.Bms, ownedCommon | bmsHashes | bmsLocal),
            (ChartOperationSourceScope.NewlyInstalledPackage, ChartFileKind.Bmson, ownedCommon),
            (ChartOperationSourceScope.PlaylistOwned, ChartFileKind.Bms, ownedCommon | bmsHashes | bmsLocal),
            (ChartOperationSourceScope.PlaylistOwned, ChartFileKind.Bmson, ownedCommon),
            (ChartOperationSourceScope.PlaylistMissing, ChartFileKind.Bms, bmsHashes)
        ];
        foreach ((ChartOperationSourceScope Scope, ChartFileKind Kind, ChartOperationCapabilities Expected) scenario in cases)
        {
            ChartFile chart = ChartTestValues.Empty(scenario.Kind) with
            {
                Path = scenario.Kind == ChartFileKind.Bms ? @"C:\Songs\chart.bms" : @"C:\Songs\chart.bmson",
                Md5 = new string('a', 32),
                Sha256 = new string('b', 64)
            };
            bool playlist = scenario.Scope is ChartOperationSourceScope.PlaylistOwned or ChartOperationSourceScope.PlaylistMissing;
            // URL無し・本体URLのみ・差分URLのみの許可をそれぞれ判断します。
            foreach (string urlKind in playlist ? new[] { "none", "main", "diff" } : new[] { "none" })
            {
                object row;
                if (playlist)
                {
                    var entry = new TestablePlaylistEntry(chart);
                    if (urlKind == "main") { entry.url = "https://example.invalid/main.zip"; }
                    if (urlKind == "diff") { entry.url_diff = "https://example.invalid/diff.zip"; }
                    row = new PlaylistDetailSourceRow(entry,
                        scenario.Scope == ChartOperationSourceScope.PlaylistMissing ? null : chart).CreateViewRow();
                }
                else
                {
                    row = scenario.Scope == ChartOperationSourceScope.PendingPackage
                        ? LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart))
                        : LibraryChartRow.FromChartFile(chart);
                }
                Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, scenario.Scope, out ChartOperationTarget target));
                ChartOperationCapabilities expected = scenario.Expected
                    | (urlKind == "none" ? ChartOperationCapabilities.None : ChartOperationCapabilities.OpenPlaylistUrls);
                Assert.AreEqual(expected, target.Capabilities, $"{scenario.Scope}/{scenario.Kind}/{urlKind}");
            }
        }
    }

    /// <summary>folder編集入口は所持bmsonのパスを受ける一方、保留bmsonはtargetなしで拒否し、保留の物理folder変更を許しません。</summary>
    [TestMethod]
    public void FolderEditChartOperationTarget_UsesBmsonChartForOwnedRowsAndRejectsPendingRows()
    {
        ChartFile ownedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Owned Bmson",
            RawArtist = "Artist",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Bmson\\chart.bmson",
            Folder = "C:\\Pending\\Bmson",
            RawTitle = "Pending Bmson",
            RawArtist = "Artist",
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('c', 64)
        };
        var pendingEntry = PackageChartEntry.FromChart((pendingBmson));

        var ownedRow = LibraryChartRow.FromChartFile((ownedBmson));
        var pendingRow = LibraryChartRow.FromPackageChartEntry(pendingEntry);

        Assert.IsFalse(GridRowResolver.TryGetBmsChart(ownedRow, out _));
        Assert.IsTrue(GridRowResolver.TryGetFolderEditChartOperationTarget(ownedRow, ChartOperationSourceScope.Library, out ChartOperationTarget ownedTarget));
        Assert.AreEqual(ChartFileKind.Bmson, ownedTarget.Chart.Kind);
        Assert.AreEqual(ownedBmson.Path, ownedTarget.Chart.Path);
        Assert.AreSame(ownedBmson.Token, ownedTarget.Chart.Token);
        Assert.IsFalse(GridRowResolver.TryGetFolderEditChartOperationTarget(pendingRow, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget pendingTarget));
        Assert.IsNull(pendingTarget);
    }

    /// <summary>raw BMSと通常表示行を各Resolver入口が受理し、BMS playerと操作targetへ実パスを渡して入口別の型判定を誤りません。</summary>
    [TestMethod]
    public void ChartOperationTarget_ChartNamedApis_TreatRawBmsAsBmsPlayerOnlyBoundary()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Bms" + ".bms", Title = "Bms", RawTitle = "Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };

        Assert.IsTrue(GridRowResolver.TryGetChartFile(file, out _));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(file, out _));
        Assert.IsTrue(GridRowResolver.TryGetBmsChart(file, out ChartFile playerFile));
        Assert.AreEqual(file.Path, playerFile.Path);

        var row = LibraryChartRow.FromChartFile(file);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.AreEqual(file.Path, target.Chart.Path);
    }

    private static void AssertMainViewOperationContext(
        MainViewUpdateMode mode,
        MainViewOperationSection expectedSection,
        ChartOperationSourceScope expectedScope)
    {
        var mainChartList = new MainChartListViewModel();
        mainChartList.SetOperationContext(mode);

        Assert.AreEqual(expectedSection, mainChartList.CurrentOperationContext.OperationSection);
        Assert.AreEqual(expectedScope, mainChartList.CurrentOperationContext.SourceScope);
    }

    /// <summary>未所持行はパスなしのMD5参照を保持し、移動要求を拒否してhashだけの譜面を物理移動しません。</summary>
    [TestMethod]
    public void ChartOperationTarget_PlaylistMissing_PreservesHashReferenceAndRejectsMoveRequest()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("Missing");
        entry.SetMd5("abababababababababababababababab");
        entry.lr2_bmsid = "12345";
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.IsFalse(target.IsOwned);
        Assert.IsTrue(target.IsPlaylistMissing);
        var chartRef = target.ToLibraryChartRef();
        Assert.IsNotNull(chartRef);
        Assert.AreEqual(ChartFileKind.Bms, chartRef.Kind);
        Assert.IsNull(chartRef.Path);
        Assert.AreEqual(entry.md5, chartRef.Md5);
        Assert.IsNull(chartRef.Token);
        Assert.AreEqual(target.Chart.Md5, chartRef.Md5);
        Assert.IsFalse(ChartLibraryMoveRequest.TryCreate([target], "C:\\Songs", out _));
    }

    /// <summary>表示前のplaylist sourceも元entryを持つ所持targetとして解決し、view行しか受け付けない誤りを検出します。</summary>
    [TestMethod]
    public void GridRowResolver_TreatsPlaylistSourceRowAsPlaylistEntryRow()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Owned Bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);
        var sourceRow = new PlaylistDetailSourceRow(entry, (bmson));

        Assert.IsTrue(GridRowResolver.IsPlaylistRow(sourceRow));
        Assert.AreSame(entry, GridRowResolver.GetPlaylistEntry(sourceRow));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(sourceRow, out ChartOperationTarget target));
        Assert.AreSame(entry, target.PlaylistEntry);
        Assert.AreEqual(ChartOperationSourceScope.PlaylistOwned, target.SourceScope);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
    }


    /// <summary>root dropは未所持項目だけを元entryとして保持し、所持BMS・bmsonは譜面へ解決して古いplaylist entryを持ち込みません。</summary>
    [TestMethod]
    public void RootFolderDropEntryPolicy_PreservesOnlyMissingPlaylistRows()
    {
        ChartFile bms = ChartTestValues.Empty();
        bms = bms with { Md5 = "abababababababababababababababab", Path = "Owned Bms" + ".bms", Title = "Owned Bms", RawTitle = "Owned Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        PlaylistDetailRow ownedBmsRow = new PlaylistDetailSourceRow(new TestablePlaylistEntry(bms), (bms)).CreateViewRow();

        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Owned Bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        var bmsonEntry = new TestablePlaylistEntry();
        bmsonEntry.SetMd5(bmson.Md5);
        bmsonEntry.SetSha256(bmson.Sha256);
        PlaylistDetailRow ownedBmsonRow = new PlaylistDetailSourceRow(bmsonEntry, (bmson)).CreateViewRow();

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetMd5("cccccccccccccccccccccccccccccccc");
        PlaylistDetailRow missingRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null).CreateViewRow();

        Assert.IsFalse(PlaylistWorkspaceViewModel.ShouldPreserveEntryForRootFolderDrop(ownedBmsRow));
        Assert.IsFalse(PlaylistWorkspaceViewModel.ShouldPreserveEntryForRootFolderDrop(ownedBmsonRow));
        Assert.IsTrue(PlaylistWorkspaceViewModel.ShouldPreserveEntryForRootFolderDrop(missingRow));
        Assert.AreEqual(ChartFileKind.Bmson, PlaylistWorkspaceViewModel.ResolveDropChart(ownedBmsonRow).Kind);
        Assert.IsNull(PlaylistWorkspaceViewModel.ResolveDropChart(missingRow));
    }

    /// <summary>表示前sourceのroot dropも未所持entryだけを保持し、所持bmsonを譜面へ解決してsource型で判断を変えません。</summary>
    [TestMethod]
    public void RootFolderDropEntryPolicy_SourceRowsPreservesOnlyMissingPlaylistRows()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Owned Bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        var bmsonEntry = new TestablePlaylistEntry();
        bmsonEntry.SetMd5(bmson.Md5);
        bmsonEntry.SetSha256(bmson.Sha256);
        var ownedBmsonSourceRow = new PlaylistDetailSourceRow(bmsonEntry, (bmson));

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetMd5("cccccccccccccccccccccccccccccccc");
        var missingSourceRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null);

        Assert.IsFalse(PlaylistWorkspaceViewModel.ShouldPreserveEntryForRootFolderDrop(ownedBmsonSourceRow));
        Assert.IsTrue(PlaylistWorkspaceViewModel.ShouldPreserveEntryForRootFolderDrop(missingSourceRow));
        Assert.AreEqual(ChartFileKind.Bmson, PlaylistWorkspaceViewModel.ResolveDropChart(ownedBmsonSourceRow).Kind);
        Assert.IsNull(PlaylistWorkspaceViewModel.ResolveDropChart(missingSourceRow));
    }

    /// <summary>summary行は単体と配列の両drop候補判定で拒否し、表の行を譜面entryとして取り込みません。</summary>
    [TestMethod]
    public void PlaylistDropCandidatePolicy_RejectsPlaylistSummaryRows()
    {
        var summaryRow = new PlaylistSummaryRow
        {
            Name = "Summary",
            TableRef = new BMSTable()
        };

        Assert.IsFalse(PlaylistWorkspaceViewModel.IsDropCandidateRow(summaryRow));
        Assert.IsFalse(PlaylistWorkspaceViewModel.AreDropCandidateRows([summaryRow]));
    }

    /// <summary>通常譜面と詳細行は混在dropを受理し、summaryを含む混在配列は拒否して無効行だけを黙って落としません。</summary>
    [TestMethod]
    public void PlaylistDropCandidatePolicy_AcceptsChartAndPlaylistDetailRows()
    {
        ChartFile bms = ChartTestValues.Empty();
        bms = bms with { Md5 = "abababababababababababababababab", Path = "Owned Bms" + ".bms", Title = "Owned Bms", RawTitle = "Owned Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var libraryRow = LibraryChartRow.FromChartFile(bms);
        PlaylistDetailRow playlistRow = new PlaylistDetailSourceRow(new TestablePlaylistEntry(bms), (bms)).CreateViewRow();

        Assert.IsTrue(PlaylistWorkspaceViewModel.IsDropCandidateRow(libraryRow));
        Assert.IsTrue(PlaylistWorkspaceViewModel.IsDropCandidateRow(playlistRow));
        Assert.IsTrue(PlaylistWorkspaceViewModel.AreDropCandidateRows([libraryRow, playlistRow]));
        Assert.IsFalse(PlaylistWorkspaceViewModel.AreDropCandidateRows([libraryRow, new PlaylistSummaryRow()]));
    }

    /// <summary>履歴行は解決済み譜面だけdropを許可し、未解決との混在を拒否します。通常編集・削除Resolverには履歴行を渡しません。</summary>
    [TestMethod]
    public void PlayHistoryRowOperationPolicy_AllowsPlaylistDropOnlyWhenResolved()
    {
        PlayHistoryRow resolvedRow = CreateResolvedPlayHistoryRow();
        PlayHistoryRow unresolvedRow = CreateUnresolvedPlayHistoryRow();

        Assert.IsNotNull(resolvedRow.ResolvedChart);
        Assert.IsNull(unresolvedRow.ResolvedChart);
        Assert.IsTrue(PlaylistWorkspaceViewModel.IsDropCandidateRow(resolvedRow));
        Assert.IsFalse(PlaylistWorkspaceViewModel.IsDropCandidateRow(unresolvedRow));
        Assert.IsTrue(PlaylistWorkspaceViewModel.AreDropCandidateRows([resolvedRow]));
        Assert.IsFalse(PlaylistWorkspaceViewModel.AreDropCandidateRows([resolvedRow, unresolvedRow]));
        Assert.AreSame(resolvedRow.ResolvedChart, PlaylistWorkspaceViewModel.ResolveDropChart(resolvedRow));
        Assert.IsNull(PlaylistWorkspaceViewModel.ResolveDropChart(unresolvedRow));
        Assert.IsFalse(GridRowResolver.IsPlaylistRow(resolvedRow));
        Assert.IsNull(GridRowResolver.GetPlaylistEntry(resolvedRow));
        Assert.IsFalse(GridRowResolver.TryGetBmsChart(resolvedRow, out _));
        Assert.IsFalse(GridRowResolver.TryGetChartFile(resolvedRow, out _));
        Assert.IsFalse(GridRowResolver.TryGetChartOperationTarget(resolvedRow, out _));
        Assert.IsFalse(GridRowResolver.TryGetFolderEditChartOperationTarget(resolvedRow, ChartOperationSourceScope.Library, out _));
    }

    /// <summary>表示中の末尾行を途中へdropすると隠れた表の相対順を保ち、フィルタ外の表を並替から消しません。</summary>
    [TestMethod]
    public void PlaylistSummaryBmtSortDrop_FilteredRowsPreservesHiddenRows()
    {
        List<BMSTable> fullOrder = CreateBmtSortTables(1, 2, 3, 4, 5);
        PlaylistSummaryRow[] visibleRows =
        [
            CreatePlaylistSummaryRow(fullOrder[0]),
            CreatePlaylistSummaryRow(fullOrder[3]),
            CreatePlaylistSummaryRow(fullOrder[4])
        ];
        PlaylistSummaryRow[] draggedRows = [visibleRows[2]];

        List<BMSTable> result = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByVisibleDrop(fullOrder, visibleRows, draggedRows, visibleInsertIndex: 1);

        CollectionAssert.AreEqual(new[] { 1, 5, 2, 3, 4 }, result.Select(table => table.playlist_id.GetValueOrDefault()).ToArray());
    }

    /// <summary>表示中の順をBMT順へ適用する際は表示行のslotだけを置換し、非表示表の位置を動かしません。</summary>
    [TestMethod]
    public void PlaylistSummaryBmtSortApplyCurrentOrder_ReplacesOnlyVisibleSlots()
    {
        List<BMSTable> fullOrder = CreateBmtSortTables(1, 2, 3, 4, 5);
        PlaylistSummaryRow[] visibleRows =
        [
            CreatePlaylistSummaryRow(fullOrder[4]),
            CreatePlaylistSummaryRow(fullOrder[0]),
            CreatePlaylistSummaryRow(fullOrder[3])
        ];

        List<BMSTable> result = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByReplacingVisibleSlots(fullOrder, visibleRows);

        CollectionAssert.AreEqual(new[] { 5, 2, 3, 1, 4 }, result.Select(table => table.playlist_id.GetValueOrDefault()).ToArray());
    }

    /// <summary>二表の末尾移動は選択順を保って非選択表の後ろへ並べ、対象を逆順や重複にしません。</summary>
    [TestMethod]
    public void PlaylistSummaryBmtSortMoveToBottom_KeepsSelectionOrder()
    {
        List<BMSTable> fullOrder = CreateBmtSortTables(1, 2, 3, 4, 5);
        PlaylistSummaryRow[] selectedRows =
        [
            CreatePlaylistSummaryRow(fullOrder[1]),
            CreatePlaylistSummaryRow(fullOrder[3])
        ];

        List<BMSTable> result = PlaylistSummaryBmtSortOrderPlanner.BuildOrderByMovingRows(fullOrder, selectedRows, insertAtTop: false);

        CollectionAssert.AreEqual(new[] { 1, 3, 5, 2, 4 }, result.Select(table => table.playlist_id.GetValueOrDefault()).ToArray());
    }

    /// <summary>空の追加要求は表の更新時刻・entries revision・既存項目を保持し、何も追加しない要求で表を変更しません。</summary>
    [TestMethod]
    public void AddBMSTableEntriesToFolder_EmptyInputDoesNotTouchPlaylist()
    {
        DateTime lastUpdate = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var table = new BMSTable
        {
            last_update = lastUpdate,
            entries = [new BMSTableEntry(JObject.Parse("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Existing\",\"level\":\"A\"}"))]
        };
        int entriesRevision = table.PlaylistEntriesRevision;
        List<BMSTableEntry> entriesBefore = [.. table.entries];

        table.AddBMSTableEntriesToFolder([], "New Folder");

        Assert.AreEqual(lastUpdate, table.last_update);
        Assert.AreEqual(entriesRevision, table.PlaylistEntriesRevision);
        CollectionAssert.AreEqual(entriesBefore, table.entries.ToList());
    }

    /// <summary>BMS参照がなくなったsource再生成は未所持・NO_SONGとなり、元所持行の削除権限を残しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_AfterBmsRemoval_RematerializesEntryAsMissingNoSong()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Owned Bms" + ".bms", Title = "Owned Bms", RawTitle = "Owned Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var entry = new TestablePlaylistEntry(file);

        var ownedSource = new PlaylistDetailSourceRow(entry, (file));
        var missingSource = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        PlaylistDetailRow missingRow = missingSource.CreateViewRow();

        Assert.IsTrue(ownedSource.IsOwned);
        Assert.IsFalse(missingSource.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, missingSource.clear);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(missingRow, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    /// <summary>bmson参照がなくなったsource再生成も未所持・NO_SONGとなり、両hashがあることだけで所持を維持しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_AfterBmsonRemoval_RematerializesEntryAsMissingNoSong()
    {
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Folder = "C:\\Songs\\Bmson",
            RawTitle = "Owned Bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('d', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.Md5);
        entry.SetSha256(bmson.Sha256);

        var ownedSource = new PlaylistDetailSourceRow(entry, (bmson));
        var missingSource = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        PlaylistDetailRow missingRow = missingSource.CreateViewRow();

        Assert.IsTrue(ownedSource.IsOwned);
        Assert.IsFalse(missingSource.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, missingSource.clear);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(missingRow, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    /// <summary>解決譜面がない行はentryのtitle・artist・folder・level・両hashを表示し、metadata不足で未所持項目を空にしません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithoutResolvedFiles_PreservesPlaylistEntryFallbackValues()
    {
        var entry = new TestablePlaylistEntry
        {
            folder = "EntryFolder",
            level = 12
        };
        entry.SetTitle("EntryTitle");
        entry.SetArtist("EntryArtist");
        entry.SetMd5("abababababababababababababababab");
        entry.SetSha256(new string('c', 64));

        var sourceRow = new PlaylistDetailSourceRow(entry, resolvedChart: null);

        Assert.IsFalse(sourceRow.IsOwned);
        Assert.AreEqual("EntryTitle", sourceRow.Title);
        Assert.AreEqual("EntryArtist", sourceRow.Artist);
        Assert.AreEqual("EntryFolder", sourceRow.Folder);
        Assert.AreEqual("12", sourceRow.Level);
        Assert.AreEqual(entry.md5, sourceRow.hash);
        Assert.AreEqual(entry.sha256, sourceRow.sha256);
        Assert.AreEqual(ClearType.NO_SONG, sourceRow.clear);
    }

    /// <summary>未所持行にchartInfoがあればlevel・難度・notes・TOTALと外部hashを表示し、metadataがあるだけで物理操作を許可しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithEntryChartInfo_DisplaysMetadataWithoutChangingOwnership()
    {
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(
            sha256: new string('d', 64),
            md5: "abababababababababababababababab",
            level: 12,
            notes: 2500,
            total: 777.5);
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("Missing");
        entry.SetMd5(chartInfo.md5);
        entry.SetSha256(chartInfo.sha256);

        var sourceRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: chartInfo);
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.IsFalse(sourceRow.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, sourceRow.clear);
        Assert.AreSame(chartInfo, sourceRow.EntryChartInfo);
        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreSame(chartInfo, sourceRow.Chart.ChartInfo);
        Assert.AreSame(sourceRow.Chart, row.Chart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(row, out ChartFile rowChart));
        Assert.AreSame(row.Chart, rowChart);
        Assert.AreSame(chartInfo, rowChart.ChartInfo);
        Assert.AreEqual(chartInfo.sha256, sourceRow.sha256);
        Assert.AreEqual(chartInfo.sha256, GridRowResolver.GetExternalActionSha256(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual("ANOTHER", row.ChartDifficultyText);
        Assert.AreEqual(2500, row.ChartNotes);
        Assert.AreEqual("777.5", row.ChartTotalText);
    }

    /// <summary>未所持entryにSHA256がない場合はchartInfoのhashを表示と外部操作へ渡し、MD5のみのentryで外部hashを落としません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithChartInfoFallback_UsesChartInfoSha256()
    {
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(new string('a', 64), "abababababababababababababababab");
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("MissingShaFallback");
        entry.SetMd5(chartInfo.md5);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: chartInfo).CreateViewRow();

        Assert.AreEqual(chartInfo.sha256, row.sha256);
        Assert.AreEqual(chartInfo.sha256, GridRowResolver.GetExternalActionSha256(row));
    }

    /// <summary>未所持metadataのpatchは旧sourceのlevelを保ち、新source/viewだけへ新level・notes・TOTAL・hashを渡して旧表示snapshotを変更しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_MissingEntryChartInfoPatchCreatesCopyForViewRematerialize()
    {
        BeMusicSeeker.Models.ChartDetails oldInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 3, notes: 500, total: 100);
        BeMusicSeeker.Models.ChartDetails newInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12, notes: 2500, total: 500);
        PlaylistDetailSourceRow sourceRow = CreateMissingSourceRow("PatchChartInfo", oldInfo);

        Assert.IsTrue(sourceRow.HasEntryChartInfoDependency);
        Assert.AreEqual("3", sourceRow.ChartLevelText);
        Assert.AreEqual(500, sourceRow.ChartNotes);

        PlaylistDetailSourceRow patchedSourceRow = sourceRow.WithEntryChartInfo(newInfo);

        Assert.AreSame(oldInfo, sourceRow.EntryChartInfo);
        Assert.AreEqual("3", sourceRow.ChartLevelText);
        PlaylistDetailRow viewRow = patchedSourceRow.CreateViewRow();
        Assert.AreSame(newInfo, patchedSourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.ChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.Chart.ChartInfo);
        Assert.AreSame(patchedSourceRow.Chart, viewRow.Chart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(viewRow, out ChartFile viewChart));
        Assert.AreSame(viewRow.Chart, viewChart);
        Assert.AreSame(newInfo, viewChart.ChartInfo);
        Assert.AreEqual(newInfo.sha256, viewRow.sha256);
        Assert.AreEqual(newInfo.sha256, GridRowResolver.GetExternalActionSha256(viewRow));
        Assert.AreEqual("12", viewRow.ChartLevelText);
        Assert.AreEqual(2500, viewRow.ChartNotes);
        Assert.AreEqual("500", viewRow.ChartTotalText);
    }

    /// <summary>chartInfoを明示patchする際は投影providerを再呼出しせず、指定した新metadataを古いprovider値で上書きしません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_ChartInfoCopyDoesNotInvokeProjectionProvider()
    {
        BeMusicSeeker.Models.ChartDetails oldInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 3);
        BeMusicSeeker.Models.ChartDetails newInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12);
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("ProviderFreePatch");
        entry.SetMd5(oldInfo.md5);
        entry.SetSha256(oldInfo.sha256);
        var resolvedChart = new ChartFile(ChartFileKind.Bms, string.Empty, oldInfo.md5, oldInfo.sha256, "ProviderFreePatch", "ProviderFreePatch", string.Empty, string.Empty, string.Empty, string.Empty, "3", 3, 7, oldInfo, null);
        int projectionCount = 0;
        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            entryChartInfo: oldInfo,
            chartInfoProjectionProvider: _ =>
            {
                projectionCount++;
                return oldInfo;
            });
        int constructionProjectionCount = projectionCount;

        PlaylistDetailSourceRow patchedSourceRow = sourceRow.WithEntryChartInfo(newInfo);

        Assert.AreEqual(constructionProjectionCount, projectionCount);
        Assert.AreSame(oldInfo, sourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.Chart.ChartInfo);
    }

    /// <summary>所持BMSのproviderから新chartInfoを受けるとsource/viewのlevel・notesも更新し、譜面構築時の欠損metadataを固定しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_OwnedBmsChartInfoFollowsProjectionProviderAfterHydration()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "Owned Bms" + ".bms", Title = "Owned Bms", RawTitle = "Owned Bms", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(new string('b', 64), file.Md5, level: 13, notes: 3333, total: 700);
        var sourceRow = new PlaylistDetailSourceRow(
            new TestablePlaylistEntry(file),
            (file),
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));

        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreEqual(13, sourceRow.ChartLevelSortKey);
        Assert.AreEqual(3333, sourceRow.ChartNotes);
        PlaylistDetailRow viewRow = sourceRow.CreateViewRow();
        Assert.AreSame(chartInfo, viewRow.Chart.ChartInfo);
        Assert.AreEqual("13", viewRow.ChartLevelText);
        Assert.AreEqual(3333, viewRow.ChartNotes);
    }

    /// <summary>所持bmsonもprovider由来のchartInfoをsource/viewのlevel・notesへ採り、BMSだけのmetadata配送にしません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_OwnedBmsonChartInfoFollowsProjectionProviderAfterHydration()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Songs\OwnedBmson\chart.bmson",
            RawTitle = "Owned Bmson",
            RawArtist = "Bmson Artist",
            ModeHint = "beat-7k",
            Level = 7,
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('c', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetTitle(song.RawTitle);
        entry.SetMd5(song.Md5);
        entry.SetSha256(song.Sha256);
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(new string('d', 64), song.Md5, level: 14, notes: 4444, total: 800);
        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            (song),
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));

        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreEqual(14, sourceRow.ChartLevelSortKey);
        Assert.AreEqual(4444, sourceRow.ChartNotes);
        PlaylistDetailRow viewRow = sourceRow.CreateViewRow();
        Assert.AreSame(chartInfo, viewRow.Chart.ChartInfo);
        Assert.AreEqual("14", viewRow.ChartLevelText);
        Assert.AreEqual(4444, viewRow.ChartNotes);
    }

    /// <summary>空transient providerを併用しても解決bmsonの表示・導入先・警告とchartInfo providerのmetadataを保ち、空状態で解決値を消しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_OwnedBmsonProjectionPreservesMetadataWithTransientProvider()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Songs\OwnedBmsonProjection\chart.bmson",
            RawTitle = "Storage Title",
            RawArtist = "Storage Artist",
            ModeHint = "beat-7k",
            Level = 7,
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('e', 64)
        };
        ChartFile projectedChart = new(
            ChartFileKind.Bmson,
            song.Path,
            song.Md5,
            song.Sha256,
            "Projected Title",
            "Projected Raw Title",
            "Projected Artist",
            "Projected Genre",
            "Projected Folder",
            "Projected Tag",
            "12",
            12d,
            14,
            chartInfo: null,
            installDestination: @"C:\Installed\Projected",
            warnings: [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "projected warning")]);
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(song.Md5);
        entry.SetSha256(song.Sha256);
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(new string('f', 64), song.Md5, level: 15, notes: 5555, total: 900);

        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            projectedChart,
            chartTransientStateProvider: (_, _) => ChartFileTransientState.Empty,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("Projected Title", row.Title);
        Assert.AreEqual("Projected Artist", row.Artist);
        Assert.AreEqual("Projected Genre", row.genre);
        Assert.AreEqual("Projected Tag", row.tag);
        Assert.AreEqual(14, row.mode);
        Assert.AreEqual("12", row.Level);
        Assert.AreEqual(@"C:\Installed\Projected", row.instl_dst);
        Assert.AreEqual("projected warning", row.DisplayWarning);
        Assert.AreSame(chartInfo, row.Chart.ChartInfo);
        Assert.AreEqual("15", row.ChartLevelText);
        Assert.AreEqual(5555, row.ChartNotes);
    }

    /// <summary>未所持表示行のlevel編集は譜面snapshot・entry・数値sort keyへ反映し、画面文字列だけを変更して後続操作を古いlevelにしません。</summary>
    [TestMethod]
    public void PlaylistDetailRow_MissingLevelEditRefreshesChartSnapshot()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("MissingEditableLevel");
        entry.SetMd5("abababababababababababababababab");
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();

        row.Level = "13";

        Assert.IsTrue(GridRowResolver.TryGetChartFile(row, out ChartFile chart));
        Assert.AreSame(row.Chart, chart);
        Assert.AreEqual("13", chart.LevelText);
        Assert.AreEqual(13d, chart.Level);
        Assert.AreEqual(13d, row.Entry.level);
        Assert.AreEqual(13d, row.EntryLevelSortKey);
    }

    /// <summary>保証：欠損情報の行を数値順から除去せず、同値notesの集合と数値検索の最終2件を保つ。</summary>
    [TestMethod]
    public void PlaylistDetailPresentationService_MissingChartInfoParticipatesInKeywordAndNumericSort()
    {
        BeMusicSeeker.Models.ChartDetails highNotesInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12, notes: 2500, total: 500);
        BeMusicSeeker.Models.ChartDetails lowNotesInfo = CreateChartInfo(new string('f', 64), "ffffffffffffffffffffffffffffffff", level: 3, notes: 500, total: 100);
        PlaylistDetailSourceRow highNotesRow = CreateMissingSourceRow("HighNotes", highNotesInfo);
        PlaylistDetailSourceRow lowNotesRow = CreateMissingSourceRow("LowNotes", lowNotesInfo);

        PlaylistDetailSourceRow tiedNotesRow = CreateMissingSourceRow("TiedNotes", CreateChartInfo(new string('a', 64), new string('a', 32), notes: 2500));
        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetTitle("Missing");
        missingEntry.SetMd5(new string('b', 32));
        var missingRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null);
        PlaylistDetailSourceRow[] source = [highNotesRow, missingRow, lowNotesRow, tiedNotesRow];
        foreach (ListSortDirection direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
        {
            List<PlaylistDetailRow> sorted = PlaylistDetailPresentationService.ApplyViewFromSource(source,
                keywordFilter: string.Empty, modeFilter: ChartModeFilter.All,
                sortParameters: new ChartListSortParameters { ColumnsName = nameof(PlaylistDetailRow.ChartNotes), Direction = direction },
                out _, out _, out _, out _, out _, out _, out _);
            CollectionAssert.AreEquivalent(new[] { "HighNotes", "LowNotes", "TiedNotes", "Missing" }, sorted.Select(row => row.Title).ToArray());
            PlaylistDetailRow[] defined = [.. sorted.Where(row => row.ChartNotes.HasValue)];
            CollectionAssert.AreEqual(direction == ListSortDirection.Ascending ? new[] { 500, 2500, 2500 } : new[] { 2500, 2500, 500 },
                defined.Select(row => row.ChartNotes is int notes ? notes
                    : throw new AssertFailedException("定義値の部分列にはnotesが必要です。")).ToArray());
            CollectionAssert.AreEquivalent(new[] { "HighNotes", "TiedNotes" }, defined.Where(row => row.ChartNotes == 2500).Select(row => row.Title).ToArray());
            Assert.IsNull(sorted.Single(row => row.Title == "Missing").ChartNotes);
        }

        List<PlaylistDetailRow> numericFiltered = PlaylistDetailPresentationService.ApplyViewFromSource(source,
            keywordFilter: "notes:>=2000", modeFilter: ChartModeFilter.All,
            sortParameters: new ChartListSortParameters { ColumnsName = nameof(PlaylistDetailRow.ChartNotes), Direction = ListSortDirection.Ascending },
            out _, out _, out _, out _, out _, out _, out _);
        CollectionAssert.AreEquivalent(new[] { "HighNotes", "TiedNotes" }, numericFiltered.Select(row => row.Title).ToArray());

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            source,
            keywordFilter: "notes:>=2000 feature:random level:12",
            modeFilter: ChartModeFilter.All,
            sortParameters: new ChartListSortParameters { ColumnsName = nameof(PlaylistDetailRow.ChartNotes), Direction = ListSortDirection.Descending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEquivalent(new[] { "HighNotes", "TiedNotes" }, result.Select(row => row.Title).ToArray());
        Assert.IsTrue(result.All(row => row.ChartNotes == 2500 && !row.IsOwned));
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
    }

    /// <summary>MD5一致候補はパス順の代表bmsonを採り、MD5ありでSHA256だけ一致する別譜面へfallbackしません。</summary>
    [TestMethod]
    public void ResolveChartForPlaylistEntry_PrefersMd5BeforeSha256AndRepresentativePathOrder()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        entry.SetSha256(new string('f', 64));
        ChartFile bmsMd5LaterPath = ChartTestValues.Empty();
        bmsMd5LaterPath = bmsMd5LaterPath with { Path = "C:\\Songs\\Omega\\chart.bms", Title = "BMS", RawTitle = "BMS", Artist = "Artist", RawArtist = "Artist", Md5 = entry.md5, Folder = System.IO.Path.GetDirectoryName("C:\\Songs\\Omega\\chart.bms") };
        bmsMd5LaterPath = bmsMd5LaterPath with { Sha256 = entry.sha256 };

        ChartFile laterPath = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Zeta\\chart.bmson",
            Md5 = entry.md5,
            Sha256 = new string('1', 64)
        };
        ChartFile earlierPath = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Alpha\\chart.bmson",
            Md5 = entry.md5,
            Sha256 = new string('2', 64)
        };
        ChartFile shaOnly = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Sha\\chart.bmson",
            Md5 = "99999999999999999999999999999999",
            Sha256 = entry.sha256
        };

        var md5Index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((laterPath)),
            LibraryChartRef.FromChartFile((earlierPath)),
            LibraryChartRef.FromChartFile((bmsMd5LaterPath))
        ]);
        LibraryChartRef preferred = md5Index.ResolveChartForPlaylistHash(entry.md5, null);
        LibraryChartRef resolvedMd5First = md5Index.ResolveChartForPlaylistEntry(entry);
        var shaIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((shaOnly))
        ]);
        LibraryChartRef resolvedBmson = shaIndex.ResolveChartForPlaylistEntry(entry);

        Assert.AreEqual(earlierPath.Path, preferred.Path);
        Assert.AreEqual(earlierPath.Md5, preferred.Md5);
        Assert.AreEqual(ChartFileKind.Bmson, preferred.Kind);
        Assert.AreEqual(earlierPath.Path, resolvedMd5First.Path);
        Assert.AreEqual(earlierPath.Md5, resolvedMd5First.Md5);
        Assert.AreEqual(ChartFileKind.Bmson, resolvedMd5First.Kind);
        Assert.AreEqual(earlierPath.Sha256, preferred.Sha256);
        Assert.IsNull(preferred.ToChartFileIdentity().Token);
        Assert.IsNull(resolvedBmson);
    }

    /// <summary>entryにMD5がない時だけSHA256一致候補からパス順の代表を採り、BMS優先の型順で代表を選びません。</summary>
    [TestMethod]
    public void ResolveChartForPlaylistEntry_UsesSha256RepresentativeByPathOnlyWhenMd5IsMissing()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetSha256(new string('f', 64));
        ChartFile laterBms = ChartTestValues.Empty();
        laterBms = laterBms with { Path = "C:\\Songs\\Omega\\chart.bms", Title = "BMS", RawTitle = "BMS", Artist = "Artist", RawArtist = "Artist", Md5 = new string('1', 32), Folder = System.IO.Path.GetDirectoryName("C:\\Songs\\Omega\\chart.bms") };
        laterBms = laterBms with { Sha256 = entry.sha256 };
        ChartFile earlierBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Alpha\\chart.bmson",
            Md5 = new string('2', 32),
            Sha256 = entry.sha256
        };

        var index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((laterBms)),
            LibraryChartRef.FromChartFile((earlierBmson))
        ]);
        LibraryChartRef preferred = index.ResolveChartForPlaylistHash(null, entry.sha256);
        LibraryChartRef resolved = index.ResolveChartForPlaylistEntry(entry);

        Assert.AreEqual(earlierBmson.Path, preferred.Path);
        Assert.AreEqual(earlierBmson.Md5, preferred.Md5);
        Assert.AreEqual(ChartFileKind.Bmson, preferred.Kind);
        Assert.AreEqual(earlierBmson.Path, resolved.Path);
        Assert.AreEqual(earlierBmson.Md5, resolved.Md5);
        Assert.AreEqual(ChartFileKind.Bmson, resolved.Kind);
        Assert.AreEqual(earlierBmson.Sha256, preferred.Sha256);
        Assert.IsNull(preferred.ToChartFileIdentity().Token);
    }

    /// <summary>entryにMD5があるが一致候補なしの場合、SHA256一致譜面へ切り替えず未解決を保って違う譜面を所持扱いしません。</summary>
    [TestMethod]
    public void ResolveChartForPlaylistEntry_DoesNotFallbackToSha256WhenMd5Exists()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        entry.SetSha256(new string('f', 64));
        ChartFile shaMatch = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Sha\\chart.bmson",
            Md5 = new string('1', 32),
            Sha256 = entry.sha256
        };

        var index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((shaMatch))
        ]);
        LibraryChartRef resolved = index.ResolveChartForPlaylistEntry(entry);

        Assert.IsNull(resolved);
    }

    /// <summary>パスのないBMS・bmson参照はhash一致でも代表から除外し、開けない譜面を所持候補へ採りません。</summary>
    [TestMethod]
    public void ResolveChartForPlaylistEntry_ExcludesPathlessBmsAndBmsonRepresentatives()
    {
        var md5Entry = new TestablePlaylistEntry();
        md5Entry.SetMd5("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var shaEntry = new TestablePlaylistEntry();
        shaEntry.SetMd5("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        shaEntry.SetSha256(new string('c', 64));
        ChartFile pathlessBms = ChartTestValues.Empty();
        pathlessBms = pathlessBms with { Path = "C:\\Songs\\Temp\\chart.bms", Title = "BMS", RawTitle = "BMS", Artist = "Artist", RawArtist = "Artist", Md5 = md5Entry.md5, Folder = System.IO.Path.GetDirectoryName("C:\\Songs\\Temp\\chart.bms") };
        pathlessBms = pathlessBms with { Path = null };
        ChartFile pathlessBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = null,
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = shaEntry.sha256
        };

        var index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((pathlessBms)),
            LibraryChartRef.FromChartFile((pathlessBmson))
        ]);

        Assert.IsNull(index.ResolveChartForPlaylistHash(md5Entry.md5, null));
        Assert.IsNull(index.ResolveChartForPlaylistHash(null, shaEntry.sha256));
        Assert.IsNull(index.ResolveChartForPlaylistEntry(md5Entry));
        Assert.IsNull(index.ResolveChartForPlaylistEntry(shaEntry));
    }

    /// <summary>MD5空・nullの参照はSHA256が一致しても所持代表から除外し、library identityの欠けた譜面を解決しません。</summary>
    [TestMethod]
    public void ResolveChartForPlaylistEntry_ExcludesMd5lessBmsAndBmsonRepresentatives()
    {
        var shaEntry = new TestablePlaylistEntry();
        shaEntry.SetSha256(new string('c', 64));
        ChartFile md5lessBms = ChartTestValues.Empty();
        md5lessBms = md5lessBms with { Path = "C:\\Songs\\Bms\\chart.bms", Title = "BMS", RawTitle = "BMS", Artist = "Artist", RawArtist = "Artist", Md5 = string.Empty, Folder = System.IO.Path.GetDirectoryName("C:\\Songs\\Bms\\chart.bms") };
        md5lessBms = md5lessBms with { Sha256 = shaEntry.sha256 };
        ChartFile md5lessBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\chart.bmson",
            Md5 = null,
            Sha256 = shaEntry.sha256
        };

        var index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((md5lessBms)),
            LibraryChartRef.FromChartFile((md5lessBmson))
        ]);

        Assert.IsNull(index.ResolveChartForPlaylistHash(null, shaEntry.sha256));
        Assert.IsNull(index.ResolveChartForPlaylistEntry(shaEntry));
    }

    /// <summary>同MD5の代表パス選択後はそのSHA256のbeatoraja scoreを採り、別パスのEASYを誤適用せずHARDを表示します。</summary>
    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesResolvedRepresentativeSha256AfterPathTieBreak()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        ChartFile laterBms = ChartTestValues.Empty();
        laterBms = laterBms with { Path = "C:\\Songs\\Omega\\chart.bms", Title = "BMS", RawTitle = "BMS", Artist = "Artist", RawArtist = "Artist", Md5 = entry.md5, Folder = System.IO.Path.GetDirectoryName("C:\\Songs\\Omega\\chart.bms") };
        laterBms = laterBms with { Sha256 = new string('1', 64) };
        ChartFile earlierBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Alpha\\chart.bmson",
            Md5 = entry.md5,
            Sha256 = new string('2', 64)
        };
        var index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromChartFile((laterBms)),
            LibraryChartRef.FromChartFile((earlierBmson))
        ]);
        LibraryChartRef representativeRef = index.ResolveChartForPlaylistEntry(entry);
        ChartFile representative = representativeRef.ToChartFileIdentity();
        var bmsonScore = new BMSScore
        {
            hash = earlierBmson.Sha256,
            clear = ClearType.HARD,
            perfect = 900,
            great = 50,
            totalnotes = 1000
        };
        var bmsScore = new BMSScore
        {
            hash = laterBms.Sha256,
            clear = ClearType.EASY,
            perfect = 100,
            great = 50,
            totalnotes = 1000
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [earlierBmson.Sha256] = bmsonScore,
                [laterBms.Sha256] = bmsScore
            }
        };

        BMSScore resolved = PlaylistEntryScoreSnapshotResolver.Resolve(
            entry,
            representative,
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNull(representative.Token);
        Assert.AreEqual(ChartFileKind.Bmson, representative.Kind);
        Assert.AreEqual(earlierBmson.Path, representative.Path);
        Assert.AreEqual(earlierBmson.Md5, representative.Md5);
        Assert.AreEqual(earlierBmson.Sha256, representative.Sha256);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(earlierBmson.Md5, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
    }

    /// <summary>所持行は注入score snapshotのlamp・rank・EXscore・notes・BPを表示し、library全体のscore反映待ちで欠損表示しません。</summary>
    [TestMethod]
    public void PlaylistDetailSourceRow_UsesScoreSnapshotForOwnedRowsBeforeGlobalHydration()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "cccccccccccccccccccccccccccccccc", Path = "Owned" + ".bms", Title = "Owned", RawTitle = "Owned", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var entry = new BMSTableEntry(file);
        var score = new BMSScore
        {
            hash = file.Md5,
            clear = ClearType.HARD,
            totalnotes = 1000,
            perfect = 800,
            great = 100,
            rank = RankType.AA,
            minbp = 3
        };
        var sourceRow = new PlaylistDetailSourceRow(entry, (file), scoreSnapshot: score);

        Assert.AreEqual(ClearType.HARD, sourceRow.clear);
        Assert.AreEqual(RankType.AA, sourceRow.rank);
        Assert.AreEqual(1700, sourceRow.score);
        Assert.AreEqual(1000, sourceRow.totalnotes);
        Assert.AreEqual(3, sourceRow.minbp);
    }

    /// <summary>MD5だけのentryでも解決BMSのSHA256からbeatoraja scoreを読み、entryのMD5表記へ変換してlampとEXscoreを返します。</summary>
    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesResolvedChartSha256ForMd5OnlyEntry()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "cccccccccccccccccccccccccccccccc", Path = "Owned" + ".bms", Title = "Owned", RawTitle = "Owned", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Sha256 = new string('a', 64) };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(file.Md5);
        var score = new BMSScore
        {
            hash = file.Sha256,
            clear = ClearType.HARD,
            perfect = 800,
            great = 100,
            totalnotes = 1000
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [file.Sha256] = score
            }
        };

        BMSScore resolved = PlaylistEntryScoreSnapshotResolver.Resolve(
            entry,
            (file),
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(file.Md5, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
        Assert.AreEqual(1700, resolved.score);
    }

    /// <summary>LR2 scoreは解決譜面のMD5を古いentry MD5より優先し、旧entryのEASYではなく実譜面のHARDを採ります。</summary>
    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_Lr2PrefersResolvedChartHashWhenEntryHashIsStale()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "cccccccccccccccccccccccccccccccc", Path = "Owned" + ".bms", Title = "Owned", RawTitle = "Owned", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        var fileScore = new BMSScore
        {
            hash = file.Md5,
            clear = ClearType.HARD,
            perfect = 800,
            great = 100,
            totalnotes = 1000
        };
        var staleEntryScore = new BMSScore
        {
            hash = entry.md5,
            clear = ClearType.EASY,
            perfect = 100,
            great = 50,
            totalnotes = 1000
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Lr2,
            ScoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [file.Md5] = fileScore,
                [entry.md5] = staleEntryScore
            }
        };

        BMSScore resolved = PlaylistEntryScoreSnapshotResolver.Resolve(
            entry,
            (file),
            entryChartInfo: null,
            snapshot,
            scoresByHash: snapshot.ScoresByHash,
            scoresBySha256: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase));

        Assert.AreSame(fileScore, resolved);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
    }

    /// <summary>未所持でもentry chartInfoのSHA256からbeatoraja scoreを読み、entryのMD5表記でEX_HARDとEXscoreを返します。</summary>
    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesEntryChartInfoSha256ForMissingEntry()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(new string('b', 64), entry.md5);
        var score = new BMSScore
        {
            hash = chartInfo.sha256,
            clear = ClearType.EX_HARD,
            perfect = 600,
            great = 50,
            totalnotes = 800
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [chartInfo.sha256] = score
            }
        };

        BMSScore resolved = PlaylistEntryScoreSnapshotResolver.Resolve(
            entry,
            resolvedChart: null,
            entryChartInfo: chartInfo,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(entry.md5, resolved.hash);
        Assert.AreEqual(ClearType.EX_HARD, resolved.clear);
        Assert.AreEqual(1250, resolved.score);
    }

    /// <summary>entry MD5と異なる所持bmsonは解決譜面SHA256からscoreを読み、実譜面MD5表記でlampとEXscoreを返します。</summary>
    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesBmsonChartSha256ForOwnedBmsonEntry()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Songs\\Bmson\\score.bmson",
            Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            Sha256 = new string('c', 64),
            RawTitle = "Bmson score"
        };
        var score = new BMSScore
        {
            hash = bmson.Sha256,
            clear = ClearType.HARD,
            perfect = 700,
            great = 100,
            totalnotes = 900
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [bmson.Sha256] = score
            }
        };

        BMSScore resolved = PlaylistEntryScoreSnapshotResolver.Resolve(
            entry,
            (bmson),
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(bmson.Md5, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
        Assert.AreEqual(1500, resolved.score);
    }

    /// <summary>通常libraryのsort・keyword更新で前段cacheが欠ければfolder段階から再構築し、cacheが揃うsort更新は再構築を省きます。</summary>
    [TestMethod]
    public void ShouldRebuildRegularFolderStage_WhenIncrementalRegularUpdateHasMissingCaches_ReturnsTrue()
    {
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
            MainViewUpdateMode.SortUpdated,
            hasFolderView: false,
            hasKeywordView: true,
            hasModeView: true,
            currentTreeMode: MainViewUpdateMode.FolderFilterSelected));
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
            MainViewUpdateMode.KeywordFilterUpdated,
            hasFolderView: true,
            hasKeywordView: false,
            hasModeView: true,
            currentTreeMode: MainViewUpdateMode.FolderFilterSelected));
        Assert.IsFalse(MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
            MainViewUpdateMode.SortUpdated,
            hasFolderView: true,
            hasKeywordView: true,
            hasModeView: true,
            currentTreeMode: MainViewUpdateMode.FolderFilterSelected));
    }

    /// <summary>同tokenのtitle・path・hash変更は既存行へ通知してscore索引も更新し、別token行を保持して旧hash索引を残しません。</summary>
    [TestMethod]
    public void NormalLibraryRowCache_AppliesOnlyChangedOwnedTokenAndKeepsOtherRows()
    {
        ChartFile first = (CreateBmsonCacheSong(@"folder\chart.bmson", "First"))
            with
        { Token = new OwnedChartToken() };
        ChartFile other = first with { Token = new OwnedChartToken(), Path = @"folder\other.bmson", Title = "Other", Md5 = new string('e', 32), Sha256 = new string('f', 64) };
        var cache = new NormalLibraryRowCache();
        var stats = new LibraryRowCacheBuildStats();
        LibraryChartRow firstRow = cache.GetOrCreate(first, stats);
        LibraryChartRow otherRow = cache.GetOrCreate(other, stats);
        ChartFile changed = first with { Title = "Changed", Path = @"moved\chart.bmson", Md5 = new string('c', 32), Sha256 = new string('d', 64) };
        int notifications = 0;
        firstRow.PropertyChanged += (_, _) => notifications++;

        cache.ApplyChanges([changed], [], null);

        Assert.AreEqual("Changed", firstRow.Title);
        Assert.AreEqual(changed.Path, firstRow.path);
        Assert.AreEqual("Other", otherRow.Title);
        Assert.AreSame(firstRow, cache.GetOrCreate(changed, stats));
        Assert.AreSame(otherRow, cache.GetOrCreate(other, stats));
        Assert.IsTrue(notifications > 0);
        Assert.AreEqual(2, cache.Count);
        Assert.AreEqual(2, stats.MissCount);
        Assert.AreEqual(2, stats.HitCount);
        Assert.AreEqual(0, cache.GetRowsForScoreKeys([first.Md5], []).Count);
        Assert.AreSame(firstRow, cache.GetRowsForScoreKeys([], [changed.Sha256]).Single());
    }

    /// <summary>同じパス・hashでも別tokenは別行として保持し、旧token削除時に新token行とscore索引を誤って消しません。</summary>
    [TestMethod]
    public void NormalLibraryRowCache_SamePathAndHashReplacementRequiresDifferentToken()
    {
        ChartFile first = (CreateBmsonCacheSong(@"folder\chart.bmson", "First"))
            with
        { Token = new OwnedChartToken() };
        ChartFile replacement = first with { Token = new OwnedChartToken() };
        var cache = new NormalLibraryRowCache();
        LibraryChartRow firstRow = cache.GetOrCreate(first, null);
        LibraryChartRow replacementRow = cache.GetOrCreate(replacement, null);

        cache.ApplyChanges([], [first.Token], null);

        Assert.AreNotSame(firstRow, replacementRow);
        Assert.AreEqual(1, cache.Count);
        Assert.AreSame(replacementRow, cache.SnapshotRows().Single());
        Assert.AreSame(replacementRow, cache.GetRowsForScoreKeys([replacement.Md5], []).Single());
    }

    /// <summary>tokenのない譜面はrow cacheへ登録せず件数・miss計測も増やさず、所持rowの寿命へ取り込みません。</summary>
    [TestMethod]
    public void NormalLibraryRowCache_DoesNotOwnDetachedCharts()
    {
        var cache = new NormalLibraryRowCache();
        var stats = new LibraryRowCacheBuildStats();
        ChartFile detached = (CreateBmsonCacheSong(@"folder\detached.bmson", "Detached"));
        Assert.IsNull(cache.GetOrCreate(detached, stats));
        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(0, stats.MissCount);
    }

    private static ChartFile CreateBmsonCacheSong(string path, string title)
    {
        return ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? string.Empty,
            RawTitle = title,
            Title = title,
            RawArtist = "Artist",
            Artist = "Artist",
            Genre = "Genre",
            ModeHint = "beat-7k",
            Mode = 7,
            Level = 7,
            LevelText = "7",
            Md5 = "22222222222222222222222222222222",
            Sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
        };
    }

    /// <summary>通常と未所持の詳細filterを各保存列modeへ配送し、利用者が別々に保存した列設定を同じmodeで読みません。</summary>
    [TestMethod]
    public void ResolvePlaylistColumnSettingMode_ReturnsPlaylistViewModesForBothPlaylistFilters()
    {
        Assert.AreEqual(
            MainViewUpdateMode.PlaylistFilterSelected,
            PlaylistWorkspaceViewModel.ResolvePlaylistColumnSettingMode(PlaylistDetailFilter.PlaylistFilter));
        Assert.AreEqual(
            MainViewUpdateMode.PlaylistNotOwnedFilterSelected,
            PlaylistWorkspaceViewModel.ResolvePlaylistColumnSettingMode(PlaylistDetailFilter.PlaylistNotOwnedFilterSelected));
    }

    private static PlaylistDetailSourceRow CreateSourceRow(string hash, string title, int? mode, string memo = "", string comment = "", double? entryLevel = null, string? sha256 = null)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = hash, Path = title + ".bms", Title = title, RawTitle = title, Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = mode };
        if (sha256 != null)
        {
            file = file with { Sha256 = sha256 };
        }
        var entry = new TestablePlaylistEntry(file)
        {
            memo = memo,
            comment = comment,
            level = entryLevel
        };
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return new PlaylistDetailSourceRow(entry, (file));
    }

    private static PlaylistDetailSourceRow CreateOwnedBmsSourceRow(
        string hash,
        string title,
        int? mode,
        out ChartFile file)
    {
        file = ChartTestValues.Empty();
        file = file with { Md5 = hash, Path = title + ".bms", Title = title, RawTitle = title, Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = mode };
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(new BMSScore
        {
            hash = hash,
            perfect = 100,
            great = 12,
            totalnotes = 200,
            IsLr2IrScoreUnsent = true
        }, file.Path));
        return new PlaylistDetailSourceRow(
            new TestablePlaylistEntry(file),
            (file));
    }


    private static ChartOperationTarget CreateDeleteTarget(ChartOperationSourceScope sourceScope, ChartOperationCapabilities capabilities)
    {
        // 削除判断だけに必要なscope・能力・パスを渡し、物理削除は起動しません。
        var chart = new ChartFile(ChartFileKind.Bms, "C:\\Library\\Song\\chart.bms", "abababababababababababababababab", null, "Title", "Title", "Artist", string.Empty, "Song", string.Empty, "7", 7, 7, null, null);
        bool isPending = sourceScope == ChartOperationSourceScope.PendingPackage;
        bool isPlaylistMissing = sourceScope == ChartOperationSourceScope.PlaylistMissing;
        return new ChartOperationTarget(
            chart,
            null,
            sourceScope,
            !isPending && !isPlaylistMissing,
            isPending,
            isPlaylistMissing,
            capabilities);
    }

    private static PlaylistDetailSourceRow CreateMissingSourceRow(string title, BeMusicSeeker.Models.ChartDetails chartInfo)
    {
        var entry = new TestablePlaylistEntry();
        entry.SetTitle(title);
        entry.SetMd5(chartInfo.md5);
        entry.SetSha256(chartInfo.sha256);
        return new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: chartInfo);
    }

    private static ChartFile CreateBmsFile(string path, string title, string artist, string hash)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, RawTitle = title, Artist = artist, RawArtist = artist, Md5 = hash, Folder = System.IO.Path.GetDirectoryName(path) };
        return file;
    }

    private static BeMusicSeeker.Models.ChartDetails CreateChartInfo(string sha256, string md5, int? level = 12, int notes = 2500, double total = 500)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('a', 64),
            level = level,
            difficulty = 4,
            difficulty_defined = true,
            mainbpm = 180,
            maxbpm = 180,
            minbpm = 120,
            length = 123000,
            mode = 7,
            judge = 100,
            feature = ChartInfoDisplayFormatter.FeatureRandom,
            notes = notes,
            n = notes,
            ln = 10,
            s = 20,
            ls = 5,
            total = total,
            total_defined = true,
            density = 10,
            peakdensity = 20,
            enddensity = 2,
            distribution = "0,1,2",
            speedchange = "0=180",
            speedchange_count = 1,
            lanenotes = "1,2,3,4,5,6,7",
            parser_version = 1,
            updated_at = DateTime.UtcNow
        };
    }

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoProvider(params BeMusicSeeker.Models.ChartDetails[] rows)
    {
        return chart =>
        {
            if (chart == null)
            {
                return null!;
            }
            return (rows ?? [])
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? (rows ?? [])
                    .Where(row => row != null)
                    .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
                ?? null!;
        };
    }

    private static List<BMSTable> CreateBmtSortTables(params int[] playlistIds)
    {
        return [.. playlistIds.Select(id => new BMSTable
        {
            playlist_id = id,
            bmt_sort = id,
            name = "Playlist " + id.ToString(CultureInfo.InvariantCulture)
        })];
    }

    private static PlaylistSummaryRow CreatePlaylistSummaryRow(BMSTable table)
    {
        return new PlaylistSummaryRow
        {
            PlaylistId = table.playlist_id,
            Name = table.name,
            BmtSort = table.bmt_sort ?? int.MaxValue,
            IsBmtOutput = table.is_bmt_output != false,
            TableRef = table
        };
    }

    private static PlayHistoryRow CreateResolvedPlayHistoryRow()
    {
        const string hash = "dddddddddddddddddddddddddddddddd";
        const string sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = @"C:\BMS\play-history-resolved.bms", Title = "Resolved Play History", RawTitle = "Resolved Play History", Artist = "Artist", RawArtist = "Artist", Md5 = hash, Folder = System.IO.Path.GetDirectoryName(@"C:\BMS\play-history-resolved.bms") };
        file = file with { Sha256 = sha256 };
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromCharts([file]);
        var projectionIndex = PlayHistoryProjectionIndex.Create(
            resolveIndex,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 });
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 1,
                        hash = hash,
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);

        return projected.Rows.Single();
    }

    private static PlayHistoryRow CreateUnresolvedPlayHistoryRow()
    {
        const string hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 2,
                        hash = hash,
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        return projected.Rows.Single();
    }

    private static PlaylistBuildRequest CreatePlaylistBuildRequest(PlaylistRequestIdentity identity)
    {
        return new PlaylistBuildRequest
        {
            Identity = identity
        };
    }

    private static PlaylistRequestIdentity CreatePlaylistIdentity(string folderName, long libraryIndexVersion = 3)
    {
        return PlaylistRequestFactory.CreateIdentity(
            new BMSTable(),
            PlaylistDetailSelectionScope.Folder,
            folderName,
            PlaylistDetailFilter.PlaylistFilter,
            keywordFilter: null,
            ChartModeFilter.All,
            sortParameters: null,
            libraryIndexVersion: libraryIndexVersion,
            playlistRevision: 4,
            scoreSnapshotVersion: 5,
            chartInfoIndexVersion: 6,
            hasResolvedSelection: true);
    }

    private static PlaylistDetailTerminalRequest CreatePlaylistTerminalRequest(IList rows, int requestVersion)
    {
        PlaylistRequestIdentity identity = CreatePlaylistIdentity("terminal", libraryIndexVersion: 0);
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        var columnSelection = new MainChartListColumnSelection(
            settings,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.PlaylistFilterSelected,
            System.Windows.Visibility.Collapsed,
            new PlaylistSummaryColumnSettings());
        return new PlaylistDetailTerminalRequest
        {
            BuildRequest = new PlaylistBuildRequest
            {
                RequestVersion = requestVersion,
                Mode = MainViewUpdateMode.PlaylistFilterSelected,
                RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
                Identity = identity
            },
            ReplaceSource = false,
            CurrentFilterType = identity.FilterType,
            ViewRows = rows,
            ColumnSelection = columnSelection,
            MainRowsRequest = new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.NormalRows(rows),
                Stopwatch = stopwatch
            }
        };
    }

    private static PlaylistDetailBuildStateSnapshot CreatePlaylistDetailBuildStateSnapshot(
        PlaylistRequestIdentity identity,
        bool hasSourceRows = true,
        int sourceRowCount = 1,
        PlaylistRequestIdentity? currentViewIdentity = null)
    {
        return new PlaylistDetailBuildStateSnapshot(
            hasSourceRows,
            sourceRowCount,
            identity.Table,
            identity.SelectionScope,
            identity.FolderName,
            identity.FilterType,
            identity.LibraryIndexVersion,
            identity.PlaylistRevision,
            identity.ScoreSnapshotVersion,
            identity.ChartInfoIndexVersion,
            identity.SourceIdentity,
            currentViewIdentity ?? identity);
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public TestablePlaylistEntry()
        {
        }

        public TestablePlaylistEntry(ChartFile bmsFile)
            : base(bmsFile)
        {
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }

        public void SetArtist(string value)
        {
            artist = value;
        }
    }

    private sealed class TrackingDisposableRow : IDisposable
    {
        private readonly bool throwOnDispose;

        internal TrackingDisposableRow(bool throwOnDispose = false)
        {
            this.throwOnDispose = throwOnDispose;
        }

        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }

}
