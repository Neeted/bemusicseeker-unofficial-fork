using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


/// <summary>workspaceの表示状態確定・世代失効・通知とBMT順保存後の選択復元を確認します。</summary>
/// <remarks>実VMケースはMainChartListとの操作mode接続を対象にし、共有composition資源のため非並列です。BMT保存失敗と復元要求は実DBと実workspaceを結び、WPF viewへの実接続はMainWindow側へ分担します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class PlaylistWorkspacePresentationStateTests
{


    /// <summary>実VMのsummary検索補助はoutput field候補を出して値候補を混入させず、blur後に候補を閉じて詳細用補助との誤接続を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryKeywordSearchAssistance_IsFieldsOnlyAndIncludesOutput()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = owner.SettingDialog;
        PlaylistWorkspaceViewModel workspace = owner.PlaylistWorkspace;

        KeywordSearchPresentationState state = workspace.PlaylistSummaryKeywordSearchAssistanceOwner.Focus("ou", 2);

        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(GridKeywordSearchContext.PlaylistSummary, state.Context);
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, state.Sections.Single().Kind);
        Assert.IsTrue(state.VisibleItems.Any(item => item.DisplayText == "output:"));
        Assert.IsFalse(state.VisibleItems.Any(item => item.Kind == KeywordSearchPresentationItemKind.Value));

        KeywordSearchPresentationState blurred = workspace.PlaylistSummaryKeywordSearchAssistanceOwner.Blur();
        Assert.IsFalse(blurred.IsOpen);
    }

    /// <summary>表示中サマリーの警告あり／なしで言語だけを再整形し、行・世代・入力・候補・保存値を保ちます。二重接続は一回の通知、切離しは無通知、再接続は現言語を保証します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(true)]
    [DataRow(false)]
    public void PlaylistSummaryKeywordSearchLanguage_RefreshesOnlyAttachedPresentation(bool warning)
    {
        CultureInfo? previousCulture = Resources.Culture;
        Settings settings = TestSettingsFactory.CreateIsolatedSettings();
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
        using SettingsDialogViewModel settingsLifetime = owner.SettingDialog;
        PlaylistWorkspaceViewModel workspace = owner.PlaylistWorkspace;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            // 警告を持つregexのORにも有効な選択肢を残し、実フィルターで一致する行を表示します。
            // 非表示中に条件を準備し、初期化時の非同期再取得を言語通知の観測へ持ち込みません。
            string raw = warning ? "  name:re:Alpha|[ na  " : "  name:alpha na  ";
            workspace.PlaylistSummaryKeywordFilter = raw;
            workspace.IsPlaylistSummaryMode = true;
            workspace.IsPlaylistSummaryKeywordSearchHelpOpen = true;
            var sourceRow = new PlaylistSummaryRow { PlaylistId = 7, Name = "Alpha name" };
            Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
            {
                Rows = new ObservableCollection<PlaylistSummaryRow>(PlaylistWorkspaceViewModel.ApplyPlaylistSummaryFilters(
                    [sourceRow], raw, PlaylistOwnedFilter.All)),
                SummaryText = "summary",
                PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
            }));
            KeywordSearchAssistanceOwner assistance = workspace.PlaylistSummaryKeywordSearchAssistanceOwner;
            Assert.IsTrue(assistance.SavedQueryOwner.TryCommitHistory("name:history").Succeeded);
            Assert.IsTrue(assistance.TryAddFavorite("name:favorite").Succeeded);
            KeywordSearchPresentationState presentation = assistance.Focus(raw, raw.Length - 2);
            KeywordSearchSavedQueryState saved = assistance.SavedQueryOwner.State;
            string history = settings.PlaylistSummaryKeywordSearchHistory;
            string favorites = settings.PlaylistSummaryKeywordSearchFavorites;
            ObservableCollection<PlaylistSummaryRow> rows = workspace.PlaylistSummaryView;
            PlaylistSummaryRow row = rows.Single();
            long generation = workspace.CurrentPlaylistSummaryPresentationGeneration;
            long dataGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
            var notifications = new List<string>();
            int refreshes = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => refreshes++;
            workspace.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            notifications.Clear();

            ResourceService.Current.ChangeCulture("en-US");

            Assert.AreEqual(warning ? string.Format(Resources.Keyword_search_warning_invalid_regex, "[") : string.Empty, workspace.PlaylistSummaryKeywordSearchWarningText);
            Assert.AreEqual(string.Format(Resources.Keyword_search_help_template, Resources.Keyword_search_help_fields_playlist_summary), workspace.PlaylistSummaryKeywordSearchHelpText);
            Assert.AreEqual(warning, workspace.HasPlaylistSummaryKeywordSearchWarning);
            Assert.AreEqual(1, notifications.Count(name => name == nameof(workspace.PlaylistSummaryKeywordSearchHelpText)));
            Assert.AreEqual(warning ? 1 : 0, notifications.Count(name => name == nameof(workspace.PlaylistSummaryKeywordSearchWarningText)));
            Assert.AreEqual(0, refreshes);
            Assert.AreEqual(raw, workspace.PlaylistSummaryKeywordFilter);
            Assert.IsTrue(workspace.IsPlaylistSummaryKeywordSearchHelpOpen);
            Assert.AreSame(presentation, assistance.Presentation);
            Assert.AreSame(saved, assistance.SavedQueryOwner.State);
            Assert.AreEqual(history, settings.PlaylistSummaryKeywordSearchHistory);
            Assert.AreEqual(favorites, settings.PlaylistSummaryKeywordSearchFavorites);
            Assert.AreSame(rows, workspace.PlaylistSummaryView);
            Assert.AreSame(row, workspace.PlaylistSummaryView.Single());
            Assert.AreEqual("summary", workspace.PlaylistSummaryText);
            Assert.AreEqual(generation, workspace.CurrentPlaylistSummaryPresentationGeneration);
            Assert.AreEqual(dataGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            Assert.AreEqual(cacheGeneration, workspace.CurrentPlaylistSummaryRowsCacheGeneration);

            workspace.DetachPlaylistSummaryKeywordSearchPresentation();
            notifications.Clear();
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, notifications.Count);
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            Assert.AreEqual(warning ? string.Format(Resources.Keyword_search_warning_invalid_regex, "[") : string.Empty, workspace.PlaylistSummaryKeywordSearchWarningText);
            Assert.AreEqual(1, notifications.Count(name => name == nameof(workspace.PlaylistSummaryKeywordSearchHelpText)));
            Assert.AreEqual(0, refreshes);
            Assert.AreSame(presentation, assistance.Presentation);
            Assert.AreSame(saved, assistance.SavedQueryOwner.State);
        }
        finally
        {
            workspace.DetachPlaylistSummaryKeywordSearchPresentation();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>現行summary結果の行・要約を通知し、適用済み性能interactionの世代を保持して次要求の開始だけで適用情報を上書きしません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistWorkspaceSummaryApplyCommitsRowsAndTextWithoutSelectionRestore()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache();
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() { TotalCharts = 3 } };
        var notifications = new List<string>();
        workspace.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);

        bool applied = workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "3 charts / 1 playlist",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        });

        Assert.IsTrue(applied);
        CollectionAssert.AreEquivalent(
            new[] { nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView), nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText) },
            notifications);
        CollectionAssert.AreEqual(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("3 charts / 1 playlist", workspace.PlaylistSummaryText);
        Assert.IsTrue(
            workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction interaction));
        Assert.AreEqual(dataGeneration, interaction.InteractionId);
        Assert.AreEqual(presentationGeneration, interaction.Generation);

        workspace.BeginPlaylistSummaryPresentationGeneration();

        Assert.IsTrue(
            workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction retainedInteraction));
        Assert.AreEqual(interaction, retainedInteraction);
    }

    /// <summary>同じ表示identityの再適用は初回行を保持してreset・source通知を各一回に抑え、同じcollection接続を再置換しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistWorkspaceSummaryApplyKeepsStableSourceAndSkipsSamePresentationIdentity()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = owner.SettingDialog;
        PlaylistWorkspaceViewModel workspace = owner.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        ObservableCollection<PlaylistSummaryRow> stableSource = workspace.PlaylistSummaryView;
        int resetCount = 0;
        int sourceNotificationCount = 0;
        stableSource.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                sourceNotificationCount++;
            }
        };
        var identity = new PlaylistSummaryPresentationIdentity(
            sourceVersion: 11L,
            keywordFilter: string.Empty,
            PlaylistOwnedFilter.All,
            nameof(PlaylistSummaryRow.Name),
            ListSortDirection.Ascending);

        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 1 }],
            SummaryText = "first",
            Identity = identity,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 2 }],
            SummaryText = "first",
            Identity = identity,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));

        Assert.AreSame(stableSource, workspace.PlaylistSummaryView);
        Assert.AreEqual(1, workspace.PlaylistSummaryView[0].PlaylistId);
        Assert.AreEqual(1, resetCount);
        Assert.AreEqual(1, sourceNotificationCount);
    }

    /// <summary>summary要求だけでは表示modeを切替せず、行reset後にmodeを公開して実MainChartListのplaylist操作sectionへ接続します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistWorkspaceSummaryTerminalCommitsRowsBeforeVisibleMode()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        var publicationOrder = new List<string>();
        workspace.PlaylistSummaryView.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                publicationOrder.Add("rows");
            }
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode))
            {
                publicationOrder.Add("mode");
            }
        };

        Assert.IsTrue(workspace.RequestPlaylistSummaryMode(enabled: true));
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 3 }],
            SummaryText = "ready",
            Identity = new PlaylistSummaryPresentationIdentity(
                sourceVersion: 17L,
                keywordFilter: string.Empty,
                PlaylistOwnedFilter.All,
                nameof(PlaylistSummaryRow.Name),
                ListSortDirection.Ascending),
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));

        CollectionAssert.AreEqual(new[] { "rows", "mode" }, publicationOrder);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(
            MainViewOperationSection.Playlist,
            viewModel.MainChartList.CurrentOperationContext.OperationSection);
    }

    /// <summary>summary退出後もdata世代とcache行を再利用でき、catalog無効化時には破棄して退出と内容変更を混同しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistWorkspaceSummaryCacheSurvivesModeExitUntilCatalogInvalidation()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = owner.SettingDialog;
        PlaylistWorkspaceViewModel workspace = owner.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            [new PlaylistSummaryRow { PlaylistId = 7 }],
            dataGeneration));

        workspace.IsPlaylistSummaryMode = false;
        List<PlaylistSummaryRow> cachedRows =
            workspace.GetPlaylistSummaryRowsCacheSnapshot(out long cacheGeneration, out long cachedDataGeneration);

        Assert.IsNotNull(cachedRows);
        Assert.AreEqual(7, cachedRows[0].PlaylistId);
        Assert.AreEqual(dataGeneration, cachedDataGeneration);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryRowsCacheGeneration, cacheGeneration);

        workspace.InvalidatePlaylistSummaryCache();
        Assert.IsNull(workspace.GetPlaylistSummaryRowsCacheSnapshot(out _, out _));
    }

}
