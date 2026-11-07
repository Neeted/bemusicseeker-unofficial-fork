using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;
using Resources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 進捗行の言語追従を、本番の <see cref="ResourceService.ChangeCulture"/> を入口に確認します。
/// 保持した事実は変えず、行の文言だけが現在の資源へ切り替わり、次の通常進捗でも現在語を維持することを保証します。
/// </summary>
/// <remarks>
/// 共有Resourcesの文化圏を変えるため並列実行しません。非WPFのため、変更・更新・表明はテストスレッドで同期的に行います。
/// 完了表示のhide後を観測するケースでは、プールスレッドで動くhide継続の表示反映をキューへ入れてテストスレッドで消化し、
/// 本番のUI配送と同じく、一回の反映を観測スレッド上で一括して終えてから次の操作へ進みます。
/// </remarks>
[TestClass]
[TestCategory("Windows.Shell")]
[DoNotParallelize]
public sealed class OperationProgressHubLanguageTests
{
    private const string TableName = "表A";

    /// <summary>行の言語に依存しない事実です。</summary>
    private sealed record RowFacts(string Key, string ParentKey, double Value, double Maximum, bool IsIndeterminate, bool HasGauge, OperationProgressAction Action);

    private static RowFacts[] CaptureFacts(OperationProgressHubViewModel hub) =>
        [.. hub.Rows.Select(row => new RowFacts(row.Key, row.ParentKey, row.Value, row.Maximum, row.IsIndeterminate, row.HasGauge, row.Action))];

    /// <summary>文化圏を保存して日本語へ揃え、終了時に購読解除・文化圏復元を必ず行います。</summary>
    private static void RunWithJapanese(OperationProgressHubViewModel? hubToDetach, Action<Action<OperationProgressHubViewModel>> body)
    {
        CultureInfo? previous = Resources.Culture;
        var detached = new List<OperationProgressHubViewModel>();
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            body(hub => detached.Add(hub));
        }
        finally
        {
            foreach (OperationProgressHubViewModel hub in detached) { hub.DetachLanguagePresentation(); }
            hubToDetach?.DetachLanguagePresentation();
            ResourceService.Current.ChangeCulture(previous?.Name ?? "en-US");
            Resources.Culture = previous;
        }
    }

    private sealed class StartupHarness
    {
        internal int InitializationCompleted;
        internal int PrepareOperation;
        internal int VersionSnapshot;
        internal int HideDelay;
        internal TaskCompletionSource? HideGate;
        internal TaskCompletionSource HideEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>UI配送の模擬キューです。別スレッドからの投入と、テストスレッドでの消化・到着待ちを同じ集合で扱います。</summary>
        internal BlockingCollection<Action> PendingDispatch = new();
        /// <summary>配送をキューへ入れるかを切り替えます。hide後を観測するケースではゲートの解放より前に切り替え、プールスレッドの継続から見えるようにします。</summary>
        internal volatile bool QueueDispatch;
        internal StartupProgressWorkflowOwner Owner = null!;

        internal static StartupHarness Create(bool blockHide = false)
        {
            var harness = new StartupHarness();
            if (blockHide) { harness.HideGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
            harness.Owner = new StartupProgressWorkflowOwner(
                () => { harness.VersionSnapshot++; return new StartupProgressVersionSnapshot(); },
                (_, _) => harness.PrepareOperation++,
                action => { if (harness.QueueDispatch) { harness.PendingDispatch.Add(action); } else { action(); } },
                _ => { },
                _ => harness.InitializationCompleted++,
                new object(),
                () =>
                {
                    harness.HideDelay++;
                    harness.HideEntered.TrySetResult();
                    return harness.HideGate?.Task ?? Task.Delay(Timeout.Infinite);
                });
            return harness;
        }

        internal void Drain() { while (PendingDispatch.TryTake(out Action? action)) { action(); } }

        /// <summary>次の配送の到着を待ち、テストスレッドで実行します。生産元が配送せずに終わった場合の待ち続けは、検証全体の期限で検出します。</summary>
        internal void RunNextDispatch() => PendingDispatch.Take()();
    }

    private static string StartupLabel(OperationProgressHubViewModel hub) =>
        StartupProgressWorkflowOwner.FormatStartupProgressCountLabel(Resources.Statusbar_progress_startup,
            (int)hub.StartupProgress.Value, (int)hub.StartupProgress.Maximum, string.Empty);

    private static string ImportPhaseText(PlaylistImportProgressPhase phase, bool beatoraja) => phase switch
    {
        PlaylistImportProgressPhase.CheckUrls => Resources.Beatoraja_table_url_import_progress_phase_check_urls,
        PlaylistImportProgressPhase.LoadTables => beatoraja ? Resources.Beatoraja_table_url_import_progress_phase_load_tables : Resources.Playlist_import_progress_phase_load_tables,
        PlaylistImportProgressPhase.RestoreBmt => Resources.Beatoraja_table_url_import_progress_phase_restore_bmt,
        PlaylistImportProgressPhase.CheckDuplicates => Resources.Playlist_import_progress_phase_check_duplicates,
        PlaylistImportProgressPhase.RegisterPlaylists => beatoraja ? Resources.Beatoraja_table_url_import_progress_phase_register_playlists : Resources.Playlist_import_progress_phase_register_playlists,
        PlaylistImportProgressPhase.UpdateReferences => beatoraja ? Resources.Beatoraja_table_url_import_progress_phase_update_references : Resources.Playlist_import_progress_phase_update_references,
        PlaylistImportProgressPhase.ApplyBmtSort => Resources.Beatoraja_table_url_import_progress_phase_apply_bmt_sort,
        _ => beatoraja ? Resources.Beatoraja_table_url_import_progress_phase_finish : Resources.Playlist_import_progress_phase_finish
    };

    /// <summary>1件の playlist 行を、保持する事実と現在の資源から期待値を作れる形で定義します。</summary>
    private sealed record PlaylistCase(string Source, long OperationId, Action<PlaylistSyncProgressSnapshot> Configure,
        Func<string> Label, Func<string> Detail)
    {
        internal string Key => "playlist:" + Source + ":" + OperationId;

        internal PlaylistSyncProgressSnapshot CreateSnapshot(int completed)
        {
            var snapshot = new PlaylistSyncProgressSnapshot
            {
                Source = Source,
                OperationId = OperationId,
                IsActive = true,
                TotalTableCount = 3,
                CompletedTableCount = completed,
                CurrentTableName = TableName
            };
            Configure(snapshot);
            return snapshot;
        }
    }

    private static List<PlaylistCase> CreatePlaylistCases()
    {
        var cases = new List<PlaylistCase>();
        string Counted(string format, int count = 1) => string.Format(CultureInfo.CurrentCulture, format, count, 3);
        cases.Add(new("w1_default", 0, s => s.Kind = PlaylistSyncProgressKind.Default,
            () => Counted(Resources.Playlist_sync_progress_label_format), () => TableName));
        cases.Add(new("w1_default_single", 0, s => { s.Kind = PlaylistSyncProgressKind.Default; s.TotalTableCount = 0; },
            () => Resources.Playlist_sync_progress_single_label, () => TableName));
        PlaylistImportProgressPhase[] importPhases = [PlaylistImportProgressPhase.LoadTables, PlaylistImportProgressPhase.CheckDuplicates,
            PlaylistImportProgressPhase.RegisterPlaylists, PlaylistImportProgressPhase.UpdateReferences, PlaylistImportProgressPhase.Finish];
        foreach (PlaylistImportProgressPhase phase in importPhases)
        {
            PlaylistImportProgressPhase captured = phase;
            cases.Add(new("w1_import_" + phase, 0, s => { s.Kind = PlaylistSyncProgressKind.ExternalPlaylistImport; s.ImportPhase = captured; },
                () => Counted(Resources.Playlist_import_progress_label_format), () => ImportPhaseText(captured, false) + ": " + TableName));
        }
        cases.Add(new("w1_import_no_table", 0, s =>
            {
                s.Kind = PlaylistSyncProgressKind.ExternalPlaylistImport; s.ImportPhase = PlaylistImportProgressPhase.LoadTables;
                s.CurrentTableName = string.Empty; s.CurrentUri = new Uri("https://example.invalid/import");
            },
            () => Counted(Resources.Playlist_import_progress_label_format), () => ImportPhaseText(PlaylistImportProgressPhase.LoadTables, false)));
        PlaylistImportProgressPhase[] beatorajaPhases = [PlaylistImportProgressPhase.CheckUrls, PlaylistImportProgressPhase.LoadTables,
            PlaylistImportProgressPhase.RestoreBmt, PlaylistImportProgressPhase.RegisterPlaylists, PlaylistImportProgressPhase.UpdateReferences,
            PlaylistImportProgressPhase.ApplyBmtSort, PlaylistImportProgressPhase.Finish];
        foreach (PlaylistImportProgressPhase phase in beatorajaPhases)
        {
            PlaylistImportProgressPhase captured = phase;
            cases.Add(new("w1_beatoraja_" + phase, 0, s => { s.Kind = PlaylistSyncProgressKind.BeatorajaTableUrlImport; s.ImportPhase = captured; },
                () => Counted(Resources.Beatoraja_table_url_import_progress_label_format), () => ImportPhaseText(captured, true) + ": " + TableName));
        }
        cases.Add(new("external_playlist_sync", 0, s => { s.Kind = PlaylistSyncProgressKind.ExternalPlaylistSync; s.CompletedTableCount = 2; s.CurrentTableName = "B"; },
            () => string.Format(Resources.Playlist_reload_processed_label_format, Resources.Statusbar_progress_task_external_playlist_sync, 2, 3),
            () => string.Format(Resources.Playlist_reload_last_processed_label_format, "B")));
        cases.Add(new("playlist_manual_reload", 0, s =>
            {
                s.Kind = PlaylistSyncProgressKind.ManualPlaylistReload; s.CompletedTableCount = 2; s.CurrentTableName = string.Empty;
                s.CurrentUri = new Uri("https://example.invalid/last-completed");
            },
            () => string.Format(Resources.Playlist_reload_processed_label_format, Resources.Statusbar_progress_task_playlist_manual_reload, 2, 3),
            () => string.Format(Resources.Playlist_reload_last_processed_label_format, "https://example.invalid/last-completed")));
        cases.Add(new("bmt", 7, s => s.Kind = PlaylistSyncProgressKind.BmtOutput,
            () => Counted(Resources.Beatoraja_bmt_export_progress_label_format), () => TableName));
        cases.Add(new("custom_folder_repair", 1, s => s.Kind = PlaylistSyncProgressKind.CustomFolderRepair,
            () => Resources.Statusbar_progress_task_custom_folder_repair + " 1/3", () => TableName));
        cases.Add(new("custom_folder_repair", 2, s => { s.Kind = PlaylistSyncProgressKind.CustomFolderRepair; s.CustomFolderStage = CustomFolderProgressStage.DbSync; s.CurrentTableName = string.Empty; },
            () => Resources.Statusbar_progress_task_custom_folder_repair + " 1/3", () => Resources.Custom_folder_db_sync_progress_single_label));
        cases.Add(new("property_save", 0, s => s.Kind = PlaylistSyncProgressKind.PropertyUpdate,
            () => Resources.Statusbar_progress_task_playlist_property_update + " 1/3", () => TableName));
        cases.Add(new("playlist_summary_bulk", 1, s => s.Kind = PlaylistSyncProgressKind.ExternalPropertyInitialization,
            () => Resources.Statusbar_progress_task_playlist_external_property_initialization + " 1/3", () => TableName));
        cases.Add(new("playlist_summary_bulk", 2, s => s.Kind = PlaylistSyncProgressKind.CustomFolderOutput,
            () => Counted(Resources.Custom_folder_output_progress_label_format), () => TableName));
        cases.Add(new("playlist_summary_bulk", 3, s => { s.Kind = PlaylistSyncProgressKind.CustomFolderOutput; s.CustomFolderStage = CustomFolderProgressStage.Output; s.CurrentTableName = string.Empty; },
            () => Counted(Resources.Custom_folder_output_progress_label_format), () => Resources.Custom_folder_output_progress_single_label));
        cases.Add(new("playlist_summary_bulk", 4, s => { s.Kind = PlaylistSyncProgressKind.CustomFolderOutput; s.CustomFolderStage = CustomFolderProgressStage.DbSync; s.CurrentTableName = string.Empty; },
            () => Counted(Resources.Custom_folder_output_progress_label_format), () => Resources.Custom_folder_db_sync_progress_single_label));
        return cases;
    }

    /// <summary>全 playlist 種別、StartupProgress、DetailRows、推定、背景、追加処理の親、LR2を同時に投入し、日本語→英語→日本語で全行の文言が現在語の資源と保持事実から作られ、事実と呼出し回数が変わらないことを確認します。</summary>
    [TestMethod]
    public void HubRows_LanguageRoundTripRefreshesEveryProjectionWithoutChangingFacts()
    {
        var startup = StartupHarness.Create();
        var hub = new OperationProgressHubViewModel(startup.Owner);
        RunWithJapanese(hub, _ =>
        {
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
            hub.AttachPlaylistProgressSources(workspace, action => action(), () => false);

            long token = startup.Owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            startup.Owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyData, token);
            startup.Owner.UpdateStartupProgressLibraryInitializationStatuses([
                new(1, BMSLibrary.LibraryInitializationProgressStage.DatabaseLoad, "", 0, 0, "", token),
                new(2, BMSLibrary.LibraryInitializationProgressStage.FileEnumeration, "Native", 0, 12, "", token),
                new(3, BMSLibrary.LibraryInitializationProgressStage.FileDiff, "Fallback", 8, 3, @"C:\bms\a.bms", token)]);
            var chartInfoRequest = new OperationProgressRequest(1, token, "chart_info_hydration", 1);
            startup.Owner.TrackStartupProgressChartInfoHydrationRequested(1, chartInfoRequest);
            startup.Owner.UpdateStartupProgressChartInfoHydrationStatus(new ChartInfoWorkflowProgressSnapshot(1, 20, 5, string.Empty, chartInfoRequest));

            hub.BeginBackgroundProgressGeneration(1);
            hub.BeginStartupBackgroundInitializationPresentation(token, 1);
            hub.UpdateAdditionalProgress(new StartupAdditionalProgressSnapshot(token, 1, 1, 3, 1, false));
            hub.UpdateBackgroundTaskProgress(new("ranking_refresh", 1, 1, false, true));
            hub.UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot
            {
                IsActive = true,
                TotalWorkCount = 8,
                CompletedWorkCount = 3,
                CurrentDisplayName = "pkg_日本語.zip"
            });
            DateTime checkedAt = new(2026, 3, 8, 20, 0, 0);
            Lr2SongDbSyncRuntimeStatus lr2 = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Running,
                Stage = "song_rows",
                StageProcessedCount = 2,
                StageTotalCount = 5,
                ProcessedCursor = 10,
                TotalCount = 100
            }, checkedAt);
            hub.UpdateLr2SongDbSyncStatus(lr2);

            List<PlaylistCase> cases = CreatePlaylistCases();
            foreach (PlaylistCase item in cases) { workspace.ReportPlaylistSyncProgress(item.CreateSnapshot(1)); }

            string ExpectedLr2Tooltip() => string.Join(Environment.NewLine, DateTimeDisplayFormatter.FormatTimestamp(checkedAt),
                string.Format(Resources.Lr2_song_db_sync_saved_position, 10, 100), Resources.Lr2_song_db_sync_stage_song_rows);

            void AssertCurrentLanguage(string stage)
            {
                OperationProgressRow[] rows = [.. hub.Rows];
                OperationProgressRow Row(string key) => rows.Single(row => row.Key == key);
                Assert.AreEqual(StartupLabel(hub), Row("startup").Label, stage);
                Assert.AreEqual(string.Empty, Row("startup").Detail, stage);
                Assert.AreEqual(Resources.Statusbar_progress_phase_ui_prepare, Row("task:ui_prepare").Label, stage);
                Assert.AreEqual(Resources.Statusbar_progress_phase_library_db_load, Row("library:DatabaseLoad:").Label, stage);
                Assert.AreEqual(Resources.Statusbar_progress_phase_file_enumeration + " (Native)", Row("library:FileEnumeration:Native").Label, stage);
                Assert.AreEqual("[3/8] " + Resources.Statusbar_progress_phase_file_diff + " (Fallback)", Row("library:FileDiff:Fallback").Label, stage);
                Assert.AreEqual(@"C:\bms\a.bms", Row("library:FileDiff:Fallback").Detail, stage);
                Assert.AreEqual("[5/20] " + Resources.Statusbar_progress_phase_chart_info_load, Row("task:chart_info_hydration").Label, stage);
                Assert.AreEqual("[1/3] " + Resources.Statusbar_progress_startup_additional, Row("startup_background").Label, stage);
                Assert.AreEqual(Resources.Statusbar_progress_phase_ranking_refresh, rows.Single(row => row.Key.StartsWith("background:ranking_refresh:", StringComparison.Ordinal)).Label, stage);
                Assert.AreEqual(string.Format(Resources.Install_estimation_label_format, 3, 8), Row("estimate").Label, stage);
                Assert.AreEqual("pkg_日本語.zip", Row("estimate").Detail, stage);
                OperationProgressRow lr2Row = Row("lr2");
                Assert.AreEqual(Resources.Lr2_song_db_sync_status_running, lr2Row.Label, stage);
                Assert.AreEqual(Resources.Lr2_song_db_sync_stage_song_rows + " [2/5]", lr2Row.Detail, stage);
                Assert.AreEqual(ExpectedLr2Tooltip(), lr2Row.ToolTip, stage);
                Assert.AreEqual(lr2Row.Label + " " + lr2Row.Detail + "\n" + lr2Row.ToolTip, lr2Row.FullText, stage);
                foreach (PlaylistCase item in cases)
                {
                    OperationProgressRow row = Row(item.Key);
                    Assert.AreEqual(item.Label(), row.Label, stage + " " + item.Key + " label");
                    Assert.AreEqual(item.Detail(), row.Detail, stage + " " + item.Key + " detail");
                    Assert.AreEqual(row.Label + " " + row.Detail, row.FullText, stage + " " + item.Key + " tooltip");
                }
            }

            RowFacts[] factsBefore = CaptureFacts(hub);
            OperationProgressRow[] japanese = [.. hub.Rows];
            AssertCurrentLanguage("ja-first");
            (bool Active, bool Failed, bool Retryable, double Value, double Maximum) startupBefore =
                (hub.StartupProgress.IsOperationActive, hub.StartupProgress.IsFailed, hub.StartupProgress.IsRetryableFailure, hub.StartupProgress.Value, hub.StartupProgress.Maximum);

            hub.AttachLanguagePresentation();
            var rowsNotifications = new List<string>();
            hub.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(OperationProgressHubViewModel.Rows)) { rowsNotifications.Add(args.PropertyName!); } };
            foreach (string culture in new[] { "en-US", "ja-JP" })
            {
                rowsNotifications.Clear();
                ResourceService.Current.ChangeCulture(culture);
                Assert.AreEqual(1, rowsNotifications.Count, culture + " の言語変更でRows通知は一回です。");
                AssertCurrentLanguage(culture);
                CollectionAssert.AreEqual(factsBefore, CaptureFacts(hub), culture + " 行の事実は不変です。");
                Assert.AreEqual(startupBefore, (hub.StartupProgress.IsOperationActive, hub.StartupProgress.IsFailed, hub.StartupProgress.IsRetryableFailure, hub.StartupProgress.Value, hub.StartupProgress.Maximum));
                if (culture == "en-US")
                {
                    foreach (OperationProgressRow before in japanese)
                    {
                        OperationProgressRow now = hub.Rows.Single(row => row.Key == before.Key);
                        Assert.IsTrue(before.Label != now.Label || before.Detail != now.Detail, before.Key + " は英語で文言が変わります。");
                    }
                }
            }
            CollectionAssert.AreEqual(japanese, hub.Rows.ToArray(), "日本語へ戻すと初回と完全に一致します。");
            Assert.AreEqual(1, startup.PrepareOperation);
            Assert.AreEqual(1, startup.VersionSnapshot);
            Assert.AreEqual(0, startup.HideDelay);
            Assert.AreEqual(0, startup.InitializationCompleted);
        });
    }

    /// <summary>言語変更前に投入され変更後に適用される保留通知と、変更後の次の通常進捗が現在語になり、旧語へ戻らないことを確認します。</summary>
    [TestMethod]
    public void HubLanguage_QueuedAndNextNotificationsUseCurrentLanguage()
    {
        var startup = StartupHarness.Create();
        var hub = new OperationProgressHubViewModel(startup.Owner);
        RunWithJapanese(hub, _ =>
        {
            var playlistQueue = new Queue<Action>();
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
            hub.AttachPlaylistProgressSources(workspace, playlistQueue.Enqueue, () => false);
            startup.QueueDispatch = true;
            hub.AttachLanguagePresentation();

            var import = new PlaylistCase("w2_import", 0, s => { s.Kind = PlaylistSyncProgressKind.ExternalPlaylistImport; s.ImportPhase = PlaylistImportProgressPhase.RegisterPlaylists; },
                () => string.Empty, () => string.Empty);
            workspace.ReportPlaylistSyncProgress(import.CreateSnapshot(1));
            startup.Owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            Assert.AreEqual(0, hub.Rows.Count, "保留中は何も表示しません。");

            ResourceService.Current.ChangeCulture("en-US");
            startup.Drain();
            while (playlistQueue.TryDequeue(out Action? apply)) { apply(); }
            Assert.AreEqual(StartupLabel(hub), hub.Rows.Single(row => row.Key == "startup").Label);
            Assert.AreEqual(Resources.Statusbar_progress_startup, hub.StartupProgress.Label);
            OperationProgressRow queued = hub.Rows.Single(row => row.Key == import.Key);
            Assert.AreEqual(string.Format(Resources.Playlist_import_progress_label_format, 1, 3), queued.Label);
            Assert.AreEqual(Resources.Playlist_import_progress_phase_register_playlists + ": " + TableName, queued.Detail);

            // 次の通常進捗（件数+1、phase遷移）も現在語です。
            PlaylistCase next = import with { Configure = s => { s.Kind = PlaylistSyncProgressKind.ExternalPlaylistImport; s.ImportPhase = PlaylistImportProgressPhase.Finish; } };
            workspace.ReportPlaylistSyncProgress(next.CreateSnapshot(2));
            while (playlistQueue.TryDequeue(out Action? apply)) { apply(); }
            OperationProgressRow progressed = hub.Rows.Single(row => row.Key == import.Key);
            Assert.AreEqual(string.Format(Resources.Playlist_import_progress_label_format, 2, 3), progressed.Label);
            Assert.AreEqual(Resources.Playlist_import_progress_phase_finish + ": " + TableName, progressed.Detail);

            ResourceService.Current.ChangeCulture("ja-JP");
            workspace.ReportPlaylistSyncProgress(import.CreateSnapshot(3));
            while (playlistQueue.TryDequeue(out Action? apply)) { apply(); }
            progressed = hub.Rows.Single(row => row.Key == import.Key);
            Assert.AreEqual(string.Format(Resources.Playlist_import_progress_label_format, 3, 3), progressed.Label);
            Assert.AreEqual(Resources.Playlist_import_progress_phase_register_playlists + ": " + TableName, progressed.Detail);
            // 開始中のStartupProgressの反映も、日本語の現在語で適用されます。
            startup.Drain();
            Assert.AreEqual(Resources.Statusbar_progress_startup, hub.StartupProgress.Label);
        });
    }

    /// <summary>Detach後・closing中・非アクティブなStartupProgressでは、言語変更でHubの行通知とStartupProgressの通知を出さず、再Attach後は現在語になることを確認します。</summary>
    /// <remarks>
    /// hide後の段は、hide継続の表示反映をテストスレッドで一括して消化し終えた時点を基準にします。
    /// 言語変更が配送した表示も観測するため、言語変更の後にキューを消化してから表明します。
    /// </remarks>
    [TestMethod]
    public void HubLanguage_DetachClosingAndInactiveStartupDoNotPublish()
    {
        var startup = StartupHarness.Create();
        var hub = new OperationProgressHubViewModel(startup.Owner);
        RunWithJapanese(hub, _ =>
        {
            bool closing = false;
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
            hub.AttachPlaylistProgressSources(workspace, action => action(), () => closing);
            startup.Owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            var cases = new PlaylistCase("w4_import", 0, s => { s.Kind = PlaylistSyncProgressKind.ExternalPlaylistImport; s.ImportPhase = PlaylistImportProgressPhase.Finish; }, () => string.Empty, () => string.Empty);
            workspace.ReportPlaylistSyncProgress(cases.CreateSnapshot(1));
            var hubNotifications = new List<string>();
            var startupNotifications = new List<string>();
            hub.PropertyChanged += (_, args) => hubNotifications.Add(args.PropertyName!);
            hub.StartupProgress.PropertyChanged += (_, args) => startupNotifications.Add(args.PropertyName!);
            string japaneseStartup = hub.StartupProgress.Label;
            string japaneseImport = hub.Rows.Single(row => row.Key == cases.Key).Label;

            // Attach前は反映しません。
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(0, hubNotifications.Count);
            Assert.AreEqual(japaneseStartup, hub.StartupProgress.Label);
            ResourceService.Current.ChangeCulture("ja-JP");

            // Attach後はRows通知一回で反映し、DisplayCultureの通知には反応しません。
            hub.AttachLanguagePresentation();
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(1, hubNotifications.Count(name => name == nameof(OperationProgressHubViewModel.Rows)));
            Assert.AreNotEqual(japaneseStartup, hub.StartupProgress.Label);
            Assert.AreNotEqual(japaneseImport, hub.Rows.Single(row => row.Key == cases.Key).Label);
            string englishStartup = hub.StartupProgress.Label;
            string englishImport = hub.Rows.Single(row => row.Key == cases.Key).Label;

            // Detach後は何も出さず、表示値は旧値のままです。
            hub.DetachLanguagePresentation();
            hubNotifications.Clear(); startupNotifications.Clear();
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, hubNotifications.Count(name => name is nameof(OperationProgressHubViewModel.Rows) or nameof(OperationProgressHubViewModel.HasRows)));
            Assert.AreEqual(0, startupNotifications.Count);
            Assert.AreEqual(englishStartup, hub.StartupProgress.Label);

            // 再Attach後は現在語です。
            hub.AttachLanguagePresentation();
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(englishStartup, hub.StartupProgress.Label);
            hubNotifications.Clear(); startupNotifications.Clear();
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(japaneseStartup, hub.StartupProgress.Label);
            Assert.AreEqual(japaneseImport, hub.Rows.Single(row => row.Key == cases.Key).Label);
            Assert.AreEqual(1, hubNotifications.Count(name => name == nameof(OperationProgressHubViewModel.Rows)));

            // closing中は何も出しません。
            closing = true;
            hubNotifications.Clear(); startupNotifications.Clear();
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(0, hubNotifications.Count(name => name is nameof(OperationProgressHubViewModel.Rows) or nameof(OperationProgressHubViewModel.HasRows)));
            Assert.AreEqual(0, startupNotifications.Count);
            Assert.AreEqual(japaneseStartup, hub.StartupProgress.Label);
            closing = false;
            ResourceService.Current.ChangeCulture("ja-JP");

        });

        // 非アクティブ（開始前と、完了表示をhideした後）のStartupProgressはLabel・IsActiveの通知も行も出しません。
        var idle = StartupHarness.Create(blockHide: true);
        var idleHub = new OperationProgressHubViewModel(idle.Owner);
        RunWithJapanese(idleHub, _ =>
        {
            idleHub.AttachLanguagePresentation();
            var startupNotifications = new List<string>();
            idle.Owner.PropertyChanged += (_, args) => startupNotifications.Add(args.PropertyName!);
            ResourceService.Current.ChangeCulture("en-US");
            CollectionAssert.DoesNotContain(startupNotifications, nameof(StartupProgressWorkflowOwner.Label));
            CollectionAssert.DoesNotContain(startupNotifications, nameof(StartupProgressWorkflowOwner.IsActive));
            Assert.AreEqual(0, idleHub.Rows.Count);
            ResourceService.Current.ChangeCulture("ja-JP");

            // 完了表示を実際にhideさせた後の非アクティブでも同じです。
            long token = idle.Owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            idle.Owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
            idle.Owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
            idle.Owner.TryCompleteStartupProgressLibraryFileDiff(1);
            foreach (StartupProgressPhase phase in new[] { StartupProgressPhase.StartupReadyData, StartupProgressPhase.StartupReadyUi, StartupProgressPhase.StartupReadyOperable })
            {
                idle.Owner.MarkStartupProgressPhaseCompleted(phase, token);
            }
            foreach (StartupProgressPhase phase in new[] { StartupProgressPhase.PlaylistEntriesHydrationDone, StartupProgressPhase.ChartInfoHydrationDone, StartupProgressPhase.ScoreHydrationDone })
            {
                idle.Owner.SkipStartupProgressPhaseIfExpected(phase, "test", token);
            }
            idle.Owner.CompleteRequiredInitialization(token);
            idle.HideEntered.Task.GetAwaiter().GetResult();
            // 継続はゲートで止まっているため、ここで配送をキューへ切り替えてから解放します。
            idle.QueueDispatch = true;
            idle.HideGate!.TrySetResult();
            // IsActiveをfalseにするのは配送されたreflectだけで、ループ条件は配送1件の実行完了後に判定するため、falseになった時点でhideの反映はテストスレッド上で一括して終わっています。
            while (idle.Owner.IsActive) { idle.RunNextDispatch(); }
            Assert.IsFalse(idle.Owner.IsActive);
            Assert.AreEqual(0, idle.PendingDispatch.Count, "hide継続はreflectの後に表示を配送しません。");
            startupNotifications.Clear();
            ResourceService.Current.ChangeCulture("en-US");
            idle.Drain();
            CollectionAssert.DoesNotContain(startupNotifications, nameof(StartupProgressWorkflowOwner.Label));
            CollectionAssert.DoesNotContain(startupNotifications, nameof(StartupProgressWorkflowOwner.SubLabel));
            CollectionAssert.DoesNotContain(startupNotifications, nameof(StartupProgressWorkflowOwner.IsActive));
            Assert.AreEqual(0, idleHub.Rows.Count);
        });
    }

    /// <summary>StartupProgressの各状態で親行Labelが現在語になり、失敗の補足は登録フォルダ検査なら現在語の資源・一般例外なら原文のまま保たれ、完了待ちの言語変更が完了通知・hide予約・操作準備を増やさないことを確認します。</summary>
    [TestMethod]
    [DataRow("running")]
    [DataRow("completedHideWait")]
    [DataRow("failedPreflight")]
    [DataRow("failedGeneral")]
    [DataRow("failedGeneralEmpty")]
    public void StartupProgressLanguage_ReappliesEachStateWithoutCompletionOrHideSideEffects(string state)
    {
        const string generalMessage = "I/O error 日本語 mixed 原文";
        var startup = StartupHarness.Create(blockHide: true);
        var hub = new OperationProgressHubViewModel(startup.Owner);
        try
        {
            RunWithJapanese(hub, _ =>
            {
                hub.AttachLanguagePresentation();
                long token = startup.Owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
                switch (state)
                {
                    case "completedHideWait":
                        startup.Owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
                        startup.Owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
                        startup.Owner.TryCompleteStartupProgressLibraryFileDiff(1);
                        foreach (StartupProgressPhase phase in new[] { StartupProgressPhase.StartupReadyData, StartupProgressPhase.StartupReadyUi, StartupProgressPhase.StartupReadyOperable })
                        {
                            startup.Owner.MarkStartupProgressPhaseCompleted(phase, token);
                        }
                        foreach (StartupProgressPhase phase in new[] { StartupProgressPhase.PlaylistEntriesHydrationDone, StartupProgressPhase.ChartInfoHydrationDone, StartupProgressPhase.ScoreHydrationDone })
                        {
                            startup.Owner.SkipStartupProgressPhaseIfExpected(phase, "test", token);
                        }
                        startup.Owner.CompleteRequiredInitialization(token);
                        break;
                    case "failedPreflight":
                        startup.Owner.FailStartupProgressOperation("preflight message that must not be shown", isDirectoryPreflightFailure: true);
                        break;
                    case "failedGeneral":
                        startup.Owner.FailStartupProgressOperation(generalMessage);
                        break;
                    case "failedGeneralEmpty":
                        startup.Owner.FailStartupProgressOperation(string.Empty);
                        break;
                }
                string running = Resources.Statusbar_progress_startup;
                string ExpectedLabel() => state switch
                {
                    "completedHideWait" => string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_startup),
                    "failedPreflight" or "failedGeneral" or "failedGeneralEmpty" => string.Format(Resources.Statusbar_progress_operation_failed_format, Resources.Statusbar_progress_startup),
                    _ => Resources.Statusbar_progress_startup
                };
                string ExpectedSubLabel() => state switch
                {
                    "failedPreflight" => Resources.LibraryDirectoryPreflightProgressFailure,
                    "failedGeneral" => generalMessage,
                    _ => string.Empty
                };
                if (state == "completedHideWait") { startup.HideEntered.Task.GetAwaiter().GetResult(); }
                (int Completed, int Prepare, int Version, int Hide) counts = (startup.InitializationCompleted, startup.PrepareOperation, startup.VersionSnapshot, startup.HideDelay);
                Assert.AreEqual(state == "completedHideWait" ? (true, 1, 1, 1) : (false, 1, 1, 0), (counts.Completed > 0, counts.Prepare, counts.Version, counts.Hide));
                (bool Active, bool Failed, bool Retryable, double Value, double Maximum) Facts() =>
                    (startup.Owner.IsOperationActive, startup.Owner.IsFailed, startup.Owner.IsRetryableFailure, startup.Owner.Value, startup.Owner.Maximum);
                (bool Active, bool Failed, bool Retryable, double Value, double Maximum) factsBefore = Facts();
                long tokenBefore = startup.Owner.GetActiveStartupProgressOperationToken();
                Assert.AreEqual(ExpectedLabel(), startup.Owner.Label);
                Assert.AreEqual(ExpectedSubLabel(), startup.Owner.SubLabel);
                string japaneseLabel = startup.Owner.Label;
                Assert.IsFalse(string.IsNullOrEmpty(running));

                foreach (string culture in new[] { "en-US", "ja-JP" })
                {
                    ResourceService.Current.ChangeCulture(culture);
                    Assert.AreEqual(ExpectedLabel(), startup.Owner.Label, culture);
                    Assert.AreEqual(ExpectedSubLabel(), startup.Owner.SubLabel, culture);
                    Assert.AreEqual(factsBefore, Facts(), culture);
                    Assert.AreEqual(tokenBefore, startup.Owner.GetActiveStartupProgressOperationToken(), culture);
                    Assert.AreEqual(counts, (startup.InitializationCompleted, startup.PrepareOperation, startup.VersionSnapshot, startup.HideDelay), culture);
                    if (culture == "en-US")
                    {
                        Assert.AreNotEqual(japaneseLabel, startup.Owner.Label);
                    }
                }
                Assert.AreEqual(japaneseLabel, startup.Owner.Label);
                if (state == "failedGeneral") { Assert.AreEqual(generalMessage, hub.Rows.Single(row => row.Key == "startup").Detail); }
                if (state == "failedGeneralEmpty") { Assert.AreEqual(string.Empty, hub.Rows.Single(row => row.Key == "startup").Detail); }

                if (state == "completedHideWait")
                {
                    // 言語変更はhideを延長・追加・取消せず、gate解放で一回だけ非表示になります。
                    ResourceService.Current.ChangeCulture("en-US");
                    Assert.IsTrue(startup.Owner.IsActive);
                    // hide継続の表示反映をテストスレッドで一括して消化し終えた時点で表明します。
                    startup.QueueDispatch = true;
                    startup.HideGate!.TrySetResult();
                    while (startup.Owner.IsActive) { startup.RunNextDispatch(); }
                    Assert.AreEqual(0, startup.PendingDispatch.Count, "hide継続はreflectの後に表示を配送しません。");
                    Assert.AreEqual(1, startup.HideDelay);
                    Assert.IsFalse(startup.Owner.IsActive);
                    Assert.AreEqual(0, hub.Rows.Count);
                }
            });
        }
        finally
        {
            startup.HideGate?.TrySetResult();
        }
    }

    /// <summary>実workspaceのURL取得行が、取得中・取消後・取消後の言語変更で現在語の書式と保持した件数・URIを保ち、取消前後でActionだけが変わり、終端で行が消えることを確認します。</summary>
    [TestMethod]
    public async Task UrlAcquisitionRow_LanguageChangeAndCancelKeepCurrentLanguageAndFacts()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(migrationWorkspace.RootDirectory, nameof(OperationProgressHubLanguageTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var gateway = new LanguageBlockingGateway(temporaryDirectory);
        var startup = StartupHarness.Create();
        var hub = new OperationProgressHubViewModel(startup.Owner);
        Task? acquisition = null;
        CultureInfo? previous = Resources.Culture;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            using PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedStore =
                PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore(new ApplicationDatabaseScope(), modelUiScheduler: new TestUiScheduler(() => null));
            var dialogs = new RecordingPlaylistUrlDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(
                action => action(), new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }), dialogs, playlistStoreProvider: () => ownedStore.Store);
            hub.AttachPlaylistProgressSources(workspace, action => action(), () => false);
            hub.AttachLanguagePresentation();
            var uri = new Uri("https://example.invalid/language.zip");

            acquisition = workspace.RunPlaylistUrlBatchAsync([uri], isDiffUrl: false);
            await TestUiDispatcherHost.AwaitNotificationAsync(gateway.ReadStarted.Task, acquisition, "url-language.read-started");

            void AssertRow(OperationProgressAction action)
            {
                OperationProgressRow row = hub.Rows.Single(item => item.Key == "url");
                Assert.AreEqual(string.Format(Resources.Playlist_url_download_progress_label_format, 0, 1), row.Label);
                Assert.AreEqual(uri.ToString(), row.Detail);
                Assert.AreEqual(0d, row.Value);
                Assert.AreEqual(1d, row.Maximum);
                Assert.AreEqual(action, row.Action);
            }
            AssertRow(OperationProgressAction.CancelUrlDownload);
            string japanese = hub.Rows.Single(item => item.Key == "url").Label;
            ResourceService.Current.ChangeCulture("en-US");
            AssertRow(OperationProgressAction.CancelUrlDownload);
            Assert.AreNotEqual(japanese, hub.Rows.Single(item => item.Key == "url").Label);
            workspace.CancelPlaylistUrlDownload();
            AssertRow(OperationProgressAction.None);
            ResourceService.Current.ChangeCulture("ja-JP");
            AssertRow(OperationProgressAction.None);
            Assert.AreEqual(japanese, hub.Rows.Single(item => item.Key == "url").Label);
            Assert.AreEqual(1, gateway.ReadCount);

            gateway.Response.TrySetResult(new AppHttpResponse(uri, new MemoryStream([1, 2, 3], writable: false)));
            await acquisition;
            Assert.IsFalse(hub.Rows.Any(item => item.Key == "url"));
            Assert.AreEqual(1, gateway.ReadCount);
        }
        finally
        {
            gateway.Response.TrySetCanceled();
            try
            {
                if (acquisition != null)
                {
                    try { await acquisition; } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                hub.DetachLanguagePresentation();
                ResourceService.Current.ChangeCulture(previous?.Name ?? "en-US");
                Resources.Culture = previous;
                if (Directory.Exists(temporaryDirectory)) { Directory.Delete(temporaryDirectory, recursive: true); }
            }
        }
    }

    /// <summary>読取り開始と応答を試験側で制御する取得口です。</summary>
    private sealed class LanguageBlockingGateway(string temporaryDirectory) : IPlaylistUrlDownloadGateway
    {
        private int readCount;

        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<AppHttpResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadCount => Volatile.Read(ref readCount);

        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref readCount);
            ReadStarted.TrySetResult();
            return Response.Task;
        }

        public string GetTemporaryDirectory() => temporaryDirectory;

        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share) => new(path, mode, access, share);

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path) => File.Delete(path);
    }
}
