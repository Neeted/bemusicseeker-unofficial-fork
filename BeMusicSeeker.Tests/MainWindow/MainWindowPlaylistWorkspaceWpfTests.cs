using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlaylistWorkspaceWpfTests
{
    private const string NativeModalFixturePlaylistName = "Native modal fixture";

    [TestMethod]
    public void ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var displayed = new List<(Window Owner, UiMessageRequest Request)>();
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest,
                messagePresenter: (owner, request) =>
                {
                    displayed.Add((owner, request));
                    return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                });
            var started = new List<Task>();
            Exception? primaryFailure = null;
            try
            {
                string header = Path.Combine(fixture.Root, "detail-header.json");
                string data = Path.Combine(fixture.Root, "detail-data.json");
                File.WriteAllText(header, "{\"name\":\"Detail reload\",\"symbol\":\"E\",\"data_url\":\"./detail-data.json\",\"level_order\":[1]}", new UTF8Encoding(true));
                File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"level\":\"1\",\"url\":\"https://before.example/song\"}]", new UTF8Encoding(true));
                Task<BMSTable> load = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
                started.Add(load);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(load, "detail-current-fixture-load");
                BMSTable original = load.GetAwaiter().GetResult();
                original.playlist_id = fixture.Table.playlist_id;
                original.EnableExternalSync();
                fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
                fixture.Playlist.CommitBMSTableWithEntriesToDB(original);
                BMSTableEntry oldEntry = original.entries.Single();
                PlaylistDetailRow oldRow = new PlaylistDetailSourceRow(oldEntry, resolvedChart: null).CreateViewRow();
                File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"level\":\"9\",\"url\":\"https://current.example/song\"}]", new UTF8Encoding(true));
                PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
                Task resync = workspace.ResyncPlaylistsAsync([original]);
                started.Add(resync);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(resync, "detail-resync-actual-terminal");
                BMSTable current = fixture.Playlist.BMSTables.Single();
                BMSTableEntry currentEntry = current.entries.Single(entry => !entry.is_removed);
                Assert.AreNotSame(original, current);
                Assert.AreEqual(original.playlist_id, current.playlist_id);
                Assert.AreEqual(oldEntry.md5, currentEntry.md5);
                Assert.AreEqual(9d, currentEntry.level);
                Assert.AreEqual("https://current.example/song", currentEntry.url);
                Assert.IsFalse(MainWindowViewModelTestFactory.GetComposition(fixture.ViewModel).PlaylistOperationAdmission.IsActive);
                displayed.Clear();
                string expectedMemo = currentEntry.memo;
                PlaylistWorkspaceMutationRejectedEventArgs? rejection = null;
                workspace.MutationRejected += (_, request) => rejection = request;
                var oldContext = new MainChartListCellEditContext(oldRow, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                Task rejected = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(oldContext, "old edit C", commit: true));
                started.Add(rejected);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(rejected, "detail-old-row-rejection-terminal");
                Assert.IsNotNull(rejection);
                Assert.IsTrue(rejection.IsStale);
                Assert.IsFalse(rejection.IsBusy);
                Assert.AreEqual(1, displayed.Count);
                Assert.AreSame(fixture.Window, displayed.Single().Owner);
                Assert.IsFalse(string.IsNullOrWhiteSpace(displayed.Single().Request.MessageBoxText));
                Assert.AreEqual(MessageBoxImage.Hand, displayed.Single().Request.Icon);
                Assert.AreEqual(9d, currentEntry.level);
                Assert.AreEqual("https://current.example/song", currentEntry.url);
                Assert.AreEqual(expectedMemo, currentEntry.memo);
                using (LR2SongDBExtended read = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "song.db")).OpenSongDbReadOnly())
                {
                    BMSTableEntry persisted = read.Table<BMSTableEntry>().Single(entry => entry.playlist_id == current.playlist_id && !entry.is_removed);
                    Assert.AreEqual(9d, persisted.level);
                    Assert.AreEqual("https://current.example/song", persisted.url);
                    Assert.AreEqual(expectedMemo, persisted.memo);
                }
                PlaylistDetailRow currentRow = new PlaylistDetailSourceRow(currentEntry, resolvedChart: null).CreateViewRow();
                var currentContext = new MainChartListCellEditContext(currentRow, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                Task fresh = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(currentContext, "fresh edit D", commit: true));
                started.Add(fresh);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fresh, "detail-current-row-save-terminal");
                Assert.AreEqual(1, displayed.Count);
                Assert.AreEqual("fresh edit D", currentEntry.memo);
                Assert.AreEqual("fresh edit D", currentRow.memo);
                Assert.IsFalse(MainWindowViewModelTestFactory.GetComposition(fixture.ViewModel).PlaylistOperationAdmission.IsActive);
                using (LR2SongDBExtended read = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "song.db")).OpenSongDbReadOnly())
                {
                    BMSTableEntry persisted = read.Table<BMSTableEntry>().Single(entry => entry.playlist_id == current.playlist_id && !entry.is_removed);
                    Assert.AreEqual(9d, persisted.level);
                    Assert.AreEqual("https://current.example/song", persisted.url);
                    Assert.AreEqual("fresh edit D", persisted.memo);
                }
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(started), "detail-edit-all-started-terminal");
                }
                catch when (primaryFailure != null) { }
                finally { fixture.Close(); }
            }
        });
    }

    [DataTestMethod]
    [DataRow("All", false)]
    [DataRow("All", true)]
    [DataRow("Single", false)]
    [DataRow("Single", true)]
    [DataRow("Property", false)]
    [DataRow("Property", true)]
    [TestCategory("Playlist")]
    public void RecommendationReload_ThreeEntriesPublishAutomaticNamesAndPresentOnlyNumericSkillChanges(
        string entry, bool sameSkill)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var displayed = new List<(Window Owner, UiMessageRequest Request)>();
            TaskCompletionSource<object?> skillDisplayed = NewCompletion();
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest,
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")),
                configureSettings: settings => settings.ShowRecommUpdatedMsg = true,
                messagePresenter: (owner, request) =>
                {
                    displayed.Add((owner, request));
                    if (request.Caption == Resources.Recommend_SkillUpdatedTitle && request.Icon == MessageBoxImage.Information)
                    {
                        skillDisplayed.TrySetResult(null);
                    }
                    return new ThemedMessageBoxResponse(
                        request.Button == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK,
                        closedWithoutSelection: false);
                });
            PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
            TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> deferredCompletion = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
            EventHandler<PlaylistExternalSyncCompletionEventArgs> syncCompleted = (_, request) =>
            {
                if (request.FromReloadTables) { deferredCompletion.TrySetResult(request); }
            };
            workspace.PlaylistExternalSyncCompleted += syncCompleted;
            Task? operation = null;
            PlaylistPropertyDialogViewModel? property = null;
            Exception? primaryFailure = null;
            try
            {
                Task<BMSTable> initialLoad = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialLoad, "recommendation-fixture-load");
                BMSTable original = initialLoad.GetAwaiter().GetResult();
                original.playlist_id = fixture.Table.playlist_id;
                original.name = "Manual name ★999";
                original.org_name = sameSkill ? "旧方針 ★6.6" : "旧方針 ★3.87";
                original.header_sha256 = null;
                original.data_sha256 = null;
                original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
                original.is_bmt_output = false;
                if (entry == "Property") { original.DisableExternalSync(); } else { original.EnableExternalSync(); }
                fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
                fixture.Playlist.CommitBMSTableWithEntriesToDB(original);
                displayed.Clear();
                operation = ApplyEntryAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "recommendation-entry-terminal");

                double skill = ReadWalkureCase("standard")["rating"]!.Value<double>("playerStarRating");
                string expectedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                    skill.ToString("F2", CultureInfo.InvariantCulture));
                BMSTable active = fixture.Playlist.BMSTables.Single();
                Assert.AreEqual(expectedName, active.name);
                Assert.AreEqual(expectedName, active.org_name);
                Assert.AreEqual(original.last_update, active.last_update);
                var repository = new PlaylistPersistenceRepository(Path.Combine(fixture.Root, "song.db"));
                BMSTable persisted = repository.LoadPlaylistHeaders().Single();
                Assert.AreEqual(expectedName, persisted.name);
                Assert.AreEqual(expectedName, persisted.org_name);
                Assert.AreEqual(original.last_update, persisted.last_update);
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));

                // 実際に接続されたツリーのコンテナーと表示名を観測する。
                var playlistRoot = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                MaterializeTreeItems(playlistRoot);
                var tableItem = playlistRoot.ItemContainerGenerator.ContainerFromItem(active) as TreeViewItem;
                Assert.IsNotNull(tableItem);
                Assert.IsTrue(FindDescendants<TextBlock>(tableItem!).Any(block => block.Text == expectedName));
                (Window Owner, UiMessageRequest Request)[] skillMessages = displayed.Where(call => call.Request.Caption == Resources.Recommend_SkillUpdatedTitle
                    && call.Request.Icon == MessageBoxImage.Information).ToArray();
                Assert.AreEqual(sameSkill ? 0 : 1, skillMessages.Length);
                if (!sameSkill)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(skillDisplayed.Task, "recommendation-message-presenter");
                    Assert.AreSame(fixture.Window, skillMessages[0].Owner);
                    Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage, skill.ToString("F2"),
                        (skill - 3.87).ToString(" (+#0.00); (-#0.00);")), skillMessages[0].Request.MessageBoxText);
                }

                async Task ApplyEntryAsync()
                {
                    switch (entry)
                    {
                        case "All":
                            await fixture.ViewModel.ReloadTablesAsync();
                            // 全体入口の完了後にも動く遅延同期の終端まで待つ。
                            PlaylistExternalSyncCompletionEventArgs completed = await deferredCompletion.Task;
                            Assert.IsTrue(completed.Succeeded);
                            Assert.IsFalse(completed.WasSkipped);
                            break;
                        case "Single":
                            await workspace.ResyncPlaylistTableAsync(original);
                            break;
                        case "Property":
                            property = await workspace.OpenPropertyDialogAsync(original);
                            Assert.IsNotNull(property);
                            property!.is_external_sync = true;
                            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await property.SaveAndApplyAsync());
                            break;
                        default: throw new AssertFailedException("Unknown entry.");
                    }
                }
            }
            catch (Exception exception) { primaryFailure = exception; throw; }
            finally
            {
                try
                {
                    if (operation != null)
                    {
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "recommendation-entry-drain"); }
                        catch when (primaryFailure != null) { }
                    }
                    if (property != null)
                    {
                        workspace.ClosePropertyDialog(property);
                        property.Dispose();
                    }
                    workspace.PlaylistExternalSyncCompleted -= syncCompleted;
                    fixture.Close();
                }
                catch when (primaryFailure != null) { }
            }
        });
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("busy")]
    [DataRow("database_failure")]
    [DataRow("output_failure")]
    [DataRow("notification_failure")]
    public void MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup(string bulkOutcome)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            bool captureOutput = false;
            string outputDirectory = string.Empty;
            using var releaseOutput = new ManualResetEventSlim();
            var outputEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new List<(Window Owner, UiMessageRequest Request)>();
            var notificationFailure = new IOException("bulk output failure notification failed");
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest,
                messagePresenter: (owner, request) =>
                {
                    displayed.Add((owner, request));
                    if (bulkOutcome == "notification_failure" && request.Icon == MessageBoxImage.Hand && request.Owner is PlaylistSummaryBulkEditDialog) { throw notificationFailure; }
                    return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                },
                bmtOptionsProvider: () =>
                {
                    if (!captureOutput) { return new BeatorajaBmtOptionsSnapshot(); }
                    outputEntered.TrySetResult();
                    releaseOutput.Wait();
                    return new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = outputDirectory };
                });
            IDisposable? busyLease = null;
            FileStream? outputBlocker = null;
            Task? apply = null;
            try
            {
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                ModalObservation<PlaylistPropertyDialog> first = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-open",
                    (dialog, _) =>
                    {
                        FrameworkElement navigation = (FrameworkElement)dialog.FindName("propertyNavigation")
                            ?? throw new AssertFailedException("Playlist property navigation was not materialized.");
                        FrameworkElement contentHost = (FrameworkElement)dialog.FindName("propertyContent")
                            ?? throw new AssertFailedException("Playlist property content host was not materialized.");
                        PropertyNavigationObservation navigationState = ObservePropertyNavigation(navigation, contentHost);
                        AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.General);
                        Assert.AreSame(
                            navigationState.ItemsByCategory[PropertyNavigationCategory.General],
                            navigationState.InitiallySelectedItem,
                            "The property dialog must capture its initial General provider before category navigation.");
                        Assert.AreEqual(
                            PropertyNavigationCategory.General,
                            IdentifyVisiblePropertyCategory(contentHost));
                        navigationState.ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.Folder);
                        Assert.AreEqual(PropertyNavigationCategory.Folder, IdentifyVisiblePropertyCategory(contentHost));
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                    });
                Assert.AreSame(fixture.Window, first.Owner);
                Assert.IsFalse(first.OwnerEnabled);
                Assert.IsInstanceOfType(first.DataContext, typeof(PlaylistPropertyDialogViewModel));
                Assert.AreEqual(1, first.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                Assert.AreSame(fixture.Table, row.TableRef);
                fixture.ModalPreparation.AssertLatest(first.Window, expectedCount: 1);

                ModalObservation<PlaylistPropertyDialog> reopened = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-reopen",
                    (dialog, _) =>
                    {
                        FrameworkElement reopenedNavigation = (FrameworkElement)dialog.FindName("propertyNavigation")
                            ?? throw new AssertFailedException("Reopened playlist property navigation was not materialized.");
                        FrameworkElement reopenedContent = (FrameworkElement)dialog.FindName("propertyContent")
                            ?? throw new AssertFailedException("Reopened playlist property content host was not materialized.");
                        PropertyNavigationObservation reopenedNavigationState = ObservePropertyNavigation(reopenedNavigation, reopenedContent);
                        AssertSelectedPropertyCategory(
                            reopenedNavigationState,
                            PropertyNavigationCategory.General,
                            "A reopened property dialog must select General without restoring navigation state.");
                        Assert.AreSame(
                            reopenedNavigationState.ItemsByCategory[PropertyNavigationCategory.General],
                            reopenedNavigationState.InitiallySelectedItem,
                            "A reopened property dialog must capture its newly selected General provider before navigation mutations.");
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                    });
                Assert.AreSame(fixture.Window, reopened.Owner);
                Assert.IsFalse(reopened.OwnerEnabled);
                Assert.AreNotSame(first.DataContext, reopened.DataContext);
                Assert.AreEqual(1, reopened.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                fixture.ModalPreparation.AssertLatest(reopened.Window, expectedCount: 2);

                outputDirectory = Path.Combine(fixture.Root, "bulk-table");
                Directory.CreateDirectory(outputDirectory);
                string manifest = Path.Combine(outputDirectory, BmtTableExportService.ManifestFileName);
                File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
                fixture.Table.Output_dir = "BulkNative";
                fixture.Table.entries = [new BMSTableEntry { playlist_id = fixture.Table.playlist_id, md5 = new string('a', 32), title = "A", folder = "1" }];
                fixture.Playlist.CommitBMSTableWithEntriesToDB(fixture.Table);
                if (bulkOutcome == "busy") { Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out busyLease)); }
                if (bulkOutcome is "output_failure" or "notification_failure") { outputBlocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None); }
                if (bulkOutcome == "database_failure")
                {
                    using var database = new LR2SongDBExtended(Path.Combine(fixture.Root, "song.db"));
                    database.Execute("CREATE TRIGGER fail_bulk_save BEFORE INSERT ON playlist BEGIN SELECT RAISE(FAIL, 'forced bulk DB failure'); END;");
                }
                captureOutput = bulkOutcome is "success" or "output_failure" or "notification_failure";
                ModalObservation<PlaylistSummaryBulkEditDialog> bulk = OpenBulkDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-open",
                    (dialog, observation) =>
                    {
                        var draft = (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
                        draft.BmtOutputOption = draft.OnOption;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
                        apply = dialog.WaitForApplyCompletionAsync();
                        if (captureOutput)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                TestUiDispatcherHost.AwaitNotificationAsync(outputEntered.Task, apply, "bulk-required-output"), "bulk-output-arrival");
                            Assert.IsFalse(apply.IsCompleted);
                            Assert.IsFalse(dialog.IsEnabled);
                            Assert.AreSame(draft.OnOption, draft.BmtOutputOption, "実出力前にはdraftをresetしません。");
                            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
                            Assert.AreEqual(0, displayed.Count);
                        }
                        releaseOutput.Set();
                        if (bulkOutcome == "notification_failure")
                        {
                            InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() =>
                                TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-compiled-apply-terminal"));
                            Assert.AreSame(notificationFailure, failure.InnerException);
                            Assert.IsTrue(apply.IsFaulted);
                        }
                        else { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-compiled-apply-terminal"); }
                        Assert.IsTrue(dialog.IsEnabled);
                        Assert.AreSame(bulkOutcome == "success" ? draft.NoChangeOption : draft.OnOption, draft.BmtOutputOption);
                        bool saved = bulkOutcome is "success" or "output_failure" or "notification_failure";
                        Assert.AreEqual(saved, fixture.Table.is_bmt_output);
                        using (var database = new LR2SongDBExtended(Path.Combine(fixture.Root, "song.db")))
                        {
                            Assert.AreEqual(saved ? 1L : 0L, database.ExecuteScalar<long>(
                                "SELECT COUNT(1) FROM playlist WHERE playlist_id = ? AND is_bmt_output = 1;", fixture.Table.playlist_id));
                            if (bulkOutcome == "database_failure")
                            {
                                database.Execute("DROP TRIGGER fail_bulk_save;");
                            }
                        }
                        if (bulkOutcome == "database_failure")
                        {
                            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
                            apply = dialog.WaitForApplyCompletionAsync();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-same-draft-after-db-failure");
                            Assert.AreSame(draft.NoChangeOption, draft.BmtOutputOption);
                            Assert.IsTrue(fixture.Table.is_bmt_output);
                            using var database = new LR2SongDBExtended(Path.Combine(fixture.Root, "song.db"));
                            Assert.AreEqual(1L, database.ExecuteScalar<long>(
                                "SELECT COUNT(1) FROM playlist WHERE playlist_id = ? AND is_bmt_output = 1;", fixture.Table.playlist_id));
                        }
                        observation.VisibleAfterApply = dialog.IsVisible;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryClose"));
                    });
                Assert.AreSame(fixture.Window, bulk.Owner);
                Assert.IsFalse(bulk.OwnerEnabled);
                Assert.IsInstanceOfType(
                    bulk.DataContext,
                    typeof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel));
                Assert.IsTrue(bulk.VisibleAfterApply);
                Assert.AreEqual(bulkOutcome != "busy", fixture.Table.is_bmt_output);
                var bulkFailures = displayed.Where(notification => notification.Request.Icon == MessageBoxImage.Hand).ToList();
                Assert.AreEqual(bulkOutcome == "success" ? 0 : 1, bulkFailures.Count);
                if (bulkFailures.Count != 0)
                {
                    Assert.AreSame(bulk.Window, bulkFailures.Single().Owner);
                    Assert.AreSame(bulk.Window, bulkFailures.Single().Request.Owner);
                    Assert.AreEqual(MessageBoxImage.Hand, bulkFailures.Single().Request.Icon);
                    StringAssert.Contains(bulkFailures.Single().Request.MessageBoxText, Resources.Msg_error_unexpected);
                }
                var outputWarnings = displayed.Where(notification => notification.Request.Icon == MessageBoxImage.Exclamation).ToList();
                Assert.AreEqual(bulkOutcome is "output_failure" or "notification_failure" ? 1 : 0, outputWarnings.Count);
                if (outputWarnings.Count != 0)
                {
                    Assert.AreSame(bulk.Window, outputWarnings.Single().Owner, "実coordinatorは現在のmodalを通知ownerに解決します。");
                    Assert.AreSame(fixture.Window, outputWarnings.Single().Request.Owner);
                }
                Assert.AreEqual(1, bulk.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActiveSummaryBulkEditDialog);
                fixture.ModalPreparation.AssertLatest(bulk.Window, expectedCount: 3);
            }
            finally
            {
                releaseOutput.Set();
                if (apply != null)
                {
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-compiled-finally"); }
                    catch (InvalidOperationException failure) when (bulkOutcome == "notification_failure" && ReferenceEquals(failure.InnerException, notificationFailure)) { }
                }
                busyLease?.Dispose();
                outputBlocker?.Dispose();
                fixture.Close();
            }
        });
    }

    [TestMethod]
    public void PlaylistPropertyDialog_NativeCloseRequestClosesAfterSynchronousReset()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest);
            try
            {
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                int nativeCloseRequestCount = 0;
                bool closedAfterSingleNativeCloseRequest = false;
                bool testOwnedRetryUsed = false;
                string? nameAtClosed = null;
                ModalObservation<PlaylistPropertyDialog> observation = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-native-close",
                    (dialog, _) =>
                    {
                        const string draftPlaylistName = "Native modal draft";
                        var draft =
                            (PlaylistPropertyDialogViewModel)dialog.DataContext;
                        draft.name = draftPlaylistName;
                        Assert.AreEqual(draftPlaylistName, draft.name);

                        TaskCompletionSource<object?> closed = NewCompletion();
                        EventHandler closedHandler = (_, _) =>
                        {
                            if (dialog.DataContext is PlaylistPropertyDialogViewModel closedDraft)
                            {
                                nameAtClosed = closedDraft.name;
                            }
                            closed.TrySetResult(null);
                        };
                        dialog.Closed += closedHandler;
                        try
                        {
                            nativeCloseRequestCount++;
                            dialog.Close();
                            try
                            {
                                TestUiDispatcherHost.AwaitPresentationOnDispatcher(
                                    closed.Task,
                                    "MainWindowPlaylistWorkspaceWpfTests.property-native-close.closed");
                                closedAfterSingleNativeCloseRequest = true;
                            }
                            catch (TimeoutException)
                            {
                                // Preserve the base red evidence before using one test-owned
                                // second close to release the nested modal route.
                                testOwnedRetryUsed = dialog.IsVisible;
                                if (dialog.IsVisible)
                                {
                                    dialog.Close();
                                }
                            }
                        }
                        finally
                        {
                            dialog.Closed -= closedHandler;
                        }
                    });

                Assert.AreEqual(
                    1,
                    nativeCloseRequestCount,
                    "The title-bar close scenario must issue exactly one native close request.");
                Assert.IsTrue(
                    closedAfterSingleNativeCloseRequest,
                    $"One native close request must close after reset completion. Test-owned retry used: {testOwnedRetryUsed}.");
                Assert.AreEqual(
                    NativeModalFixturePlaylistName,
                    nameAtClosed,
                    "Reset must restore the fixture's original playlist name before the native window raises Closed.");
                Assert.IsTrue(
                    observation.Window.DialogResult.HasValue && !observation.Window.DialogResult.Value,
                    "A reset-backed native close must return the modal coordinator's cancelled/false result.");
                Assert.IsTrue(fixture.Window.IsEnabled, "The owner must be re-enabled after the modal route returns.");
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                Assert.IsNull(observation.Window.DataContext);
                Assert.AreEqual(1, observation.DataContextDetachCount);
            }
            finally
            {
                fixture.Close();
            }
        });
    }

    [TestMethod]
    public void MainWindowClose_WaitsForBulkApplyBeforeTerminalShutdownAndCleansOwnedState()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest);
            bool applyWriteBlockerHeld = false;
            Task? acceptedApplyCompletion = null;
            try
            {
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                void ReleaseApplyWriteBlocker()
                {
                    if (!applyWriteBlockerHeld)
                    {
                        return;
                    }
                    applyWriteBlockerHeld = false;
                    fixture.Playlist.FreeWriterLockBMSTables();
                }

                ModalObservation<PlaylistSummaryBulkEditDialog> shutdown = OpenBulkDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown",
                    (dialog, observation) =>
                    {
                        var draft =
                            (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
                        draft.BmtOutputOption = draft.OnOption;
                        fixture.Playlist.AcquireWriterLockBMSTables();
                        applyWriteBlockerHeld = true;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
                        Task applyCompletion = dialog.WaitForApplyCompletionAsync();
                        acceptedApplyCompletion = applyCompletion;
                        fixture.Lifetime.RequestShutdownAction = () =>
                            observation.ApplyCompletedAtTerminalRequest = applyCompletion.IsCompleted;
                        observation.ApplyWasPending = !dialog.IsEnabled;

                        void ReleaseApplyAfterOwnerShutdownClose(object? sender, EventArgs args)
                        {
                            dialog.Closed -= ReleaseApplyAfterOwnerShutdownClose;
                            Assert.IsTrue(
                                dialog.IsOwnerShutdownClose,
                                "Bulk dialog must close through the owner-shutdown path before the apply lock is released.");
                            observation.ShutdownCountBeforeLockRelease = fixture.Lifetime.RequestShutdownCount;
                            ReleaseApplyWriteBlocker();
                        }
                        dialog.Closed += ReleaseApplyAfterOwnerShutdownClose;
                        fixture.Window.Close();
                        observation.OwnerCloseWasCanceled = fixture.Window.IsVisible;
                        observation.ShutdownCountBeforeApplyCompletion = fixture.Lifetime.RequestShutdownCount;
                    });

                fixture.ModalPreparation.AssertLatest(shutdown.Window, expectedCount: 1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(fixture.Lifetime.ShutdownRequested.Task, fixture.Window.CloseCompletion, "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown.terminal-request"), "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown.terminal-request");
                Assert.AreEqual(
                    1,
                    fixture.Lifetime.RequestShutdownCount,
                    $"shutdown={fixture.Lifetime.RequestShutdownCount}, applyPending={shutdown.ApplyWasPending}, ownerCloseCanceled={shutdown.OwnerCloseWasCanceled}, beforeApply={shutdown.ShutdownCountBeforeApplyCompletion}, beforeRelease={shutdown.ShutdownCountBeforeLockRelease}, visible={fixture.Window.IsVisible}");
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActiveSummaryBulkEditDialog);
                Assert.IsNull(shutdown.Window.DataContext);
                Assert.AreEqual(1, shutdown.DataContextDetachCount);
                Assert.IsTrue(shutdown.ApplyWasPending);
                Assert.IsTrue(shutdown.OwnerCloseWasCanceled);
                Assert.AreEqual(0, shutdown.ShutdownCountBeforeApplyCompletion);
                Assert.AreEqual(0, shutdown.ShutdownCountBeforeLockRelease);
                Assert.IsTrue(shutdown.ApplyCompletedAtTerminalRequest);
                fixture.Window.Close();
                Assert.IsFalse(fixture.Window.IsVisible);

                // Repeated owner shutdown signals remain idempotent after terminal authorization.
                fixture.ViewModel.ShellShutdownWorkflow.RequestTerminalApplicationShutdown();
                Assert.AreEqual(1, fixture.Lifetime.RequestShutdownCount);
            }
            finally
            {
                if (applyWriteBlockerHeld)
                {
                    applyWriteBlockerHeld = false;
                    fixture.Playlist.FreeWriterLockBMSTables();
                }
                try
                {
                    if (acceptedApplyCompletion != null)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(acceptedApplyCompletion, "bulk-shutdown-finally-actual-apply");
                    }
                }
                finally { fixture.Close(); }
            }
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MainWindowPlaylistSync_UsesHttpAndUiTerminalBeforeAcceptingAnotherEdit()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture? fixture = null;
            TaskCompletionSource<PlaylistWorkspaceMutationRejectedEventArgs>? busyRejection = null;
            bool closeNextMutationNotification = false;
            fixture = CreateActualMainWindowFixture(
                windowTest,
                closeNextMessage: () => closeNextMutationNotification);
            try
            {
                BMSTable localTable = CreateAdmissionFixtureTable();
                fixture.Playlist.BMSTables.Add(localTable);
                PersistFixtureTable(fixture.Root, localTable);

                PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
                workspace.MutationRejected += (_, request) =>
                {
                    if (request.Kind == PlaylistWorkspaceMutationKind.RenameFolder && request.IsBusy)
                    {
                        busyRejection?.TrySetResult(request);
                    }
                };

                TaskCompletionSource<object?> presentationTerminal = NewCompletion();
                int presentationThreadId = 0;
                workspace.PlaylistTablesPresentationChanged += (_, _) =>
                {
                    Interlocked.CompareExchange(
                        ref presentationThreadId,
                        Environment.CurrentManagedThreadId,
                        comparand: 0);
                    BMSTable? activeTarget = fixture!.Playlist.BMSTables
                        .FirstOrDefault(table => table?.playlist_id == fixture!.Table.playlist_id);
                    if (activeTarget?.entries?.Any(entry => entry?.title == "Manual result") == true)
                    {
                        presentationTerminal.TrySetResult(null);
                    }
                };

                BMSTable manualTarget = fixture.Table;
                using var manualServer = new BlockingPlaylistJsonServer(
                    "{\"name\":\"Manual server table\",\"symbol\":\"M\",\"data_url\":\"./score.json\",\"level_order\":[1]}",
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Manual result\",\"artist\":\"Artist\",\"level\":\"1\"}]");
                manualTarget.Page_url = manualServer.PageUri;
                manualTarget.Header_url = manualServer.PageUri;
                manualTarget.Data_url = new Uri("./score.json", UriKind.Relative);
                manualTarget.EnableExternalSync();
                Assert.IsTrue(workspace.ContainsActivePlaylistTable(manualTarget));
                Assert.IsNotNull(manualTarget.Page_url);
                Assert.IsFalse(fixture.Playlist.IsPlaylistUpdating);

                Task manualReload = workspace.ResyncPlaylistTableAsync(manualTarget);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    Task.WhenAny(manualReload, manualServer.DataRequestAccepted.Task),
                    "MainWindowPlaylistWorkspaceWpfTests.manual-data-request");
                Assert.IsFalse(
                    manualReload.IsCompleted,
                    "Manual reload must remain admitted while the local HTTP score response is held.");

                closeNextMutationNotification = true;
                busyRejection = NewCompletion<PlaylistWorkspaceMutationRejectedEventArgs>();
                Task busyRename = workspace.RenameFolderAsync(
                    localTable,
                    PlaylistFolderNode.CreateFolder("Local"),
                    "Local while manual reload is active");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    busyRename,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-busy-rename");
                closeNextMutationNotification = false;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    busyRejection.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-busy-rejection");
                Assert.IsTrue(busyRejection!.Task.Result.IsBusy);
                Assert.AreEqual(PlaylistWorkspaceMutationKind.RenameFolder, busyRejection!.Task.Result.Kind);
                Assert.AreEqual("Local", localTable.entries.Single().folder);
                Assert.IsFalse(
                    localTable.entries.Any(entry => entry.folder == "Local while manual reload is active"),
                    "A rejected edit must not be replayed after the manual reload releases its admission.");

                BlockingPlaylistJsonServer.ResponseStage deferredResponse = manualServer.EnqueueResponse(
                    "{\"name\":\"Deferred server table\",\"symbol\":\"D\",\"data_url\":\"./score.json\",\"level_order\":[1]}",
                    "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Deferred result\",\"artist\":\"Artist\",\"level\":\"1\"}]");
                TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> deferredCompletion = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
                workspace.PlaylistExternalSyncCompleted += (_, request) =>
                {
                    if (request.Reason == "real_deferred_sync")
                    {
                        deferredCompletion.TrySetResult(request);
                    }
                };
                TaskCompletionSource<object?> deferredPresentation = NewCompletion();
                workspace.PlaylistTablesPresentationChanged += (_, _) =>
                {
                    BMSTable? activeTarget = fixture!.Playlist.BMSTables
                        .FirstOrDefault(table => table?.playlist_id == manualTarget.playlist_id);
                    if (activeTarget?.entries?.Any(entry => entry?.title == "Deferred result") == true)
                    {
                        deferredPresentation.TrySetResult(null);
                    }
                };

                var deferredProgress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
                TaskCompletionSource<object?> deferredProgressTerminal = NewCompletion();
                workspace.PlaylistSyncProgressChanged += (_, request) =>
                {
                    deferredProgress.Enqueue(request.Snapshot);
                    if (request.Snapshot.Source == "external_playlist_sync" && !request.Snapshot.IsActive)
                    { deferredProgressTerminal.TrySetResult(null); }
                };
                PlaylistExternalSyncCompletionEventArgs? skipped = null;
                workspace.PlaylistExternalSyncCompleted += (_, result) =>
                {
                    if (result.Reason == "busy_optional_sync") { skipped = result; }
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    workspace.RunExternalPlaylistSyncAsync("busy_optional_sync", false, true, 0),
                    "MainWindowPlaylistWorkspaceWpfTests.busy-optional-sync");
                Assert.IsNotNull(skipped);
                Assert.IsTrue(skipped.WasSkipped);
                Assert.IsFalse(deferredResponse.DataRequestAccepted.Task.IsCompleted);

                manualServer.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    manualReload,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-reload");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    presentationTerminal.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-ui-terminal");
                Assert.AreEqual(
                    TestUiDispatcherHost.Dispatcher.Thread.ManagedThreadId,
                    Volatile.Read(ref presentationThreadId),
                    "Playlist table presentation must finish on the shared UI dispatcher.");

                BMSTable reloadedTarget = fixture.Playlist.BMSTables
                    .Single(table => table.playlist_id == manualTarget.playlist_id);
                Assert.AreNotSame(manualTarget, reloadedTarget);
                Assert.AreEqual("Manual result", reloadedTarget.entries.Single().title);
                Assert.IsFalse(deferredResponse.DataRequestAccepted.Task.IsCompleted, "Busy要求を解放後に自動実行しません。");
                Task explicitSync = workspace.RunExternalPlaylistSyncAsync("real_deferred_sync", false, true, 0);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    Task.WhenAny(deferredResponse.DataRequestAccepted.Task, deferredCompletion.Task),
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-data-request");
                Assert.IsTrue(
                    deferredResponse.DataRequestAccepted.Task.IsCompleted,
                    "The accepted deferred sync must reach its real HTTP request after the manual terminal.");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredResponse.DataRequestAccepted.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-data-request-signal");
                Assert.IsFalse(
                    deferredCompletion.Task.IsCompleted,
                    "Deferred sync must retain its accepted request while the HTTP score response is held.");

                closeNextMutationNotification = true;
                busyRejection = NewCompletion<PlaylistWorkspaceMutationRejectedEventArgs>();
                Task deferredBusyRename = workspace.RenameFolderAsync(
                    localTable,
                    PlaylistFolderNode.CreateFolder("Local"),
                    "Local while deferred sync is active");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredBusyRename,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-busy-rename");
                closeNextMutationNotification = false;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    busyRejection.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-busy-rejection");
                Assert.IsTrue(busyRejection!.Task.Result.IsBusy);
                Assert.AreEqual(PlaylistWorkspaceMutationKind.RenameFolder, busyRejection!.Task.Result.Kind);
                Assert.AreEqual("Local", localTable.entries.Single().folder);

                deferredResponse.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredCompletion.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-completion");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredPresentation.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-ui-terminal");
                Assert.IsTrue(deferredCompletion.Task.Result.Succeeded);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(deferredProgressTerminal.Task, "MainWindowPlaylistWorkspaceWpfTests.deferred-progress-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                PlaylistSyncProgressSnapshot[] notifications = deferredProgress.Where(snapshot => snapshot.Source == "external_playlist_sync").ToArray();
                Assert.IsTrue(notifications.Length > 0);
                Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "external_playlist_sync"));
                Assert.IsTrue(notifications.All(snapshot => snapshot.OperationId == 0));
                Assert.IsTrue(notifications.All(snapshot => snapshot.Request is { } request
                    && request.OperationToken == 0 && request.Source == "external_playlist_sync" && request.Version > 0));
                Assert.IsTrue(notifications.All(snapshot => snapshot.Request == notifications[0].Request));
                Assert.IsFalse(fixture.ViewModel.ProgressHub.Rows.Any(row => row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal)));
                Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive
                    && snapshot.CurrentUri?.AbsoluteUri == manualServer.PageUri.AbsoluteUri));
                Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount > 0));
                Assert.IsFalse(notifications[^1].IsActive);

                TestUiDispatcherHost.AwaitTaskOnDispatcher(explicitSync, "MainWindowPlaylistWorkspaceWpfTests.explicit-sync-actual-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Playlist.WaitForPlaylistMutationIdleAsync(),
                    "MainWindowPlaylistWorkspaceWpfTests.playlist-actual-terminal");

                closeNextMutationNotification = true;
                try
                {
                    awaitOnDispatcher(
                        workspace.RenameFolderAsync(
                            localTable,
                            PlaylistFolderNode.CreateFolder("Local"),
                            "Local after deferred terminal"),
                        "MainWindowPlaylistWorkspaceWpfTests.deferred-retry");
                }
                finally
                {
                    closeNextMutationNotification = false;
                }
                Assert.AreEqual("Local after deferred terminal", localTable.entries.Single().folder);

                // MainWindow の実再読込み入口から、要求受付・HTTP・Owner・Hub まで接続する。
                BlockingPlaylistJsonServer.ResponseStage reloadResponse = manualServer.EnqueueResponse(
                    "{\"name\":\"Reloaded table\",\"symbol\":\"R\",\"data_url\":\"./score.json\",\"level_order\":[1]}",
                    "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"ReloadTables result\",\"artist\":\"Artist\",\"level\":\"1\"}]");
                TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> reloadCompletion = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
                workspace.PlaylistExternalSyncCompleted += (_, request) =>
                {
                    if (request.Reason == "ReloadTables") { reloadCompletion.TrySetResult(request); }
                };
                TaskCompletionSource<object?> reloadProgressTerminal = NewCompletion();
                workspace.PlaylistSyncProgressChanged += (_, request) =>
                {
                    if (request.Snapshot.Source == "external_playlist_sync" && !request.Snapshot.IsActive)
                    { reloadProgressTerminal.TrySetResult(null); }
                };
                Task reloadOperation = fixture.ViewModel.ReloadTablesAsync();
                try
                {
                    Task firstReloadOutcome = Task.WhenAny(reloadResponse.DataRequestAccepted.Task, reloadOperation);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(firstReloadOutcome, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-arrival");
                    if (reloadOperation.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadOperation, "reload-tables-early-failure"); }
                    Assert.IsTrue(reloadResponse.DataRequestAccepted.Task.IsCompleted);
                    Assert.IsFalse(reloadOperation.IsCompleted, "通信から必要出力・公開まで同じPの実Taskで待ちます。");
                    Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    StartupProgressWorkflowOwner progress = fixture.ViewModel.ProgressHub.StartupProgress;
                    long operationToken = progress.GetActiveStartupProgressOperationToken();
                    Assert.IsTrue(operationToken > 0);
                    OperationProgressRow progressRow = fixture.ViewModel.ProgressHub.Rows.Single(row =>
                        row.Key.StartsWith("background:external_playlist_sync:", StringComparison.Ordinal)
                        || row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal));
                    Assert.AreEqual("startup", progressRow.ParentKey);
                    StringAssert.Contains(progressRow.Label, Resources.Statusbar_progress_task_external_playlist_sync);
                    Assert.AreEqual(Resources.Statusbar_progress_reload_tables, progress.Label);
                    reloadResponse.ReleaseResponse();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadOperation, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-operation");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadCompletion.Task, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-completion");
                    Assert.IsTrue(reloadCompletion.Task.Result.Succeeded);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadProgressTerminal.Task, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-progress-terminal");
                    PlaylistSyncProgressSnapshot[] captured = deferredProgress.Where(snapshot => snapshot.Source == "external_playlist_sync"
                        && snapshot.Request?.OperationToken == operationToken).ToArray();
                    Assert.IsTrue(captured.Length > 0);
                    Assert.IsTrue(captured.All(snapshot => snapshot.Request == captured[0].Request));
                    Assert.IsFalse(captured[^1].IsActive);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(fixture.ViewModel.ProgressHub.Rows.Any(row => row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal)));
                }
                finally
                {
                    reloadResponse.ReleaseResponse();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadOperation, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-drain");
                }

            }
            finally
            {
                closeNextMutationNotification = false;
                ActualMainWindowFixture closingFixture = fixture!;
                fixture = null;
                closingFixture.Close();
            }

            static void awaitOnDispatcher(Task task, string operationName)
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(task, operationName);
            }
        });
    }

    [TestMethod]
    public void PlaylistDialogs_UseDirectWorkspaceComposition()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                SettingsWindow? settingsWindow = null;
                try
                {
                    settingsWindow = window.CreateSettingsWindowForPresentation();

                    Assert.IsFalse(settingsWindow.IsVisible);
                    Assert.AreSame(viewModel.SettingDialog, settingsWindow.DataContext);
                    Assert.AreSame(viewModel.PlaybackPanel, settingsWindow.PlaybackPanel);
                    Assert.AreSame(viewModel.PlaylistWorkspace, settingsWindow.PlaylistWorkspace);

                    var loadPlaylistDialog = (FrameworkElement)window.FindName("loadPlaylistURIDialog");
                    Assert.IsNotNull(loadPlaylistDialog);
                    Assert.AreSame(viewModel.PlaylistWorkspace, loadPlaylistDialog.DataContext);
                }
                finally
                {
                    settingsWindow?.CloseFromPresentation();
                }
            });
    }

    [TestMethod]
    public void PlaylistRootReload_UsesOneTypedTerminalAndHandlesBeforeAsyncCompletion()
    {
        TaskCompletionSource<object?> completion = NewCompletion();
        int callCount = 0;
        MainWindowPlaylistTablesReloadTerminal reloadTerminal =
            new(() =>
            {
                callCount++;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (_, window) =>
                {
                    try
                    {

                        var menu = (ContextMenu)window.FindResource("treeViewPlaylistRootContextMenu");
                        MenuItem reload = menu.Items.OfType<MenuItem>().Last();

                        RoutedEventArgs args = RaiseMenuClick(reload);

                        Assert.IsTrue(args.Handled);
                        Assert.AreEqual(1, callCount);
                        Assert.IsFalse(completion.Task.IsCompleted);

                    }
                    finally
                    {
                        completion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion.Task, "compiled-playlist-terminal-delegate");
                    }

                },
                playlistWorkspaceTerminals: CreateTerminals(tablesReload: reloadTerminal));
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    [TestMethod]
    public void PlaylistTableListImportMenu_StaysOpenOnlyForLeafItemsAndUsesQueue()
    {
        var collectionCalls = new List<BMSTableSimple>();
        var builtInTags = new List<string>();
        BMSTableSimpleCategorized parent = new()
        {
            name = "Collection group",
            Children =
            [
                new BMSTableSimpleCategorized
                {
                    name = "Collection leaf",
                    url = new Uri("https://example.invalid/collection.json")
                }
            ]
        };
        MainWindowPlaylistCollectionImportTerminal importTerminal = new(
            source =>
            {
                collectionCalls.Add(source);
                return true;
            },
            rawTag =>
            {
                builtInTags.Add(rawTag);
                return true;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var rootMenu = (ContextMenu)window.FindResource("treeViewPlaylistRootContextMenu");
                MaterializeMenuItems(rootMenu);
                MenuItem collectionMenu = FindMenuItem(
                    rootMenu,
                    "treeViewPlaylistRootContextMenuItemLoadPlaylistCollection");
                collectionMenu.ItemsSource = new List<BMSTableSimpleCategorized> { parent };
                MaterializeMenuItems(collectionMenu);

                MenuItem parentItem = GetOrGenerateMenuItem(collectionMenu, parent);
                Assert.IsNotNull(parentItem);
                Assert.IsFalse(parentItem.StaysOpenOnClick);
                MaterializeMenuItems(parentItem);

                BMSTableSimple leaf = parent.Children.Single();
                MenuItem leafItem = GetOrGenerateMenuItem(parentItem, leaf);
                Assert.IsNotNull(leafItem);
                Assert.IsTrue(leafItem.StaysOpenOnClick);

                RoutedEventArgs collectionArgs = RaiseMenuClick(leafItem);
                Assert.IsTrue(collectionArgs.Handled);
                Assert.AreEqual(1, collectionCalls.Count);
                Assert.AreSame(leaf, collectionCalls[0]);

                MenuItem builtInMenu = FindMenuItem(
                    rootMenu,
                    "treeViewPlaylistRootContextMenuItemLoadWalkureTable");
                MenuItem[] builtInLeaves = builtInMenu.Items.OfType<MenuItem>().ToArray();
                CollectionAssert.AreEquivalent(new[]
                {
                    "bmseeker:table.estimation?type=easy", "bmseeker:table.estimation?type=normal",
                    "bmseeker:table.estimation?type=hard", "bmseeker:table.estimation?type=fc",
                    "bmseeker:table.recommended", "bmseeker:table.recommended?base=failed",
                    "bmseeker:table.recommended?failed=noplay"
                }, builtInLeaves.Select(item => (string)item.Tag).ToArray());
                foreach (MenuItem builtInLeaf in builtInLeaves)
                {
                    RoutedEventArgs args = RaiseMenuClick(builtInLeaf);
                    Assert.IsTrue(args.Handled);
                }
                CollectionAssert.AreEqual(builtInLeaves.Select(item => (string)item.Tag).ToArray(), builtInTags);
            },
            playlistWorkspaceTerminals: CreateTerminals(collectionImport: importTerminal));
    }

    [TestMethod]
    public void PlaylistUrlBulkImport_IsOwnedByWorkspaceAndShellForwarded()
    {
        TaskCompletionSource<object?> singleCompletion = NewCompletion();
        TaskCompletionSource<object?> bulkCompletion = NewCompletion();
        var singleUrls = new List<Uri>();
        var availabilityCalls = new List<(object Context, IReadOnlyList<object> Rows)>();
        var bulkCalls = new List<(IReadOnlyList<object> Rows, bool IsDiff)>();
        var externalLookupCalls = new List<IReadOnlyList<object>>();
        int expansionCalls = 0;
        PlaylistUrlInstallTreeExpansionEventSourceFake expansionEventSource = new();
        PlaylistUrlInstallTreeExpansionOwnerFake expansionOwner = new(expansionEventSource);
        BMSTable playlistTable = new() { name = "URL playlist" };
        PlaylistDetailRow firstRow = CreatePlaylistUrlRow(
            playlistTable,
            "33333333333333333333333333333333",
            "https://example.invalid/main-first.zip",
            "https://example.invalid/diff-first.zip");
        PlaylistDetailRow secondRow = CreatePlaylistUrlRow(
            playlistTable,
            "44444444444444444444444444444444",
            "https://example.invalid/main-second.zip",
            "https://example.invalid/diff-second.zip");
        MainWindowPlaylistUrlAcquisitionTerminal urlAcquisition = new(
            url =>
            {
                singleUrls.Add(url);
                return singleCompletion.Task;
            },
            (rows, isDiff) =>
            {
                bulkCalls.Add((rows, isDiff));
                return bulkCompletion.Task;
            },
            rows =>
            {
                externalLookupCalls.Add(rows);
                return Task.CompletedTask;
            },
            (contextRow, rows) =>
            {
                availabilityCalls.Add((contextRow, rows));
                return new PlaylistUrlContextMenuAvailability(
                    isPlaylistContext: true,
                    isBulkContext: rows.Count > 1,
                    canOpenUrl: true,
                    canOpenDiffUrl: true,
                    canFindExternalPackage: true);
            });
        MainWindowPlaylistUrlInstallTreeExpansionTerminal expansion = new(() =>
        {
            Assert.IsTrue(bulkCompletion.Task.IsCompleted);
            expansionCalls++;
        });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    try
                    {
                        var table = (CustomTableView)window.FindName("customTableView");
                        table.ItemsSource = new List<object> { firstRow, secondRow };
                        table.SelectRowsByPredicate(_ => true);
                        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                        var menu = (ContextMenu)window.FindResource("tableContextMenu");
                        menu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                        OpenContextMenu(menu);

                        Assert.AreEqual(1, availabilityCalls.Count);
                        Assert.AreSame(firstRow, availabilityCalls[0].Context);
                        Assert.AreEqual(2, availabilityCalls[0].Rows.Count);
                        Assert.AreSame(firstRow, availabilityCalls[0].Rows[0]);
                        Assert.AreSame(secondRow, availabilityCalls[0].Rows[1]);

                        MenuItem openUrl = FindMenuItem(menu, "tableContextMenuItemOpenURL");
                        RoutedEventArgs openArgs = RaiseMenuClick(openUrl);
                        Assert.IsTrue(openArgs.Handled);
                        Assert.AreEqual(1, bulkCalls.Count);
                        Assert.IsFalse(bulkCalls[0].IsDiff);
                        Assert.AreEqual(2, bulkCalls[0].Rows.Count);
                        Assert.AreSame(firstRow, bulkCalls[0].Rows[0]);
                        Assert.AreSame(secondRow, bulkCalls[0].Rows[1]);
                        Assert.IsFalse(bulkCompletion.Task.IsCompleted);
                        Assert.AreEqual(0, expansionCalls);

                        bulkCompletion.SetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(bulkCompletion.Task, "compiled-bulk-url-terminal");

                        RoutedEventArgs diffArgs = RaiseMenuClick(
                            FindMenuItem(menu, "tableContextMenuItemOpenURLdiff"));
                        Assert.IsTrue(diffArgs.Handled);
                        Assert.AreEqual(2, bulkCalls.Count);
                        Assert.IsTrue(bulkCalls[1].IsDiff);
                        Assert.AreEqual(2, bulkCalls[1].Rows.Count);
                        Assert.AreSame(firstRow, bulkCalls[1].Rows[0]);
                        Assert.AreSame(secondRow, bulkCalls[1].Rows[1]);

                        RoutedEventArgs lookupArgs = RaiseMenuClick(
                            FindMenuItem(menu, "tableContextMenuItemFindExternalPackage"));
                        Assert.IsTrue(lookupArgs.Handled);
                        Assert.AreEqual(1, externalLookupCalls.Count);
                        Assert.AreEqual(2, externalLookupCalls[0].Count);
                        Assert.AreSame(firstRow, externalLookupCalls[0][0]);
                        Assert.AreSame(secondRow, externalLookupCalls[0][1]);

                        expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
                        Assert.AreEqual(1, expansionCalls);

                        table.ItemsSource = new List<object> { firstRow };
                        table.SelectRowsByPredicate(_ => true);
                        table.Width = 120d;
                        table.Height = 80d;
                        table.HeaderHeight = 0d;
                        table.RowHeight = 60d;
                        table.Columns =
                        [
                            new CustomTableColumn(
                                "Url1",
                                "URL1",
                                layout: null,
                                fallbackOrder: 0,
                                sortMemberPath: null,
                                alignment: TextAlignment.Center,
                                textSelector: _ => "download",
                                minWidth: 40,
                                maxWidth: 40,
                                canResize: false,
                                cellKind: CustomTableCellKind.DownloadIcon)
                        ];
                        MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                        table.Measure(new Size(table.Width, table.Height));
                        table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                        table.UpdateLayout();

                        CustomTableHitTestResult hit = table.HitTestTable(new Point(20d, 30d));
                        Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
                        Assert.AreEqual(0, hit.RowIndex);
                        Assert.AreSame(firstRow, hit.Row);
                        Assert.AreEqual("Url1", hit.Column?.Id);
                        Assert.AreEqual(CustomTableCellKind.DownloadIcon, hit.Column?.CellKind);

                        // Physical cursor input is forbidden in this lane, and CustomTableView has no
                        // deterministic typed action-dispatch seam. Keep this test's reflection narrowly
                        // scoped to the existing CellActionRequested backing subscriber until such a seam
                        // is introduced, then retire this invocation for that typed/internal route.
                        FieldInfo actionField = typeof(CustomTableView).GetField(
                            nameof(CustomTableView.CellActionRequested),
                            BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var actionSubscriber = actionField.GetValue(table) as Delegate;
                        Assert.IsNotNull(actionSubscriber);
                        Assert.AreEqual(1, actionSubscriber!.GetInvocationList().Length);
                        actionSubscriber.DynamicInvoke(table, new CustomTableCellActionRequestedEventArgs(hit));
                        Assert.AreEqual(1, singleUrls.Count);
                        Assert.AreEqual(firstRow.Url, singleUrls[0]);
                        Assert.IsFalse(singleCompletion.Task.IsCompleted);
                        singleCompletion.SetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(singleCompletion.Task, "compiled-single-url-terminal");
                    }
                    finally
                    {
                        singleCompletion.TrySetResult(null);
                        bulkCompletion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(singleCompletion.Task, "compiled-single-url-finally");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(bulkCompletion.Task, "compiled-bulk-url-finally");
                    }
                },
                playlistWorkspaceTerminals: CreateTerminals(
                    urlAcquisition: urlAcquisition,
                    urlInstallTreeExpansionEventSource: expansionEventSource,
                    urlInstallTreeExpansion: expansion));

            Assert.AreEqual(0, expansionEventSource.SubscriberCount);
            expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
            Assert.AreEqual(1, expansionCalls);
        }
        finally
        {
            singleCompletion.TrySetResult(null);
            bulkCompletion.TrySetResult(null);
        }
    }

    [TestMethod]
    public void PlaylistUrlInstallTreeExpansion_UsesSuccessfulOwnerEventAndUnsubscribes()
    {
        int expansionCalls = 0;
        PlaylistUrlInstallTreeExpansionEventSourceFake expansionEventSource = new();
        PlaylistUrlInstallTreeExpansionOwnerFake expansionOwner = new(expansionEventSource);
        MainWindowPlaylistUrlInstallTreeExpansionTerminal expansion = new(() => expansionCalls++);

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, _) =>
            {
                Assert.AreEqual(1, expansionEventSource.SubscriberCount);

                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Rejected);
                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Faulted);
                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Canceled);
                Assert.AreEqual(0, expansionEventSource.PublishCount);
                Assert.AreEqual(0, expansionCalls);

                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
                Assert.AreEqual(1, expansionEventSource.PublishCount);
                Assert.AreEqual(1, expansionCalls);
            },
            playlistWorkspaceTerminals: CreateTerminals(
                urlInstallTreeExpansionEventSource: expansionEventSource,
                urlInstallTreeExpansion: expansion));

        Assert.AreEqual(0, expansionEventSource.SubscriberCount);
        expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
        Assert.AreEqual(2, expansionEventSource.PublishCount);
        Assert.AreEqual(1, expansionCalls);
    }

    [TestMethod]
    public void PlaylistEntryRemovalRoutesThroughWorkspaceOwner()
    {
        TaskCompletionSource<object?> completion = NewCompletion();
        IReadOnlyList<object>? capturedRows = null;
        int callCount = 0;
        BMSTable playlistTable = new() { name = "Playlist" };
        PlaylistDetailRow firstRow = CreatePlaylistRow(
            playlistTable,
            "11111111111111111111111111111111",
            @"C:\wave6e-playlist\first.bms");
        PlaylistDetailRow secondRow = CreatePlaylistRow(
            playlistTable,
            "22222222222222222222222222222222",
            @"C:\wave6e-playlist\second.bms");
        MainWindowPlaylistEntryRemovalTerminal entryRemoval = new(
            rows =>
            {
                callCount++;
                capturedRows = rows;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    try
                    {

                        var table = (CustomTableView)window.FindName("customTableView");
                        table.ItemsSource = new List<object> { firstRow, secondRow };
                        table.SelectRowsByPredicate(row => ReferenceEquals(row, firstRow) || ReferenceEquals(row, secondRow));
                        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                        var menu = (ContextMenu)window.FindResource("tableContextMenu");
                        menu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                        MenuItem removeEntry = FindMenuItem(menu, "tableContextMenuItemDeleteEntry");
                        OpenContextMenu(menu);

                        RoutedEventArgs args = RaiseMenuClick(removeEntry);

                        Assert.IsTrue(args.Handled);
                        Assert.AreEqual(1, callCount);
                        Assert.IsFalse(completion.Task.IsCompleted);
                        Assert.IsNotNull(capturedRows);
                        Assert.AreEqual(2, capturedRows!.Count);
                        Assert.AreSame(firstRow, capturedRows![0]);
                        Assert.AreSame(secondRow, capturedRows![1]);

                    }
                    finally
                    {
                        completion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion.Task, "compiled-playlist-terminal-delegate");
                    }

                },
                playlistWorkspaceTerminals: CreateTerminals(entryRemoval: entryRemoval));
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    [TestMethod]
    public void PlaylistOverwriteLevel_RoutesThroughWorkflowOwner()
    {
        TaskCompletionSource<object?> completion = NewCompletion();
        BMSTable? capturedTable = null;
        int callCount = 0;
        BMSTable table = new() { name = "Overwrite target" };
        MainWindowPlaylistTableLevelOverwriteTerminal overwrite = new(
            captured =>
            {
                callCount++;
                capturedTable = captured;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (_, window) =>
                {
                    try
                    {

                        var menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
                        menu.PlacementTarget = new TreeViewItem { DataContext = table };
                        menu.DataContext = table;
                        OpenContextMenu(menu);

                        MenuItem overwriteLevel = FindMenuItem(
                            menu,
                            "treeViewPlaylistTableContextMenuItemOverwriteLevel");
                        overwriteLevel.DataContext = table;
                        RoutedEventArgs args = RaiseMenuClick(overwriteLevel);

                        Assert.IsTrue(args.Handled);
                        Assert.AreEqual(1, callCount);
                        Assert.AreSame(table, capturedTable);
                        Assert.IsFalse(completion.Task.IsCompleted);

                    }
                    finally
                    {
                        completion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion.Task, "compiled-playlist-terminal-delegate");
                    }

                },
                playlistWorkspaceTerminals: CreateTerminals(tableLevelOverwrite: overwrite));
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    [TestMethod]
    public void PlaylistTableContextMenu_CompiledTreePreservesCurrentActionsForEligibleAndIneligibleTables()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
                foreach (BMSTable table in new[]
                {
                    new BMSTable { Page_url = new Uri("https://example.test/external"), is_external_sync = true },
                    new BMSTable { Page_url = new Uri("https://example.test/local"), is_external_sync = false }
                })
                {
                    menu.PlacementTarget = new TreeViewItem { DataContext = table };
                    menu.DataContext = table;
                    OpenContextMenu(menu);

                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemReload").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOpenPageURI").IsEnabled);
                    MenuItem lampViewer = FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOpenLampViewer");
                    Assert.AreEqual(Resources.Open_lamp_viewer, lampViewer.Header);
                    Assert.AreSame(lampViewer, menu.Items[2]);
                    Assert.IsInstanceOfType(menu.Items[3], typeof(Separator));
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOverwriteLevel").IsEnabled);
                    Assert.AreEqual(
                        !table.is_external_sync,
                        FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemCreateNewFolder").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemRemoveTable").IsEnabled);
                }

                var summaryMenu = (ContextMenu)window.FindResource("playlistSummaryContextMenu");
                MenuItem summaryLampViewer = FindMenuItem(summaryMenu, "playlistSummaryContextMenuOpenLampViewer");
                Assert.AreEqual(Resources.Open_lamp_viewer, summaryLampViewer.Header);
                Assert.AreSame(summaryLampViewer, summaryMenu.Items[2]);
                Assert.IsInstanceOfType(summaryMenu.Items[3], typeof(Separator));
            });
    }

    [TestMethod]
    public void PlaylistLampNavigation_SelectsTableRootThroughComposedMainWindowTerminal()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var foregroundOperations = new List<string>();
            ActualMainWindowFixture? fixture = null;
            fixture = CreateActualMainWindowFixture(
                windowTest,
                CreateForegroundTerminal(
                    foregroundOperations,
                    () =>
                    {
                        var playlistRoot = (TreeViewItem)fixture!.Window.FindName("treeViewItemPlaylist");
                        var selectedTable = playlistRoot.ItemContainerGenerator
                            .ContainerFromItem(fixture!.Table) as TreeViewItem;
                        Assert.IsNotNull(selectedTable);
                        Assert.IsTrue(
                            selectedTable!.IsSelected,
                            "the playlist root selection must be applied before foreground activation");
                    }));
            try
            {
                fixture.Table.entries =
                [
                    new BMSTableEntry
                    {
                        folder = "normal",
                        md5 = new string('a', 32)
                    }
                ];
                fixture.Table.Folder_order = ["normal"];
                TestUiDispatcherHost.ProcessQueuedPresentation();

                var playlistRoot = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                MaterializeTreeItems(playlistRoot);
                var tableItem = playlistRoot.ItemContainerGenerator
                    .ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem, "the active playlist table must be materialized in the shell tree");
                tableItem!.IsExpanded = true;
                MaterializeTreeItems(tableItem);

                TreeViewItem? folderItem = FindDescendants<TreeViewItem>(tableItem)
                    .FirstOrDefault(item => item.DataContext is PlaylistFolderNode);
                Assert.IsNotNull(
                    folderItem,
                    "a playlist folder child must be materialized for the root-selection route");
                folderItem!.IsSelected = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(folderItem!.IsSelected);

                var request = PlaylistLampViewerNavigationRequest.ForOverall(
                    fixture.Table.playlist_id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    PlaylistLampSegmentKind.Clear,
                    PlaylistLampClearCategory.ASSIST,
                    rankCategory: null);
                Assert.IsTrue(fixture.ViewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(request));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                tableItem = playlistRoot.ItemContainerGenerator.ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem);
                Assert.IsTrue(tableItem!.IsSelected, "overall lamp navigation must select the playlist table root");
                Assert.IsFalse(
                    FindDescendants<TreeViewItem>(tableItem!)
                        .Where(item => !ReferenceEquals(item, tableItem))
                        .Any(item => item.DataContext is PlaylistFolderNode && item.IsSelected),
                    "overall lamp navigation must not leave a child folder selected");
                CollectionAssert.AreEqual(
                    new[] { "activate", "focus" },
                    foregroundOperations,
                    "successful overall navigation must focus the main shell exactly once");
            }
            finally
            {
                fixture!.Close();
            }
        });
    }

    [TestMethod]
    public void PlaylistLampNavigation_SelectsFolderThroughComposedMainWindowTerminal()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var foregroundOperations = new List<string>();
            ActualMainWindowFixture? fixture = null;
            fixture = CreateActualMainWindowFixture(
                windowTest,
                CreateForegroundTerminal(
                    foregroundOperations,
                    () =>
                    {
                        var playlistRoot = (TreeViewItem)fixture!.Window.FindName("treeViewItemPlaylist");
                        var selectedTable = playlistRoot.ItemContainerGenerator
                            .ContainerFromItem(fixture!.Table) as TreeViewItem;
                        Assert.IsNotNull(selectedTable);
                        TreeViewItem? selectedFolder = FindDescendants<TreeViewItem>(selectedTable!)
                            .FirstOrDefault(item => item.DataContext is PlaylistFolderNode && item.IsSelected);
                        Assert.IsNotNull(
                            selectedFolder,
                            "the folder selection must be applied before foreground activation");
                        Assert.AreEqual(
                            "normal",
                            ((PlaylistFolderNode)selectedFolder!.DataContext).FolderName);
                    }));
            try
            {
                fixture.Table.entries =
                [
                    new BMSTableEntry
                    {
                        folder = "normal",
                        md5 = new string('a', 32)
                    }
                ];
                fixture.Table.Folder_order = ["normal"];
                TestUiDispatcherHost.ProcessQueuedPresentation();

                var playlistRoot = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                MaterializeTreeItems(playlistRoot);
                var tableItem = playlistRoot.ItemContainerGenerator
                    .ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem, "the active playlist table must be materialized in the shell tree");
                tableItem!.IsExpanded = true;
                MaterializeTreeItems(tableItem);

                TreeViewItem? folderItem = FindDescendants<TreeViewItem>(tableItem)
                    .FirstOrDefault(item => item.DataContext is PlaylistFolderNode);
                Assert.IsNotNull(
                    folderItem,
                    "a playlist folder child must be materialized for the folder-selection route");
                tableItem!.IsSelected = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(tableItem!.IsSelected);

                var request = PlaylistLampViewerNavigationRequest.ForFolder(
                    PlaylistLampSegmentInvocationRequest.ForClear(
                        fixture.Table.playlist_id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "normal",
                        PlaylistLampClearCategory.ASSIST));
                Assert.IsTrue(fixture.ViewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(request));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                tableItem = playlistRoot.ItemContainerGenerator.ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem);
                TreeViewItem? selectedFolder = FindDescendants<TreeViewItem>(tableItem!)
                    .FirstOrDefault(item => item.DataContext is PlaylistFolderNode && item.IsSelected);
                Assert.IsNotNull(selectedFolder, "folder lamp navigation must select the target folder");
                Assert.AreEqual("normal", ((PlaylistFolderNode)selectedFolder!.DataContext).FolderName);
                CollectionAssert.AreEqual(
                    new[] { "activate", "focus" },
                    foregroundOperations,
                    "successful folder navigation must focus the main shell exactly once");
            }
            finally
            {
                fixture!.Close();
            }
        });
    }

    [TestMethod]
    public void MainWindowForegroundTerminal_RestoresMinimizedThenAttemptsActivationAndFocus()
    {
        WindowState state = WindowState.Minimized;
        var operations = new List<string>();
        var terminal = new MainWindowForegroundTerminal(
            () => state,
            () =>
            {
                operations.Add("restore");
                state = WindowState.Normal;
            },
            () =>
            {
                operations.Add("activate");
                return false;
            },
            () =>
            {
                operations.Add("focus");
                return false;
            });

        terminal.FocusMainWindow();

        CollectionAssert.AreEqual(new[] { "restore", "activate", "focus" }, operations);
        Assert.AreEqual(WindowState.Normal, state);
    }

    [TestMethod]
    public void PlaylistTableRemoval_RoutesThroughWorkflowOwner()
    {
        RunPlaylistTableRemovalRejectionScenario();
        RunPlaylistTableRemovalSiblingFallbackScenario();
        RunPlaylistTableRemovalEmptyRootScenario();
    }

    [TestMethod]
    public void PlaylistTableRemovalFailure_NotifiesAndPreservesEmptyRootSelection()
    {
        RunPlaylistTableRemovalFailureScenario(
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    }

    [TestMethod]
    public void PlaylistTableRemovalNotificationFailure_IsDiagnosticOnlyAndDoesNotRetry()
    {
        RunPlaylistTableRemovalFailureScenario(
            UiDialogResult.Failed(new InvalidOperationException("playlist removal notification failed")));
    }

    [TestMethod]
    public void PlaylistTableRemovalCancellation_DoesNotNotify()
    {
        // これは確認 dialog の Cancel であり、operation が OperationCanceledException になる経路とは分けて観測する。
        var dialogs = new RecordingPlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                (TreeViewItem playlistRoot, MenuItem removeTable) =
                    PreparePlaylistTableRemovalContext(window, new BMSTable { name = "Cancelled" });

                RoutedEventArgs args = RaiseMenuClick(removeTable);
                Assert.IsTrue(args.Handled);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    dialogs.ConfirmationShown.Task,
                    "playlist table removal cancellation confirmation");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                Assert.AreEqual(0, dialogs.MessageRequests.Count);
                Assert.IsTrue(playlistRoot.IsSelected);
                Assert.AreEqual(1, playlistRoot.Items.Count);
            },
            playlistWorkspaceDialogService: dialogs);
    }

    [TestMethod]
    public void PlaylistTableRemovalOperationCancellation_DoesNotNotify()
    {
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        var dialogs = new RecordingPlaylistWorkspaceDialogService();
        var cancellationToken = new CancellationToken(canceled: true);
        int callCount = 0;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (_, applySelectionBeforeMutation) =>
            {
                callCount++;
                applySelectionBeforeMutation();
                return Task.FromCanceled(cancellationToken);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, new BMSTable { name = "Cancelled operation" });
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        selectionCompletion.Task,
                        "playlist table removal operation cancellation cleanup");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.AreEqual(0, dialogs.MessageRequests.Count);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    Assert.IsNull(viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection().Table);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal),
            playlistWorkspaceDialogService: dialogs);
    }

    private static void RunPlaylistTableRemovalFailureScenario(UiDialogResult messageResult)
    {
        BMSTable firstTable = new() { name = "First" };
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        var failure = new InvalidOperationException("playlist table removal persistence failed");
        var dialogs = new RecordingPlaylistWorkspaceDialogService
        {
            MessageResult = messageResult
        };
        int callCount = 0;
        PlaylistTreeSelectionActivatedEventArgs? emptyRootSelection = null;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                callCount++;
                Assert.AreSame(firstTable, table);
                applySelectionBeforeMutation();
                return Task.FromException(failure);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        emptyRootSelection = args;
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable);
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);

                    // この fixture では pre-completed の Task.FromException と同期完了する dialog fake を使うため、
                    // RaiseEvent の復帰時点まで async void handler が通知と cleanup を実行する前提を signal で確認する。
                    // callback の開始だけを完了とみなさず、旧 Logging の ContinueWith が fault を吸収する経路とも区別する。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(selectionCompletion.Task, dialogs.MessageShown.Task),
                        "playlist table removal failure notification and empty-root cleanup");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.IsNotNull(emptyRootSelection);
                    Assert.IsFalse(emptyRootSelection!.IsSummary);
                    Assert.IsNotNull(emptyRootSelection!.Detail);
                    Assert.IsNull(emptyRootSelection!.Detail.Table);
                    Assert.AreEqual(1, dialogs.MessageRequests.Count);
                    Assert.IsTrue(
                        dialogs.MessageRequests[0].MessageBoxText.Contains(
                            failure.Message,
                            StringComparison.Ordinal));
                    Assert.AreEqual(MessageBoxImage.Hand, dialogs.MessageRequests[0].Icon);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    Assert.IsNull(viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection().Table);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal),
            playlistWorkspaceDialogService: dialogs);
    }

    private static void RunPlaylistTableRemovalRejectionScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        BMSTable secondTable = new() { name = "Second" };
        TaskCompletionSource<object?> completion = NewCompletion();
        int callbackInvocationCount = 0;
        int callCount = 0;
        BMSTable? capturedTable = null;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, _) =>
            {
                callCount++;
                capturedTable = table;
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable, secondTable);

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.AreSame(firstTable, capturedTable);
                    Assert.AreEqual(0, callbackInvocationCount);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        completion.Task,
                        "playlist table removal rejection");
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(2, playlistRoot.Items.Count);
                }
                finally
                {
                    completion.TrySetResult(null);
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static void RunPlaylistTableRemovalSiblingFallbackScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        BMSTable secondTable = new() { name = "Second" };
        TaskCompletionSource<object?> completion = NewCompletion();
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        int callbackInvocationCount = 0;
        var callbacks = new List<System.Action>();
        var capturedTables = new List<BMSTable>();
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                capturedTables.Add(table);
                callbacks.Add(applySelectionBeforeMutation);
                callbackInvocationCount++;
                applySelectionBeforeMutation();
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && ReferenceEquals(args.Detail.Table, secondTable))
                    {
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable, secondTable);

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, capturedTables.Count);
                    Assert.AreSame(firstTable, capturedTables[0]);
                    Assert.AreEqual(1, callbacks.Count);
                    Assert.AreEqual(1, callbackInvocationCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(completion.Task, selectionCompletion.Task),
                        "playlist table removal sibling fallback");

                    var sibling = playlistRoot.ItemContainerGenerator.ContainerFromItem(secondTable) as TreeViewItem;
                    Assert.IsNotNull(sibling);
                    Assert.IsTrue(sibling!.IsSelected);
                    Assert.AreSame(secondTable, sibling!.DataContext);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                    completion.TrySetResult(null);
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static void RunPlaylistTableRemovalEmptyRootScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        TaskCompletionSource<object?> completion = NewCompletion();
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        PlaylistTreeSelectionActivatedEventArgs? emptyRootSelection = null;
        int callbackInvocationCount = 0;
        var callbacks = new List<System.Action>();
        var capturedTables = new List<BMSTable>();
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                capturedTables.Add(table);
                callbacks.Add(applySelectionBeforeMutation);
                callbackInvocationCount++;
                applySelectionBeforeMutation();
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        emptyRootSelection = args;
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable);
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, capturedTables.Count);
                    Assert.AreSame(firstTable, capturedTables[0]);
                    Assert.AreEqual(1, callbacks.Count);
                    Assert.AreEqual(1, callbackInvocationCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(completion.Task, selectionCompletion.Task),
                        "playlist table removal empty-root detail selection");

                    Assert.IsNotNull(emptyRootSelection);
                    Assert.IsFalse(emptyRootSelection!.IsSummary);
                    Assert.IsNotNull(emptyRootSelection!.Detail);
                    Assert.IsNull(emptyRootSelection!.Detail.Table);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    PlaylistDetailSelection emptyDetailSelection =
                        viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection();
                    Assert.IsNotNull(emptyDetailSelection);
                    Assert.IsNull(emptyDetailSelection.Table);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                    completion.TrySetResult(null);
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static (TreeViewItem PlaylistRoot, MenuItem RemoveTable) PreparePlaylistTableRemovalContext(
        MainWindow window,
        params BMSTable[] tables)
    {
        var playlistRoot = (TreeViewItem)window.FindName("treeViewItemPlaylist");
        playlistRoot.ItemsSource = null;
        playlistRoot.Items.Clear();
        foreach (BMSTable table in tables)
        {
            playlistRoot.Items.Add(table);
        }
        MaterializeTreeItems(playlistRoot);
        playlistRoot.IsSelected = true;
        TestUiDispatcherHost.ProcessQueuedPresentation();

        var menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
        BMSTable firstTable = tables[0];
        menu.PlacementTarget = new TreeViewItem { DataContext = firstTable };
        menu.DataContext = firstTable;
        OpenContextMenu(menu);
        MenuItem removeTable = FindMenuItem(
            menu,
            "treeViewPlaylistTableContextMenuItemRemoveTable");
        removeTable.DataContext = firstTable;
        return (playlistRoot, removeTable);
    }

    private static void QueueCompletion(TaskCompletionSource<object?> completion)
    {
        TestUiDispatcherHost.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal,
            new Action(() => completion.TrySetResult(null)));
    }

    private static MainWindowPlaylistWorkspaceTerminals CreateTerminals(
        MainWindowPlaylistEntryRemovalTerminal? entryRemoval = null,
        MainWindowPlaylistTableLevelOverwriteTerminal? tableLevelOverwrite = null,
        MainWindowPlaylistTableRemovalTerminal? tableRemoval = null,
        MainWindowPlaylistCollectionImportTerminal? collectionImport = null,
        MainWindowPlaylistUrlAcquisitionTerminal? urlAcquisition = null,
        IMainWindowPlaylistUrlInstallTreeExpansionEventSource? urlInstallTreeExpansionEventSource = null,
        MainWindowPlaylistUrlInstallTreeExpansionTerminal? urlInstallTreeExpansion = null,
        MainWindowPlaylistTablesReloadTerminal? tablesReload = null)
    {
        return new MainWindowPlaylistWorkspaceTerminals(
            entryRemoval ?? new MainWindowPlaylistEntryRemovalTerminal(_ => Task.CompletedTask),
            tableLevelOverwrite ?? new MainWindowPlaylistTableLevelOverwriteTerminal(_ => Task.CompletedTask),
            tableRemoval ?? new MainWindowPlaylistTableRemovalTerminal((_, _) => Task.CompletedTask),
            collectionImport ?? new MainWindowPlaylistCollectionImportTerminal((_) => false, (_) => false),
            urlAcquisition ?? new MainWindowPlaylistUrlAcquisitionTerminal(
                (_) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_) => Task.CompletedTask,
                (_, _) => new PlaylistUrlContextMenuAvailability(
                    isPlaylistContext: false,
                    isBulkContext: false,
                    canOpenUrl: false,
                    canOpenDiffUrl: false,
                    canFindExternalPackage: false)),
            urlInstallTreeExpansionEventSource ?? new PlaylistUrlInstallTreeExpansionEventSourceFake(),
            urlInstallTreeExpansion ?? new MainWindowPlaylistUrlInstallTreeExpansionTerminal(() => { }),
            tablesReload ?? new MainWindowPlaylistTablesReloadTerminal(() => Task.CompletedTask));
    }

    private enum PlaylistUrlInstallOwnerOutcome
    {
        Succeeded,
        Rejected,
        Faulted,
        Canceled
    }

    private sealed class PlaylistUrlInstallTreeExpansionOwnerFake
    {
        private readonly PlaylistUrlInstallTreeExpansionEventSourceFake eventSource;

        internal PlaylistUrlInstallTreeExpansionOwnerFake(
            PlaylistUrlInstallTreeExpansionEventSourceFake eventSource)
        {
            this.eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        }

        internal void Publish(PlaylistUrlInstallOwnerOutcome outcome)
        {
            if (outcome == PlaylistUrlInstallOwnerOutcome.Succeeded)
            {
                eventSource.Publish();
            }
        }
    }

    private sealed class PlaylistUrlInstallTreeExpansionEventSourceFake
        : IMainWindowPlaylistUrlInstallTreeExpansionEventSource
    {
        private Action? handlers;

        internal int SubscriberCount => handlers?.GetInvocationList().Length ?? 0;

        internal int PublishCount { get; private set; }

        public void Subscribe(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            handlers += handler;
        }

        public void Unsubscribe(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            handlers -= handler;
        }

        internal void Publish()
        {
            PublishCount++;
            handlers?.Invoke();
        }
    }

    private static PlaylistDetailRow CreatePlaylistRow(BMSTable table, string md5, string path)
    {
        ChartFile file = ChartTestValues.Empty(ChartFileKind.Bmson) with { Md5 = md5, Path = path, RawTitle = md5 };
        BMSTableEntry entry = new() { md5 = md5, parent = table };
        return new PlaylistDetailSourceRow(
            entry,
            (file))
            .CreateViewRow();
    }

    private static PlaylistDetailRow CreatePlaylistUrlRow(
        BMSTable table,
        string md5,
        string url,
        string urlDiff)
    {
        ChartFile file = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Md5 = md5,
            Path = $@"C:\wave6e-playlist-url\{md5}.bms",
            RawTitle = md5
        };
        BMSTableEntry entry = new()
        {
            md5 = md5,
            parent = table,
            Url = new Uri(url),
            Url_diff = new Uri(urlDiff)
        };
        return new PlaylistDetailSourceRow(
            entry,
            (file))
            .CreateViewRow();
    }

    private static TaskCompletionSource<object?> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BMSTable CreateAdmissionFixtureTable()
    {
        var entry = new BMSTableEntry
        {
            md5 = "cccccccccccccccccccccccccccccccc",
            title = "Local admission entry",
            folder = "Local"
        };
        return new BMSTable
        {
            playlist_id = 2,
            name = "Local admission table",
            symbol = "LOCAL",
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
            is_bmt_output = false,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None,
            Folder_order = ["Local"],
            entries = [entry]
        };
    }

    private static void PersistFixtureTable(string root, BMSTable table)
    {
        string songDbPath = Path.Combine(root, "song.db");
        using var database = new LR2SongDBExtended(songDbPath);
        database.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
        foreach (BMSTableEntry entry in table.entries ?? [])
        {
            database.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
        }
        database.Commit();
    }

    private sealed class BlockingPlaylistJsonServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly object responseSync = new();
        private readonly List<ResponseStage> responseStages = [];
        private readonly Task serverTask;
        private TcpClient? acceptedClient;
        private ResponseStage? currentResponseStage;
        private int nextResponseStageIndex;
        private int disposed;

        internal BlockingPlaylistJsonServer(string headerJson, string scoreJson)
        {
            EnqueueResponse(headerJson, scoreJson);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            PageUri = new Uri("http://127.0.0.1:" + port + "/playlist.json");
            serverTask = ServeAsync();
        }

        internal Uri PageUri { get; }

        internal TaskCompletionSource<object?> DataRequestAccepted => responseStages[0].DataRequestAccepted;

        internal void ReleaseResponse()
        {
            responseStages[0].ReleaseResponse();
        }

        internal ResponseStage EnqueueResponse(string headerJson, string scoreJson)
        {
            var response = new ResponseStage(headerJson, scoreJson);
            lock (responseSync)
            {
                responseStages.Add(response);
            }
            return response;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            ReleaseResponse();
            lock (responseSync)
            {
                foreach (ResponseStage response in responseStages)
                {
                    response.ReleaseResponse();
                }
            }
            lifetime.Cancel();
            listener.Stop();
            acceptedClient?.Dispose();
            try
            {
                serverTask.Wait();
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(IsExpectedShutdownException))
            {
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                    acceptedClient = client;
                    using NetworkStream stream = client.GetStream();
                    string requestTarget = await ReadRequestTargetAsync(stream, lifetime.Token).ConfigureAwait(false);
                    bool isDataRequest = requestTarget.EndsWith("/score.json", StringComparison.OrdinalIgnoreCase);
                    ResponseStage response = GetResponseStage(isDataRequest);
                    byte[] body = Encoding.UTF8.GetBytes(isDataRequest ? response.ScoreJson : response.HeaderJson);
                    byte[] header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    if (isDataRequest)
                    {
                        response.DataRequestAccepted.TrySetResult(null);
                        await response.ResponseReleased.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
                        await stream.WriteAsync(body, lifetime.Token).ConfigureAwait(false);
                        await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await stream.WriteAsync(body, lifetime.Token).ConfigureAwait(false);
                        await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        private ResponseStage GetResponseStage(bool isDataRequest)
        {
            lock (responseSync)
            {
                currentResponseStage ??= nextResponseStageIndex < responseStages.Count
                    ? responseStages[nextResponseStageIndex]
                    : throw new InvalidOperationException("No playlist response stage was queued.");
                ResponseStage response = currentResponseStage;
                if (isDataRequest)
                {
                    currentResponseStage = null;
                    nextResponseStageIndex++;
                }
                return response;
            }
        }

        internal sealed class ResponseStage
        {
            internal ResponseStage(string headerJson, string scoreJson)
            {
                HeaderJson = headerJson ?? throw new ArgumentNullException(nameof(headerJson));
                ScoreJson = scoreJson ?? throw new ArgumentNullException(nameof(scoreJson));
            }

            internal string HeaderJson { get; }

            internal string ScoreJson { get; }

            internal TaskCompletionSource<object?> DataRequestAccepted { get; } = NewCompletion();

            internal TaskCompletionSource<object?> ResponseReleased { get; } = NewCompletion();

            internal void ReleaseResponse()
            {
                ResponseReleased.TrySetResult(null);
            }
        }

        private static async Task<string> ReadRequestTargetAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            int length = 0;
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(length, buffer.Length - length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The playlist HTTP client closed before request headers completed.");
                }
                length += read;
                int terminator = FindHeaderTerminator(buffer, length);
                if (terminator < 0)
                {
                    if (length == buffer.Length)
                    {
                        throw new InvalidDataException("The playlist HTTP request headers exceeded the test limit.");
                    }
                    continue;
                }
                string requestLine = Encoding.ASCII.GetString(buffer, 0, terminator).Split(" ", StringSplitOptions.RemoveEmptyEntries)[1];
                return requestLine;
            }
        }

        private static int FindHeaderTerminator(byte[] buffer, int length)
        {
            for (int index = 3; index < length; index++)
            {
                if (buffer[index - 3] == '\r'
                    && buffer[index - 2] == '\n'
                    && buffer[index - 1] == '\r'
                    && buffer[index] == '\n')
                {
                    return index - 3;
                }
            }
            return -1;
        }

        private static bool IsExpectedShutdownException(Exception exception)
        {
            return exception is OperationCanceledException
                or ObjectDisposedException
                or SocketException;
        }
    }

    private static void ReassertNonActivatingPosition(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // MainWindow restores its persisted native placement during source initialization,
        // after the shared presentation scope has prepared the window. Reassert the one-shot
        // offscreen position after Loaded so that the common policy check observes the actual
        // MainWindow route without allowing foreground interaction. The extra span tolerates
        // WPF coordinates being logical pixels while the native restore uses physical pixels.
        window.Left = SystemParameters.VirtualScreenLeft
            + (SystemParameters.VirtualScreenWidth * 4d)
            + 4096d;
        window.Top = SystemParameters.VirtualScreenTop
            + (SystemParameters.VirtualScreenHeight * 4d)
            + 4096d;
    }

    private static UiDialogCoordinator CreateActualRouteDialogService(
        TestWindowPresentationScope windowTest,
        ModalPreparationRecorder modalPreparation,
        Func<bool>? closeNextMessage = null,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse>? messagePresenter = null)
    {
        ArgumentNullException.ThrowIfNull(windowTest);
        ArgumentNullException.ThrowIfNull(modalPreparation);
        Func<Window, IDisposable> modalScope = dialogWindow =>
            {
                // UiDialogCoordinator invokes this modal scope after creating and owning the
                // child, immediately before ShowDialog. Keep the real coordinator route while
                // giving the shared scope its required pre-show non-activating seam.
                windowTest.PrepareForOwnedPresentation(dialogWindow);
                modalPreparation.Record(dialogWindow);
                RoutedEventHandler? closeOnLoaded = null;
                DispatcherOperation? closeOperation = null;
                if (closeNextMessage?.Invoke() == true)
                {
                    closeOnLoaded = (_, _) =>
                    {
                        dialogWindow.Loaded -= closeOnLoaded!;
                        closeOperation = dialogWindow.Dispatcher.BeginInvoke(
                            DispatcherPriority.Loaded,
                            new Action(() =>
                            {
                                closeOperation = null;
                                if (dialogWindow.IsVisible)
                                {
                                    dialogWindow.Close();
                                }
                            }));
                    };
                    dialogWindow.Loaded += closeOnLoaded!;
                }

                IDisposable activeModalScope = UiDialogOwnerResolver.PushActiveModal(dialogWindow);
                return new CallbackDisposable(() =>
                {
                    if (closeOperation?.Status == DispatcherOperationStatus.Pending)
                    {
                        closeOperation.Abort();
                    }
                    if (closeOnLoaded != null)
                    {
                        dialogWindow.Loaded -= closeOnLoaded!;
                    }
                    activeModalScope.Dispose();
                });
            };
        return messagePresenter == null
            ? new UiDialogCoordinator(new UiDialogOwnerResolver(), modalScope)
            : new UiDialogCoordinator(new UiDialogOwnerResolver(), modalScope, messagePresenter);
    }

    private static MainWindowForegroundTerminal CreateForegroundTerminal(
        ICollection<string> operations,
        Action? activationObserved = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return new MainWindowForegroundTerminal(
            () => WindowState.Normal,
            () => operations.Add("restore"),
            () =>
            {
                activationObserved?.Invoke();
                operations.Add("activate");
                return true;
            },
            () =>
            {
                operations.Add("focus");
                return true;
            });
    }

    private static ActualMainWindowFixture CreateActualMainWindowFixture(
        TestWindowPresentationScope windowTest,
        MainWindowForegroundTerminal? foregroundTerminal = null,
        Func<bool>? closeNextMessage = null,
        Func<CancellationToken, Task<WalkureScoreInput>>? recommendationScoreReader = null,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse>? messagePresenter = null,
        Action<Settings>? configureSettings = null,
        Func<BeatorajaBmtOptionsSnapshot>? bmtOptionsProvider = null)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(MainWindowPlaylistWorkspaceWpfTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);

        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = root;
                values.StandaloneBmsRootPaths = root;
                values.BMSInstallDir = root;
                values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
                values.EnablePlaylistUrlCompletion = false;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseExternalPanelImage = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            });
        configureSettings?.Invoke(settings);
        var lifetime = new RecordingApplicationLifetime();
        var modalPreparation = new ModalPreparationRecorder();
        UiDialogCoordinator actualRouteDialogService = CreateActualRouteDialogService(
            windowTest,
            modalPreparation,
            closeNextMessage, messagePresenter);
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: lifetime,
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var library = new TestBmsLibrary(
            songDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
            operationAdmission: composition.OperationAdmission,
            playlistOperationAdmission: composition.PlaylistOperationAdmission);
        var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
            () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
            beatorajaBmtOptionsProvider: bmtOptionsProvider,
            recommendationScoreReader: recommendationScoreReader, settings: settings);
        var table = new BMSTable
        {
            playlist_id = 1,
            bmt_sort = 1,
            name = NativeModalFixturePlaylistName,
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
            is_bmt_output = false,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None
        };
        playlist.BMSTables = new ObservableCollection<BMSTable> { table };
        var viewModel = new MainWindowViewModel(
            composition,
            new FixedStartupLibraryFactory(library, playlist));
        viewModel.StartupUpdateWorkflow.NotifyClosing();
        viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);

        var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
        MainWindow? window = null;
        try
        {
            Task initialization = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                () => { },
                action => action(),
                () => false);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                initialization,
                "MainWindowPlaylistWorkspaceWpfTests.main-window-initialization");
            Assert.IsTrue(viewModel.IsInitializationCompleted);

            window = ownership.CreateWindow(() => new MainWindow(
                viewModel,
                settingsWindowCreated: null,
                playlistWorkspaceDialogService: actualRouteDialogService,
                mainWindowForegroundTerminal: foregroundTerminal));
            RoutedEventHandler ensureNonActivatingPosition = (_, _) =>
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => ReassertNonActivatingPosition(window)));
            window.Loaded += ensureNonActivatingPosition;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
            }
            finally
            {
                window.Loaded -= ensureNonActivatingPosition;
            }
            return new ActualMainWindowFixture(
                root,
                viewModel,
                window!,
                playlist,
                table,
                lifetime,
                modalPreparation,
                ownership);
        }
        catch (Exception failure)
        {
            try { ownership.Dispose(); }
            catch (Exception cleanupFailure) { failure.Data["MainWindowTestCleanupFailure"] = cleanupFailure.ToString(); }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception cleanupFailure) { failure.Data["MainWindowFixtureDirectoryCleanupFailure"] = cleanupFailure.ToString(); }
            throw;
        }
    }

    private static ModalObservation<PlaylistPropertyDialog> OpenPropertyDialog(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string operationName,
        Action<PlaylistPropertyDialog, ModalObservation<PlaylistPropertyDialog>>? drive = null)
    {
        drive ??= (dialog, _) =>
        {
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
        };
        return OpenDialog(
            owner,
            summary,
            row,
            commandResourcePath: "Resources.Property",
            operationName,
            drive);
    }

    private static ModalObservation<PlaylistSummaryBulkEditDialog> OpenBulkDialog(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string operationName,
        Action<PlaylistSummaryBulkEditDialog, ModalObservation<PlaylistSummaryBulkEditDialog>>? drive = null)
    {
        drive ??= (dialog, observation) =>
        {
            var viewModel =
                (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
            viewModel.BmtOutputOption = viewModel.OnOption;
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
            dialog.WaitForApplyCompletionAsync().ContinueWith(
                _ => observation.CallbackGate.Queue(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        observation.VisibleAfterApply = dialog.IsVisible;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryClose"));
                    }),
                TaskScheduler.Default);
        };
        return OpenDialog(
            owner,
            summary,
            row,
            commandResourcePath: "Resources.Playlist_summary_bulk_edit",
            operationName,
            drive);
    }

    private static ModalObservation<TWindow> OpenDialog<TWindow>(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string commandResourcePath,
        string operationName,
        Action<TWindow, ModalObservation<TWindow>> drive)
        where TWindow : Window
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandResourcePath);
        var opened = new TaskCompletionSource<ModalObservation<TWindow>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ModalObservation<TWindow>? observation = null;
        var callbackGate = new DispatcherCallbackGate(owner.Dispatcher);

        void ObserveDialog()
        {
            if (!callbackGate.IsOpen)
            {
                return;
            }
            TWindow? dialog = Application.Current.Windows
                .OfType<TWindow>()
                .FirstOrDefault(candidate => candidate.IsVisible && candidate.IsLoaded
                    && candidate.ActualWidth > 0 && candidate.ActualHeight > 0);
            if (dialog == null)
            {
                callbackGate.Queue(
                    DispatcherPriority.Loaded,
                    ObserveDialog);
                return;
            }

            observation = new ModalObservation<TWindow>(
                dialog,
                dialog.Owner,
                !owner.IsEnabled,
                dialog.DataContext,
                callbackGate);
            dialog.DataContextChanged += observation.HandleDataContextChanged;
            try
            {
                drive(dialog, observation);
                opened.TrySetResult(observation);
            }
            catch (Exception exception)
            {
                // callbackの失敗を待機Taskへ渡し、nested modalを実際に閉じる。
                // Dispatcher例外の捕捉だけで、来ないopened通知を待ち続けない。
                opened.TrySetException(exception);
                try
                {
                    if (dialog is PlaylistPropertyDialog propertyDialog)
                    {
                        propertyDialog.CloseForOwnerShutdown();
                    }
                    else if (dialog is PlaylistSummaryBulkEditDialog bulkDialog)
                    {
                        bulkDialog.CloseForOwnerShutdown();
                    }
                }
                catch (Exception cleanupFailure)
                {
                    exception.Data["ModalCleanupFailure"] = cleanupFailure;
                }
            }
        }

        try
        {
            var menu = (ContextMenu)owner.FindResource("playlistSummaryContextMenu");
            menu.PlacementTarget = summary;
            menu.Tag = new CustomTableContextMenuContext(row, 0);
            MenuItem command = FindMenuItemByHeaderBindingPath(menu, commandResourcePath);
            callbackGate.Queue(
                DispatcherPriority.Loaded,
                ObserveDialog);
            RaiseMenuClick(command);

            TestUiDispatcherHost.AwaitTaskOnDispatcher(opened.Task, operationName + ".opened");
            observation = opened.Task.GetAwaiter().GetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                observation.DataContextDetached.Task,
                operationName + ".cleanup");
            observation.Window.DataContextChanged -= observation.HandleDataContextChanged;
            return observation;
        }
        finally
        {
            callbackGate.Close();
            if (observation != null)
            {
                observation.Window.DataContextChanged -= observation.HandleDataContextChanged;
            }
        }
    }

    private static Button FindAutomationButton(DependencyObject root, string automationId)
    {
        Button? button = FindDescendants<Button>(root)
            .FirstOrDefault(candidate => string.Equals(
                AutomationProperties.GetAutomationId(candidate),
                automationId,
                StringComparison.Ordinal));
        return button
            ?? throw new AssertFailedException($"Button '{automationId}' was not materialized.");
    }

    private static PropertyNavigationObservation ObservePropertyNavigation(
        FrameworkElement navigation,
        FrameworkElement contentHost)
    {
        AutomationPeer navigationPeer = UIElementAutomationPeer.CreatePeerForElement(navigation)
            ?? throw new AssertFailedException("Playlist property navigation automation was not materialized.");
        ISelectionProvider selectionProvider = navigationPeer.GetPattern(PatternInterface.Selection) as ISelectionProvider
            ?? throw new AssertFailedException("Playlist property navigation does not expose Selection automation.");
        Assert.IsFalse(selectionProvider.CanSelectMultiple);
        AutomationPeer[] navigationItems = navigationPeer.GetChildren()?.ToArray()
            ?? throw new AssertFailedException("Playlist property navigation items were not exposed to Automation.");
        Assert.AreEqual(3, navigationItems.Length, "Playlist property navigation must expose three category peers.");
        PropertyNavigationItem[] selectionItems = navigationItems
            .Select(CreatePropertyNavigationItem)
            .ToArray();
        Assert.IsTrue(selectionItems.All(item => !string.IsNullOrWhiteSpace(item.Peer.GetName())));
        Assert.AreEqual(1, selectionItems.Count(item => item.Provider.IsSelected));
        Assert.AreEqual(1, selectionProvider.GetSelection().Length);

        PropertyNavigationItem initiallySelectedItem = selectionItems.Single(item => item.Provider.IsSelected);
        PropertyNavigationCategory initialCategory = IdentifyVisiblePropertyCategory(contentHost);

        var itemsByCategory = new Dictionary<PropertyNavigationCategory, PropertyNavigationItem>();
        Assert.IsTrue(
            itemsByCategory.TryAdd(initialCategory, initiallySelectedItem),
            "The initially selected property provider must map to one observable content role.");
        foreach (PropertyNavigationItem item in selectionItems)
        {
            if (ReferenceEquals(item, initiallySelectedItem))
            {
                continue;
            }

            PropertyNavigationCategory category;
            if (!item.Peer.IsEnabled())
            {
                AssertAuthorityDefinedCustomFolderAvailability(navigation, item.Peer);
                PropertyNavigationItem selectedBeforeAttempt = selectionItems.Single(candidate => candidate.Provider.IsSelected);
                bool rejected = false;
                try
                {
                    item.Provider.Select();
                }
                catch (ElementNotEnabledException)
                {
                    rejected = true;
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(
                    rejected || !item.Provider.IsSelected,
                    "A disabled property navigation provider must reject Automation selection.");
                Assert.IsTrue(
                    selectedBeforeAttempt.Provider.IsSelected,
                    "A rejected disabled property navigation selection must leave the current category selected.");
                category = PropertyNavigationCategory.CustomFolder;
            }
            else
            {
                item.Provider.Select();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                category = IdentifyVisiblePropertyCategory(contentHost);
            }

            Assert.IsTrue(
                itemsByCategory.TryAdd(category, item),
                $"Multiple navigation items expose the {category} property content.");
        }

        Assert.AreEqual(3, itemsByCategory.Count, "Property navigation categories must map to unique observable content roles.");
        Assert.AreEqual(
            PropertyNavigationCategory.General,
            initialCategory,
            "The SelectionItem provider captured before any mutation must map to the General content role.");
        Assert.AreSame(
            initiallySelectedItem,
            itemsByCategory[PropertyNavigationCategory.General],
            "The captured initial SelectionItem must be the provider mapped to General.");
        initiallySelectedItem.Provider.Select();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        return new PropertyNavigationObservation(selectionProvider, itemsByCategory, initiallySelectedItem);
    }

    private static PropertyNavigationItem CreatePropertyNavigationItem(AutomationPeer peer)
        => new(
            peer,
            peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new AssertFailedException("A property navigation item lacks SelectionItem automation."));

    private static void AssertSelectedPropertyCategory(
        PropertyNavigationObservation navigation,
        PropertyNavigationCategory expectedCategory,
        string? message = null)
    {
        string assertionMessage = message ?? $"Property navigation category {expectedCategory} must be selected.";
        Assert.AreEqual(1, navigation.SelectionProvider.GetSelection().Length, assertionMessage);
        Assert.AreEqual(1, navigation.ItemsByCategory.Values.Count(item => item.Provider.IsSelected), assertionMessage);
        Assert.IsTrue(navigation.ItemsByCategory[expectedCategory].Provider.IsSelected, assertionMessage);
    }

    private static PropertyNavigationCategory IdentifyVisiblePropertyCategory(FrameworkElement contentHost)
    {
        var visibleCategories = new List<PropertyNavigationCategory>();
        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.name)))
        {
            visibleCategories.Add(PropertyNavigationCategory.General);
        }

        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.folder_sort_key))
            || HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.folder_order)))
        {
            visibleCategories.Add(PropertyNavigationCategory.Folder);
        }

        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.output_dir)))
        {
            visibleCategories.Add(PropertyNavigationCategory.CustomFolder);
        }

        Assert.AreEqual(
            1,
            visibleCategories.Count,
            "Exactly one authority-backed Property category content role must be visible.");
        return visibleCategories[0];
    }

    private static bool HasVisibleBinding(DependencyObject root, string path)
    {
        return FindDescendants<FrameworkElement>(root)
            .Any(element => element.IsVisible
                && element.ActualWidth > 0d
                && element.ActualHeight > 0d
                && HasBindingPath(element, path));
    }

    private static bool HasBindingPath(FrameworkElement element, string path)
    {
        LocalValueEnumerator localValues = element.GetLocalValueEnumerator();
        while (localValues.MoveNext())
        {
            if (localValues.Current.Value is BindingExpressionBase expression
                && expression.ParentBindingBase is Binding binding
                && string.Equals(binding.Path?.Path, path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertAuthorityDefinedCustomFolderAvailability(
        FrameworkElement navigation,
        AutomationPeer expectedProvider)
    {
        FrameworkElement[] availabilityHosts = FindDescendants<FrameworkElement>(navigation)
            .Where(element => HasBindingPath(
                element,
                nameof(PlaylistPropertyDialogViewModel.OperationModeLR2DB)))
            .ToArray();
        Assert.AreEqual(
            1,
            availabilityHosts.Length,
            "The conditionally available property category must expose one authority-backed availability binding.");
        Assert.IsFalse(
            availabilityHosts[0].IsEnabled,
            "The authority-backed custom-folder availability binding must be disabled for a rejected provider.");
        AutomationPeer availabilityPeer = UIElementAutomationPeer.CreatePeerForElement(availabilityHosts[0])
            ?? throw new AssertFailedException(
                "The authority-backed custom-folder availability host must expose the disabled SelectionItem provider.");
        Assert.AreEqual(
            expectedProvider.GetAutomationControlType(),
            availabilityPeer.GetAutomationControlType(),
            "The disabled provider must retain the authority-backed custom-folder Automation role.");
        Assert.AreEqual(
            expectedProvider.GetBoundingRectangle(),
            availabilityPeer.GetBoundingRectangle(),
            "The disabled provider must be the authority-backed custom-folder navigation role.");
    }

    private enum PropertyNavigationCategory
    {
        General,
        Folder,
        CustomFolder,
    }

    private sealed record PropertyNavigationItem(
        AutomationPeer Peer,
        ISelectionItemProvider Provider);

    private sealed record PropertyNavigationObservation(
        ISelectionProvider SelectionProvider,
        IReadOnlyDictionary<PropertyNavigationCategory, PropertyNavigationItem> ItemsByCategory,
        PropertyNavigationItem InitiallySelectedItem);

    private static MenuItem FindMenuItemByHeaderBindingPath(
        ItemsControl root,
        string expectedBindingPath)
    {
        MenuItem? command = root.Items
            .OfType<MenuItem>()
            .SingleOrDefault(item => BindingOperations.GetBindingBase(
                    item,
                    HeaderedItemsControl.HeaderProperty) is Binding binding
                && string.Equals(
                    binding.Path?.Path,
                    expectedBindingPath,
                    StringComparison.Ordinal));
        return command
            ?? throw new AssertFailedException(
                $"Menu item with header binding '{expectedBindingPath}' was not materialized.");
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is T typed)
            {
                yield return typed;
            }
            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            foreach (object logicalChild in LogicalTreeHelper.GetChildren(current))
            {
                if (logicalChild is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
    }

    private static void RaiseButtonClick(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

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

    private sealed class RecordingPlaylistWorkspaceDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        internal TaskCompletionSource<UiConfirmationRequest> ConfirmationShown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<UiMessageRequest> MessageShown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            MessageShown.TrySetResult(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            ConfirmationRequests.Add(request);
            ConfirmationShown.TrySetResult(request);
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ModalObservation<TWindow>
        where TWindow : Window
    {
        internal ModalObservation(
            TWindow window,
            Window owner,
            bool ownerEnabled,
            object dataContext,
            DispatcherCallbackGate callbackGate)
        {
            Window = window;
            Owner = owner;
            OwnerEnabled = ownerEnabled;
            DataContext = dataContext;
            CallbackGate = callbackGate;
        }

        internal TWindow Window { get; }

        internal Window Owner { get; }

        internal bool OwnerEnabled { get; }

        internal object DataContext { get; }

        internal DispatcherCallbackGate CallbackGate { get; }

        internal bool VisibleAfterApply { get; set; }

        internal bool ApplyWasPending { get; set; }

        internal bool OwnerCloseWasCanceled { get; set; }

        internal bool ApplyCompletedAtTerminalRequest { get; set; }

        internal int ShutdownCountBeforeApplyCompletion { get; set; }

        internal int ShutdownCountBeforeLockRelease { get; set; }

        internal int DataContextDetachCount { get; private set; }

        internal TaskCompletionSource<object?> DataContextDetached { get; } = NewCompletion();

        internal void HandleDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue != null)
            {
                return;
            }
            DataContextDetachCount++;
            DataContextDetached.TrySetResult(null);
        }
    }

    /// <summary>
    /// Cancels dispatcher callbacks owned by one modal observation and aborts callbacks
    /// that have not started, so a watchdog or terminal cleanup cannot keep polling alive.
    /// </summary>
    private sealed class DispatcherCallbackGate
    {
        private readonly Dispatcher dispatcher;
        private readonly object sync = new();
        private readonly HashSet<DispatcherOperation> pendingOperations = [];
        private bool isOpen = true;

        internal DispatcherCallbackGate(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        internal bool IsOpen
        {
            get
            {
                lock (sync)
                {
                    return isOpen;
                }
            }
        }

        internal void Queue(DispatcherPriority priority, Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            DispatcherOperation? operation = null;
            lock (sync)
            {
                if (!isOpen)
                {
                    return;
                }

                operation = dispatcher.BeginInvoke(
                    priority,
                    new Action(() => Execute(operation!, callback)));
                pendingOperations.Add(operation!);
            }
        }

        internal void Close()
        {
            DispatcherOperation[] operations;
            lock (sync)
            {
                if (!isOpen)
                {
                    return;
                }

                isOpen = false;
                operations = pendingOperations.ToArray();
                pendingOperations.Clear();
            }

            foreach (DispatcherOperation operation in operations)
            {
                operation.Abort();
            }
        }

        private void Execute(DispatcherOperation operation, Action callback)
        {
            lock (sync)
            {
                pendingOperations.Remove(operation);
                if (!isOpen)
                {
                    return;
                }
            }

            callback();
        }
    }

    private sealed class ModalPreparationRecorder
    {
        private readonly List<Window> preparedWindows = [];

        internal int Count => preparedWindows.Count;

        internal void Record(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            preparedWindows.Add(window);
        }

        internal void AssertLatest(Window expectedWindow, int expectedCount)
        {
            ArgumentNullException.ThrowIfNull(expectedWindow);
            Assert.AreEqual(expectedCount, Count);
            Assert.AreSame(expectedWindow, preparedWindows[^1]);
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));

        public void Dispose()
        {
            Interlocked.Exchange(ref dispose, null)?.Invoke();
        }
    }

    private sealed class ActualMainWindowFixture
    {
        internal ActualMainWindowFixture(
            string root,
            MainWindowViewModel viewModel,
            MainWindow window,
            TestBmsPlaylist playlist,
            BMSTable table,
            RecordingApplicationLifetime lifetime,
            ModalPreparationRecorder modalPreparation,
            MainWindowTestLifetime ownership)
        {
            Root = root;
            ViewModel = viewModel;
            Window = window;
            Playlist = playlist;
            Table = table;
            Lifetime = lifetime;
            ModalPreparation = modalPreparation;
            this.ownership = ownership;
        }

        internal string Root { get; }

        internal MainWindowViewModel ViewModel { get; }

        internal MainWindow Window { get; }

        internal TestBmsPlaylist Playlist { get; }

        internal BMSTable Table { get; }

        internal RecordingApplicationLifetime Lifetime { get; }

        internal ModalPreparationRecorder ModalPreparation { get; }

        private readonly MainWindowTestLifetime ownership;

        internal void Close()
        {
            try { ownership.Dispose(); }
            finally { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
        }
    }

    private sealed class FixedStartupLibraryFactory : IStartupLibraryFactory
    {
        internal FixedStartupLibraryFactory(TestBmsLibrary library, TestBmsPlaylist playlist)
        {
            Library = library;
            Playlist = playlist;
        }

        private TestBmsLibrary Library { get; }

        private TestBmsPlaylist Playlist { get; }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile) => Library;

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library) => Playlist;
    }

    private sealed class RecordingApplicationLifetime : IApplicationLifetimePort
    {
        internal TaskCompletionSource<object?> ShutdownRequested { get; } = NewCompletion();

        internal int RequestShutdownCount { get; private set; }

        internal Action? RequestShutdownAction { get; set; }

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
        }

        public void RequestShutdown()
        {
            RequestShutdownCount++;
            ShutdownRequested.TrySetResult(null);
            RequestShutdownAction?.Invoke();
        }

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }

    private static void MaterializeTreeItems(TreeViewItem root)
    {
        root.ApplyTemplate();
        root.Measure(new Size(640d, 480d));
        root.Arrange(new Rect(0d, 0d, 640d, 480d));
        // 必要なbinding/renderを処理して実containerを確定する。全ApplicationIdleは
        // MediaContextの入力待ちにも依存するため、表示内容の完了条件には使わない。
        root.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(root.UpdateLayout));
    }

    private static void MaterializeMenuItems(ItemsControl menu)
    {
        menu.ApplyTemplate();
        menu.Measure(new Size(640d, 480d));
        menu.Arrange(new Rect(0d, 0d, 640d, 480d));
        menu.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(menu.UpdateLayout));
    }

    private static MenuItem GetOrGenerateMenuItem(ItemsControl owner, object item)
    {
        var generated = owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
        if (generated != null)
        {
            return generated;
        }

        int index = owner.Items.IndexOf(item);
        Assert.IsTrue(index >= 0, $"The menu item {item} was not present in its ItemsSource.");
        IItemContainerGenerator generator = owner.ItemContainerGenerator;
        using (generator.StartAt(
            generator.GeneratorPositionFromIndex(index),
            GeneratorDirection.Forward,
            allowStartAtRealizedItem: true))
        {
            DependencyObject candidate = generator.GenerateNext(out bool newlyRealized);
            if (newlyRealized)
            {
                generator.PrepareItemContainer(candidate);
            }
        }

        generated = owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
        return generated!;
    }

    private static void OpenContextMenu(ContextMenu menu)
    {
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static RoutedEventArgs RaiseMenuClick(MenuItem item)
    {
        var args = new RoutedEventArgs(MenuItem.ClickEvent, item);
        item.RaiseEvent(args);
        return args;
    }

    private static MenuItem FindMenuItem(ItemsControl root, string name)
    {
        MenuItem? result = FindMenuItemOrNull(root, name);
        if (result != null)
        {
            return result;
        }
        throw new AssertFailedException($"Menu item '{name}' was not found.");
    }

    private static MenuItem? FindMenuItemOrNull(ItemsControl root, string name)
    {
        foreach (MenuItem item in root.Items.OfType<MenuItem>())
        {
            if (item.Name == name)
            {
                return item;
            }
            if (item.Items.Count > 0)
            {
                MenuItem? nested = FindMenuItemOrNull(item, name);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        return null;
    }
}
