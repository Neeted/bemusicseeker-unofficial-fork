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
            Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
            Assert.AreEqual(1, notifications.Count(name => name == nameof(workspace.GridHeaderText)));
            Assert.AreEqual(0, notifications.Count(name => name == nameof(workspace.PlaylistSummaryText)));
            Assert.AreEqual(generation, workspace.CurrentPlaylistSummaryPresentationGeneration);
            Assert.AreEqual(dataGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            Assert.AreEqual(cacheGeneration, workspace.CurrentPlaylistSummaryRowsCacheGeneration);

            string enHeader = workspace.GridHeaderText;
            workspace.DetachPlaylistSummaryKeywordSearchPresentation();
            notifications.Clear();
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, notifications.Count);
            Assert.AreEqual(enHeader, workspace.GridHeaderText);
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
            Assert.AreNotEqual(enHeader, workspace.GridHeaderText);
            Assert.AreEqual("summary", workspace.PlaylistSummaryText);
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

    /// <summary>採用済みのsummaryは言語往復で見出しと自動件数を現在語へ再整形し、未採用の要求・退出後は何も出しません。通常・0件・絞込み後で、行・世代・scheduler呼出しなどの再構築痕跡を増やさないことを保証します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("normal", 5, 2)]
    [DataRow("zero", 0, 0)]
    [DataRow("filtered", 2, 1)]
    public void PlaylistSummaryLanguage_ReformatsAdoptedHeaderAndCountsWithoutRebuild(string scenario, int expectedCharts, int expectedTables)
    {
        CultureInfo? previousCulture = Resources.Culture;
        int schedulerCalls = 0;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _, restoreUiApplyScheduler: action =>
            {
                schedulerCalls++;
                action();
                return Task.CompletedTask;
            });
            int refreshRequests = 0, buildStarts = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => refreshRequests++;
            workspace.PlaylistSummaryBuildStarted += (_, _) => buildStarts++;
            if (scenario == "filtered") { workspace.PlaylistSummaryKeywordFilter = "alpha"; }
            Assert.IsTrue(workspace.RequestPlaylistSummaryMode(enabled: true));
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            var notifications = new List<string>();
            workspace.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);

            // 要求だけで未採用の間は、言語を変えても見出しと件数を出さない。
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(string.Empty, workspace.GridHeaderText);
            Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
            CollectionAssert.DoesNotContain(notifications, nameof(workspace.GridHeaderText));
            CollectionAssert.DoesNotContain(notifications, nameof(workspace.PlaylistSummaryText));

            PlaylistSummaryRow[] source = scenario == "zero"
                ? []
                :
                [
                    new PlaylistSummaryRow { PlaylistId = 1, Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                    new PlaylistSummaryRow { PlaylistId = 2, Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
                ];
            Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(source, workspace.BeginPlaylistSummaryDataRebuildGeneration()));
            workspace.RequestPlaylistSummaryPresentationRefresh();
            Assert.IsTrue(workspace.IsPlaylistSummaryMode);

            string Counts() => string.Format(Resources.Playlist_summary_format, expectedCharts, expectedTables);
            Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
            Assert.AreEqual(Counts(), workspace.PlaylistSummaryText);
            Assert.AreEqual(expectedTables, workspace.PlaylistSummaryView.Count);

            ObservableCollection<PlaylistSummaryRow> rows = workspace.PlaylistSummaryView;
            PlaylistSummaryRow[] rowInstances = [.. rows];
            long presentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
            long dataGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
            long appliedDataGeneration = workspace.AppliedPlaylistSummaryDataGeneration;
            long completedTimestamp = workspace.LastPlaylistSummaryBuildCompletedTimestamp;
            Assert.IsTrue(workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(out PerformanceInteraction interaction));
            int callsBefore = schedulerCalls, refreshesBefore = refreshRequests, buildsBefore = buildStarts;
            int collectionChanges = 0;
            rows.CollectionChanged += (_, _) => collectionChanges++;

            string headerBefore = Resources.Playlist_summary_header;
            string countsBefore = workspace.PlaylistSummaryText;
            foreach (string culture in new[] { "ja-JP", "en-US", "ja-JP" })
            {
                notifications.Clear();
                ResourceService.Current.ChangeCulture(culture);
                Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
                Assert.AreEqual(Counts(), workspace.PlaylistSummaryText);
                Assert.AreNotEqual(headerBefore, workspace.GridHeaderText);
                Assert.AreNotEqual(countsBefore, workspace.PlaylistSummaryText);
                headerBefore = workspace.GridHeaderText;
                countsBefore = workspace.PlaylistSummaryText;
                Assert.AreEqual(1, notifications.Count(name => name == nameof(workspace.GridHeaderText)));
                Assert.AreEqual(1, notifications.Count(name => name == nameof(workspace.PlaylistSummaryText)));
                CollectionAssert.DoesNotContain(notifications, nameof(workspace.PlaylistSummaryView));
                CollectionAssert.DoesNotContain(notifications, nameof(workspace.IsPlaylistSummaryMode));
                CollectionAssert.DoesNotContain(notifications, nameof(workspace.IsPlaylistDetailViewActive));
                CollectionAssert.DoesNotContain(notifications, nameof(workspace.PlaylistSummarySortParameters));
                CollectionAssert.DoesNotContain(notifications, nameof(workspace.PlaylistSummaryKeywordFilter));
                CollectionAssert.DoesNotContain(notifications, nameof(workspace.PlaylistSummaryOwnedFilter));
            }

            Assert.AreSame(rows, workspace.PlaylistSummaryView);
            CollectionAssert.AreEqual(rowInstances, rows.ToArray());
            Assert.AreEqual(0, collectionChanges);
            Assert.AreEqual(presentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
            Assert.AreEqual(dataGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            Assert.AreEqual(cacheGeneration, workspace.CurrentPlaylistSummaryRowsCacheGeneration);
            Assert.AreEqual(appliedDataGeneration, workspace.AppliedPlaylistSummaryDataGeneration);
            Assert.AreEqual(completedTimestamp, workspace.LastPlaylistSummaryBuildCompletedTimestamp);
            Assert.IsTrue(workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(out PerformanceInteraction afterInteraction));
            Assert.AreEqual(interaction, afterInteraction);
            Assert.AreEqual(callsBefore, schedulerCalls);
            Assert.AreEqual(refreshesBefore, refreshRequests);
            Assert.AreEqual(buildsBefore, buildStarts);

            // 概要を抜けた後は見出しも件数も空のまま、言語変更で通知しない。
            Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
            notifications.Clear();
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(string.Empty, workspace.GridHeaderText);
            Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
            CollectionAssert.DoesNotContain(notifications, nameof(workspace.GridHeaderText));
            CollectionAssert.DoesNotContain(notifications, nameof(workspace.PlaylistSummaryText));
        }
        finally
        {
            workspace?.DetachPlaylistSummaryKeywordSearchPresentation();
            workspace?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>UI適用待ちの間の言語変更は採用済みの件数で再整形し、待機中の適用を増減させません。後から採用された結果は現在語で出ます。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryLanguage_QueuedPresentationKeepsAdoptedCountsUntilAdoption()
    {
        CultureInfo? previousCulture = Resources.Culture;
        var queued = new List<Action>();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queueing = false;
        int schedulerCalls = 0;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _, restoreUiApplyScheduler: action =>
            {
                schedulerCalls++;
                if (!queueing) { action(); return Task.CompletedTask; }
                queued.Add(action);
                return applied.Task;
            });
            int refreshRequests = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => refreshRequests++;
            workspace.RequestPlaylistSummaryMode(enabled: true);
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
                [new PlaylistSummaryRow { PlaylistId = 1, Name = "alpha", TotalCharts = 2 }, new PlaylistSummaryRow { PlaylistId = 2, Name = "beta", TotalCharts = 3 }],
                workspace.BeginPlaylistSummaryDataRebuildGeneration()));
            workspace.RequestPlaylistSummaryPresentationRefresh();
            Assert.AreEqual(string.Format(Resources.Playlist_summary_format, 5, 2), workspace.PlaylistSummaryText);

            // 絞込みの再表示をUI適用待ちにしたまま、言語だけを変える。
            queueing = true;
            workspace.PlaylistSummaryKeywordFilter = "alpha";
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
            int callsBefore = schedulerCalls, refreshesBefore = refreshRequests;
            long generation = workspace.CurrentPlaylistSummaryPresentationGeneration;

            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
            Assert.AreEqual(string.Format(Resources.Playlist_summary_format, 5, 2), workspace.PlaylistSummaryText);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual(callsBefore, schedulerCalls);
            Assert.AreEqual(refreshesBefore, refreshRequests);
            Assert.AreEqual(generation, workspace.CurrentPlaylistSummaryPresentationGeneration);

            queued.Single()();
            Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
            Assert.AreEqual(string.Format(Resources.Playlist_summary_format, 2, 1), workspace.PlaylistSummaryText);
            Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
            Assert.AreEqual(1, queued.Count);
        }
        finally
        {
            applied.TrySetResult();
            workspace?.DetachPlaylistSummaryKeywordSearchPresentation();
            workspace?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>概要セルの局所編集で変わった件数は、その後の言語変更でも編集後の値で再整形し、一括採用時の件数へ戻さず再構築もしません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistSummaryLanguage_LocalEditCountIsReformattedWithoutRebuild()
    {
        CultureInfo? previousCulture = Resources.Culture;
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            PlaylistWorkspaceViewModel workspace = fixture.Workspace;
            workspace.RequestPlaylistSummaryMode(enabled: true);
            workspace.RebuildPlaylistSummaryView(runAsync: false);
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            workspace.AttachPlaylistSummaryKeywordSearchPresentation();
            PlaylistSummaryRow row = workspace.PlaylistSummaryView.Single(item => ReferenceEquals(item.TableRef, fixture.Table));
            // 一括採用時は絞込みに合う1行を表示し、編集で絞込みから外れて表示件数を変える。
            workspace.PlaylistSummaryKeywordFilter = "name:Prefix";
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
            string bulkText = workspace.PlaylistSummaryText;
            Assert.IsTrue((await workspace.CompleteSummaryPropertyEditAsync(row, nameof(PlaylistSummaryRow.Name), "Other", true)).IsApplied);
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            int viewCount = workspace.PlaylistSummaryView.Count;
            int charts = workspace.PlaylistSummaryView.Sum(item => item.TotalCharts);
            int refreshes = 0, builds = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => refreshes++;
            workspace.PlaylistSummaryBuildStarted += (_, _) => builds++;
            ObservableCollection<PlaylistSummaryRow> rows = workspace.PlaylistSummaryView;
            PlaylistSummaryRow[] instances = [.. rows];
            long presentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
            long dataGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;

            string editedText = string.Format(Resources.Playlist_summary_format, charts, viewCount);
            Assert.AreEqual(editedText, workspace.PlaylistSummaryText);
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(string.Format(Resources.Playlist_summary_format, charts, viewCount), workspace.PlaylistSummaryText);
            Assert.AreNotEqual(editedText, workspace.PlaylistSummaryText);
            Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
            Assert.AreSame(rows, workspace.PlaylistSummaryView);
            CollectionAssert.AreEqual(instances, rows.ToArray());
            Assert.AreEqual(presentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
            Assert.AreEqual(dataGeneration, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            Assert.AreEqual(0, refreshes);
            Assert.AreEqual(0, builds);
            Assert.AreEqual(0, viewCount);
            Assert.AreNotEqual(bulkText, workspace.PlaylistSummaryText);
        }
        finally
        {
            fixture.Workspace.DetachPlaylistSummaryKeywordSearchPresentation();
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
