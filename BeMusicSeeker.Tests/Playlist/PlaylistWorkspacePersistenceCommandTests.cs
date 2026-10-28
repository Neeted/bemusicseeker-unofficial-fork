using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using static BeMusicSeeker.Tests.PlaylistWorkspaceTestDataSupport;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

/// <summary>backup・復元・export・BMT順序・dropの実確定と、summary buildの世代・取消・終端を検証します。</summary>
/// <remarks>実SQLite、出力、configの確定順と失敗後の事実は実資源を用い、UIは既存schedulerの受理・実行・完了を制御します。画面の所有modalと入力操作はWPF代表が分担し、process DB lock競争だけ非並列です。</remarks>
[TestClass]
public sealed class PlaylistWorkspacePersistenceCommandTests
{
    /// <summary>旧ファイルの削除共有禁止と旧path限定のSQLite削除失敗を実SQL復元→workspace通知まで通し、残存警告でも新出力・readiness・終了許可・受付解放を保ちます。故障解除後の現在表の明示再出力で過去の整理を再開しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaylistRestore_PreviousCleanupFailureWarnsWithoutBlockingReadinessOrExit(bool failDatabaseCleanup)
    {
        await using var fixture = new PlaylistRestoreOutputFixture();
        fixture.PersistAndOutput(PlaylistRestoreOutputFixture.Table(1, "A"));
        string backupPath = Path.Combine(fixture.DirectoryPath, "restore.sql");
        File.WriteAllText(backupPath, fixture.Playlist.GetPlaylistDump());
        fixture.PersistAndOutput(PlaylistRestoreOutputFixture.Table(1, "B", "Changed"));
        string oldDirectory = Path.Combine(fixture.Settings.LR2CustomFolderOutputBaseDir, "B");
        string oldFile = Path.Combine(oldDirectory, "0000.lr2folder");
        string newFile = Path.Combine(fixture.Settings.LR2CustomFolderOutputBaseDir, "A", "0000.lr2folder");
        FileStream? held = null;
        PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(fixture.Playlist, fixture.Library,
            () => fixture.OutputSettings, () => fixture.Config);
        var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
        workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
        try
        {
            if (failDatabaseCleanup)
            {
                using var db = new LR2SongDBExtended(fixture.SongDb);
                string path = oldFile.Replace("'", "''", StringComparison.Ordinal);
                db.Execute("CREATE TRIGGER fail_restore_old_cleanup BEFORE DELETE ON folder WHEN OLD.path = '" + path
                    + "' BEGIN SELECT RAISE(ABORT, 'old output cleanup failure'); END;");
            }
            else { held = new FileStream(oldFile, FileMode.Open, FileAccess.Read, FileShare.Read); }

            Assert.IsTrue(await workspace.RestorePlaylistBackupAsync(backupPath), "旧整理の残存だけで終了を拒否しません。");
            await fixture.Playlist.StartupReadiness.WaitForRequiredPlaylistReadinessAsync();
            Assert.IsTrue(File.Exists(newFile));
            StringAssert.Contains(BmsPlaylistTestSupport.ReadShiftJisText(newFile), "#TITLE Saved");
            using (var db = new LR2SongDBExtended(fixture.SongDb))
            {
                Assert.AreEqual("A", db.Table<BMSTable>().Single().Output_dir);
                Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.path == newFile && row.title == "Saved"));
                Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.path == oldFile));
            }
            if (!failDatabaseCleanup) { Assert.IsTrue(File.Exists(oldFile)); }
            IReadOnlyList<PlaylistOperationNotificationOwner.OperationNotification> receipt = notifications.Single().Receipt.Notifications;
            Assert.IsTrue(receipt.Any(item => item.Severity == PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning));
            Assert.IsTrue(receipt.Any(item => item.Severity == PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning
                && item.Message.Contains(oldDirectory, StringComparison.OrdinalIgnoreCase)), "残存する旧配置を警告で識別できます。");
            Assert.IsTrue(receipt.Any(item => item.Message.Contains(Resources.Msg_success_playlist_restore, StringComparison.Ordinal)));
            Assert.IsFalse(receipt.Any(item => item.Severity == PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error
                || item.Message.Contains(Resources.Msg_failed_playlist_restore, StringComparison.Ordinal)));
            Assert.IsTrue(fixture.Playlist.CanEnterPlaylistMutation);
            Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
            held?.Dispose();
            held = null;
            if (failDatabaseCleanup)
            {
                using var db = new LR2SongDBExtended(fixture.SongDb);
                db.Execute("DROP TRIGGER fail_restore_old_cleanup;");
            }
            fixture.Playlist.ReOutputCustomFolder(fixture.Playlist.BMSTables.Single());
            using (var db = new LR2SongDBExtended(fixture.SongDb))
            { Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.path == oldFile), "明示再出力へ過去の旧整理を持ち越しません。"); }
            Assert.AreEqual(!failDatabaseCleanup, File.Exists(oldFile));
        }
        finally
        {
            held?.Dispose();
            workspace.StopPlaylistSummaryDataBuild();
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        }
    }
    /// <summary>実workerのrawからDispatcher適用へ接続し、通常・表示失敗・交換・停止でもAction後のscheduler実Taskより先にbuild idleへ抜けません。entry集計はworker、通知はUIに属します。</summary>
    [DataTestMethod]
    [DataRow("normal")]
    [DataRow("failure")]
    [DataRow("hidden")]
    [DataRow("stop")]
    [DoNotParallelize]
    public async Task SummaryBuild_OwnsDispatcherApplicationThroughSchedulerCompletion(string outcome)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? apply = null;
        bool scheduledFromWorker = false, publishedOnUi = true;
        async Task Apply(Action action)
        {
            await execute.Task;
            await TestUiDispatcherHost.Dispatcher.InvokeAsync(action).Task;
            executed.TrySetResult();
            await complete.Task;
            if (outcome == "failure") { throw new InvalidOperationException("summary UI completion failure"); }
        }
        using PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => fixture.Playlist,
            playlistLibraryProvider: () => fixture.Library, presentationRefreshDeferredProvider: _ => true,
            restoreUiApplyScheduler: action =>
            {
                scheduledFromWorker = !TestUiDispatcherHost.Dispatcher.CheckAccess();
                apply = Apply(action);
                entered.TrySetResult();
                return apply;
            }, restoreUiThreadCheck: () => TestUiDispatcherHost.Dispatcher.CheckAccess());
        workspace.PlaylistSummaryView.CollectionChanged += (_, _) => publishedOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess();
        TestUiDispatcherHost.Invoke(() => workspace.RequestPlaylistSummaryMode(true));
        workspace.RebuildPlaylistSummaryView();
        Task idle = workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, idle, "summary-build-ui-scheduled");
            Assert.IsNotNull(apply);
            if (outcome == "hidden") { TestUiDispatcherHost.Invoke(() => workspace.RequestPlaylistSummaryMode(false)); }
            if (outcome == "stop") { workspace.StopPlaylistSummaryDataBuild(); }
            execute.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(executed.Task, apply, "summary-build-ui-executed");
            Assert.IsTrue(scheduledFromWorker);
            Assert.IsTrue(publishedOnUi);
            Assert.IsFalse(idle.IsCompleted);
            Assert.IsFalse(workspace.IsPlaylistSummaryDataBuildIdle);
            if (outcome is "normal" or "failure") { Assert.AreEqual("Prefix", workspace.PlaylistSummaryView.Single().Name); }
            else { Assert.AreEqual(0, workspace.PlaylistSummaryView.Count); }
            complete.TrySetResult();
            if (outcome == "failure")
            { Assert.AreEqual("summary UI completion failure", (await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => apply)).Message); }
            else { await apply; }
            await idle;
        }
        finally
        {
            execute.TrySetResult(); complete.TrySetResult();
            try { if (apply != null) { await apply; } }
            catch (InvalidOperationException failure) when (outcome == "failure" && failure.Message == "summary UI completion failure") { }
            await idle;
        }
    }

    /// <summary>順序四入口を実DB・本番サマリー・configへ接続し、同期前のP保持、無変更と次操作を確認します。</summary>
    [DataTestMethod]
    // 四つの入口へ異なる選択を渡し、どれも独立期待の3→1→2へ並べ替えます。
    [DataRow("top")]
    [DataRow("bottom")]
    [DataRow("visible")]
    [DataRow("drop")]
    public async Task BmtOrderCommands_CompleteDatabaseSummaryAndConfigInOneAcceptedTask(string command)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspacePersistenceCommandTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? operation = null;
        PlaylistWorkspaceViewModel? workspace = null;
        bool gateEnabled = false;
        try
        {
            string db = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(db);
            string root = Path.Combine(directory, "beatoraja");
            string output = Path.Combine(root, "table");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            var options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
            BMSTable[] tables = Enumerable.Range(1, 3).Select(id => new BMSTable
            {
                playlist_id = id,
                name = "Table " + id,
                bmt_sort = id,
                last_update = new DateTime(2024, 1, 1),
                Folder_order = ["Folder"],
                entries = [new BMSTableEntry { title = "Song " + id, md5 = new string((char)('a' + id), 32), folder = "Folder" }]
            }).ToArray();
            var playlist = new TestBmsPlaylist(db, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () =>
                {
                    if (gateEnabled)
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    }
                    return options;
                },
                () => new CustomFolderOutputSettingsSnapshot(),
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(db, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = new ObservableCollection<BMSTable>(tables) };
            playlist.CommitBMSTablesWithEntriesToDB(tables);
            await playlist.BmtOutput.ExportAllAsync("order-test-initial");
            string[] files = Directory.GetFiles(output);
            Dictionary<string, byte[]> bytes = files.ToDictionary(path => path, File.ReadAllBytes);
            Dictionary<string, DateTime> times = files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
            var library = new TestBmsLibrary(db);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
            workspace.RebuildPlaylistSummaryView(runAsync: false);
            PlaylistSummaryRow[] rows = workspace.PlaylistSummaryView.ToArray();
            Assert.AreEqual(3, rows.Length);
            PlaylistSummaryRow[] desired = [rows[2], rows[0], rows[1]];
            gateEnabled = true;
            operation = Task.Run(async () =>
            {
                switch (command)
                {
                    case "top": await workspace.MoveSummaryRowsToBmtTopAsync([rows[2]]); break;
                    case "bottom": await workspace.MoveSummaryRowsToBmtBottomAsync([rows[0], rows[1]]); break;
                    case "visible": await workspace.ApplyCurrentVisibleBmtOrderAsync(desired); break;
                    case "drop": await workspace.DropSummaryRowsInBmtOrderAsync(rows, [rows[2]], 0, 3); break;
                    default: Assert.Fail("Unknown input"); break;
                }
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "BMT order URL sync gate");
            Assert.IsFalse(operation.IsCompleted);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(db).OpenSongDbReadOnly())
            {
                CollectionAssert.AreEqual(new int?[] { 3, 1, 2 }, verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).Select(table => table.playlist_id).ToArray());
            }
            Assert.ThrowsException<InvalidOperationException>(() => playlist.AcquirePlaylistMutationLease("order-test-competing"));
            release.TrySetResult();
            await operation;
            CollectionAssert.AreEqual(new int?[] { 3, 1, 2 }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId).ToArray());
            CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged", "bemusicseeker://playlist/3", "bemusicseeker://playlist/1", "bemusicseeker://playlist/2" },
                BeatorajaConfigService.ReadTableUrls(root).ToArray());
            foreach (string file in files)
            {
                CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
            long generation = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            await workspace.ApplyCurrentVisibleBmtOrderAsync(workspace.PlaylistSummaryView.ToArray());
            Assert.AreEqual(generation, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            foreach (string file in files)
            {
                CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            release.TrySetResult();
            try { if (operation != null) { await operation; } }
            finally
            {
                try
                {
                    if (workspace != null)
                    {
                        workspace.StopPlaylistSummaryDataBuild();
                        await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                    }
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
        }
    }

    /// <summary>出力失敗のpath・詳細をworkspaceの受信境界へ渡し、warning receiptを既存表示通知へ配送して失敗情報の欠落を検出します。</summary>
    [TestMethod]
    public void BmtOutputFailure_PublishesIndependentNotificationReceipt()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        PlaylistOperationNotificationPresentationRequestedEventArgs? observed = null;
        workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => observed = request;
        workspace.ReportBmtOutputFailures(Array.AsReadOnly(new[]
        {
            new BmtTableExportService.FileOperationFailure("owned-output/locked.bmt", "sharing-denied")
        }));
        Assert.IsNotNull(observed);
        PlaylistOperationNotificationOwner.OperationNotification notification = observed!.Receipt.Notifications.Single();
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, notification.Severity);
        StringAssert.Contains(notification.Message, "owned-output/locked.bmt");
        StringAssert.Contains(notification.Message, "sharing-denied");
    }

    /// <summary>代表順序入口を実configの解析・読取り・置換失敗へ接続し、確定DB、実通知中のP、出力保全と修復後の明示操作を保証します。</summary>
    [DataTestMethod]
    // corruptはJSON解析、readは共有拒否の読込、replaceは読める共有lock下の置換失敗です。
    [DataRow("corrupt")]
    [DataRow("read")]
    [DataRow("replace")]
    public async Task BmtOrderConfigFailure_KeepsCommittedOrderAndAdmissionThroughNotification(string fault)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspacePersistenceCommandTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task? operation = null;
        FileStream? heldConfig = null;
        bool failureObserved = false;
        Exception? primaryFailure = null;
        try
        {
            (TestBmsPlaylist Playlist, PlaylistWorkspaceViewModel Workspace, string DatabasePath, string ConfigPath, string OutputPath) fixture = await CreateBmtOrderFailureFixtureAsync(directory, outputEnabled: true);
            playlist = fixture.Playlist;
            workspace = fixture.Workspace;
            string originalConfig = File.ReadAllText(fixture.ConfigPath);
            string[] files = Directory.GetFiles(fixture.OutputPath);
            Dictionary<string, byte[]> bytes = files.ToDictionary(path => path, File.ReadAllBytes);
            Dictionary<string, DateTime> times = files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
            List<string> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                notifications.Add(request.Receipt.Notifications.Single().Message);
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            };
            if (fault == "corrupt") { File.WriteAllText(fixture.ConfigPath, "{"); }
            else { heldConfig = new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, fault == "read" ? FileShare.None : FileShare.ReadWrite); }
            operation = workspace.MoveSummaryRowsToBmtTopAsync([workspace.PlaylistSummaryView.Single(row => row.PlaylistId == 3)]);
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "BMT config failure notification");
            Assert.IsFalse(operation.IsCompleted);
            Assert.ThrowsException<InvalidOperationException>(() => playlist.AcquirePlaylistMutationLease("config-failure-competing"));
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabasePath).OpenSongDbReadOnly())
            {
                CollectionAssert.AreEqual(new int?[] { 3, 1, 2 }, verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).Select(table => table.playlist_id).ToArray());
            }
            release.TrySetResult();
            PlaylistMutationPostCommitException failure = await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(() => operation);
            failureObserved = true;
            Assert.IsInstanceOfType(failure.GetBaseException(), fault == "corrupt" ? typeof(Newtonsoft.Json.JsonReaderException) : typeof(IOException));
            Assert.AreEqual(1, notifications.Count);
            StringAssert.Contains(notifications[0], fixture.ConfigPath);
            StringAssert.Contains(notifications[0], failure.GetBaseException().Message);
            foreach (string file in files)
            {
                CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
            await playlist.WaitForPlaylistMutationIdleAsync();
            heldConfig?.Dispose();
            heldConfig = null;
            Assert.AreEqual(fault == "corrupt" ? "{" : originalConfig, File.ReadAllText(fixture.ConfigPath));
            var repaired = Newtonsoft.Json.Linq.JObject.Parse(originalConfig);
            var repairedUrls = repaired["tableURL"] as Newtonsoft.Json.Linq.JArray;
            Assert.IsNotNull(repairedUrls);
            repairedUrls.Insert(1, "https://example.com/repaired-unmanaged");
            File.WriteAllText(fixture.ConfigPath, repaired.ToString());
            operation = workspace.MoveSummaryRowsToBmtTopAsync([workspace.PlaylistSummaryView.Single(row => row.PlaylistId == 2)]);
            await operation;
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabasePath).OpenSongDbReadOnly())
            {
                CollectionAssert.AreEqual(new int?[] { 2, 3, 1 }, verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).Select(table => table.playlist_id).ToArray());
            }
            var actualUrls = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(fixture.ConfigPath))["tableURL"] as Newtonsoft.Json.Linq.JArray;
            Assert.IsNotNull(actualUrls);
            CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged", "https://example.com/repaired-unmanaged", "bemusicseeker://playlist/2", "bemusicseeker://playlist/3", "bemusicseeker://playlist/1" }, actualUrls.Values<string>().ToArray());
            Assert.AreEqual(1, notifications.Count);
            foreach (string file in files)
            {
                CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally
        {
            release.TrySetResult();
            try
            {
                try { if (operation != null) { await operation; } }
                catch (Exception cleanupFailure) when (primaryFailure != null || failureObserved)
                {
                    Trace.WriteLine("BMT設定失敗テストの操作回収: " + cleanupFailure);
                }
                try
                {
                    if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
                    if (workspace != null)
                    {
                        workspace.StopPlaylistSummaryDataBuild();
                        await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                    }
                }
                finally { heldConfig?.Dispose(); Directory.Delete(directory, recursive: true); }
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            {
                Trace.WriteLine("BMT設定失敗テストの資源回収: " + cleanupFailure);
            }
        }
    }

    /// <summary>保持falseでも無効の通常順序編集は実config・台帳の読取不能へ依存せず、DBだけを保存し通知・出力変更を行わないことを保証します。</summary>
    [TestMethod]
    public async Task BmtOrderDisabled_SavesOrderWithoutReadingLockedConfigOrManifest()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspacePersistenceCommandTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        FileStream? heldConfig = null;
        FileStream? heldManifest = null;
        Task? operation = null;
        Exception? primaryFailure = null;
        try
        {
            (TestBmsPlaylist Playlist, PlaylistWorkspaceViewModel Workspace, string DatabasePath, string ConfigPath, string OutputPath) fixture = await CreateBmtOrderFailureFixtureAsync(directory, outputEnabled: false);
            playlist = fixture.Playlist;
            workspace = fixture.Workspace;
            string manifest = Path.Combine(fixture.OutputPath, BmtTableExportService.ManifestFileName);
            string[] files = Directory.GetFiles(fixture.OutputPath);
            Dictionary<string, byte[]> bytes = files.ToDictionary(path => path, File.ReadAllBytes);
            Dictionary<string, DateTime> times = files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
            byte[] configBytes = File.ReadAllBytes(fixture.ConfigPath);
            DateTime configTime = File.GetLastWriteTimeUtc(fixture.ConfigPath);
            int notifications = 0;
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => notifications++;
            heldConfig = new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);
            heldManifest = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);
            operation = workspace.MoveSummaryRowsToBmtTopAsync([workspace.PlaylistSummaryView.Single(row => row.PlaylistId == 3)]);
            await operation;
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabasePath).OpenSongDbReadOnly())
            {
                CollectionAssert.AreEqual(new int?[] { 3, 1, 2 }, verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).Select(table => table.playlist_id).ToArray());
            }
            Assert.AreEqual(0, notifications);
            foreach (string file in files)
            {
                byte[] actual = new byte[bytes[file].Length];
                if (file == manifest) { heldManifest.ReadExactly(actual); }
                else { actual = File.ReadAllBytes(file); }
                CollectionAssert.AreEqual(bytes[file], actual);
                Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
            }
            byte[] actualConfig = new byte[configBytes.Length];
            heldConfig.ReadExactly(actualConfig);
            CollectionAssert.AreEqual(configBytes, actualConfig);
            Assert.AreEqual(configTime, File.GetLastWriteTimeUtc(fixture.ConfigPath));
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally
        {
            try
            {
                try
                {
                    try { if (operation != null) { await operation; } }
                    catch (Exception cleanupFailure) when (primaryFailure != null)
                    {
                        Trace.WriteLine("無効BMTテストの操作回収: " + cleanupFailure);
                    }
                    if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }
                    if (workspace != null)
                    {
                        workspace.StopPlaylistSummaryDataBuild();
                        await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                    }
                }
                finally { heldManifest?.Dispose(); heldConfig?.Dispose(); Directory.Delete(directory, recursive: true); }
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            {
                Trace.WriteLine("無効BMTテストの資源回収: " + cleanupFailure);
            }
        }
    }

    /// <summary>設定失敗と無効操作の判定に共用する、実SQLite・gzip・台帳・configと本番通知接続を持つ三表を準備します。</summary>
    private static async Task<(TestBmsPlaylist Playlist, PlaylistWorkspaceViewModel Workspace, string DatabasePath, string ConfigPath, string OutputPath)> CreateBmtOrderFailureFixtureAsync(string directory, bool outputEnabled)
    {
        string db = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
        PlaylistPersistenceRepository.EnsureSchema(db);
        string root = Path.Combine(directory, "beatoraja");
        string output = Path.Combine(root, "table");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        string config = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
        File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
        var options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
        BMSTable[] tables = Enumerable.Range(1, 3).Select(id => new BMSTable
        {
            playlist_id = id,
            name = "Table " + id,
            bmt_sort = id,
            last_update = new DateTime(2024, 1, 1),
            Folder_order = ["Folder"],
            entries = [new BMSTableEntry { title = "Song " + id, md5 = new string((char)('a' + id), 32), folder = "Folder" }]
        }).ToArray();
        var playlist = new TestBmsPlaylist(db, null, null, null,
            () => new PlaylistUrlCompletionOptionsSnapshot(), () => options, () => new CustomFolderOutputSettingsSnapshot(),
            BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(db, CustomFolderOutputPhysicalSurface.Empty))
        { BMSTables = new ObservableCollection<BMSTable>(tables) };
        playlist.CommitBMSTablesWithEntriesToDB(tables);
        await playlist.BmtOutput.ExportAllAsync("config-failure-initial");
        File.WriteAllText(Path.Combine(output, "unmanaged.bmt"), "unmanaged-content");
        options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = outputEnabled, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true, KeepBeatorajaBmtFilesWhenOutputDisabled = false };
        var library = new TestBmsLibrary(db);
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
            playlistLibraryProvider: () => library,
            playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables));
        workspace.RefreshPlaylistTreeTables(playlist, library);
        workspace.IsPlaylistSummaryMode = true;
        workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
        workspace.RebuildPlaylistSummaryView(runAsync: false);
        playlist.BmtOutput.FailureReporter = workspace.ReportBmtOutputFailures;
        return (playlist, workspace, db, config, output);
    }

    /// <summary>実backupを別DBへ復元して日本語名を保持し、未接続・空集合の警告と書込み失敗のerrorを区別します。通知先が失敗しても確定bytesを保持し、失敗を再送出せず既存backupを保護します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceBackupPlaylistAsync_PublishesSuccessWarningAndFailureReceipts()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> unavailableNotifications = [];
            unavailableWorkspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => unavailableNotifications.Add(request);
            string unavailablePath = Path.Combine(tempDirectory, "unavailable-backup.sql");

            await unavailableWorkspace.BackupPlaylistAsync(unavailablePath);

            Assert.IsFalse(File.Exists(unavailablePath));
            Assert.AreEqual(1, unavailableNotifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                unavailableNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                unavailableNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup);

            string successDbDirectory = Path.Combine(tempDirectory, "success");
            Directory.CreateDirectory(successDbDirectory);
            string successDbPath = Path.Combine(successDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(successDbPath))
            {
            }
            BMSPlaylist successPlaylist;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> successNotifications;
            PlaylistWorkspaceViewModel successWorkspace = CreateBackupWorkspace(
                successDbPath,
                [new BMSTable { playlist_id = 1, name = "バックアップ", symbol = "B", bmt_sort = 1 }],
                out successPlaylist,
                out successNotifications);
            successPlaylist.CommitBMSTableHeaderToDB(successPlaylist.BMSTables.Single());
            string successPath = Path.Combine(tempDirectory, "backup.sql");

            await successWorkspace.BackupPlaylistAsync(successPath);

            Assert.IsTrue(File.Exists(successPath));
            byte[] backupBytes = File.ReadAllBytes(successPath);
            Assert.IsFalse(backupBytes.Length >= 3
                && backupBytes[0] == 0xEF
                && backupBytes[1] == 0xBB
                && backupBytes[2] == 0xBF);
            string backupDump = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(backupBytes);
            string roundTripDbDirectory = Path.Combine(tempDirectory, "round-trip");
            Directory.CreateDirectory(roundTripDbDirectory);
            string roundTripDbPath = Path.Combine(roundTripDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(roundTripDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(roundTripDbPath);
            new PlaylistPersistenceRepository(roundTripDbPath).LoadPlaylistDump(backupDump);
            using (LR2SongDBExtended verifyRoundTrip = new BmsLibraryDbGateway(roundTripDbPath).OpenSongDbReadOnly())
            {
                BMSTable restored = verifyRoundTrip.Table<BMSTable>().Single();
                Assert.AreEqual("バックアップ", restored.name);
                Assert.AreEqual("B", restored.symbol);
            }
            Assert.AreEqual(1, successNotifications.Count);
            Assert.AreEqual("playlist backup notification", successNotifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                successNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                successNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup);

            EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> brokenSubscriber =
                (_, _) => throw new InvalidOperationException("backup notification subscriber failed");
            successWorkspace.PlaylistOperationNotificationPresentationRequested += brokenSubscriber;
            string notifiedSuccessPath = Path.Combine(tempDirectory, "notified-success.sql");
            await successWorkspace.BackupPlaylistAsync(notifiedSuccessPath);
            CollectionAssert.AreEqual(backupBytes, File.ReadAllBytes(notifiedSuccessPath));
            Assert.AreEqual(2, successNotifications.Count);
            successWorkspace.PlaylistOperationNotificationPresentationRequested -= brokenSubscriber;

            string previousBackupPath = Path.Combine(tempDirectory, "previous-backup.sql");
            byte[] previousBackupBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("previous backup bytes");
            File.WriteAllBytes(previousBackupPath, previousBackupBytes);
            using (var lockedDestination = new FileStream(
                previousBackupPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                await successWorkspace.BackupPlaylistAsync(previousBackupPath);
            }
            CollectionAssert.AreEqual(previousBackupBytes, File.ReadAllBytes(previousBackupPath));
            Assert.AreEqual(3, successNotifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                successNotifications[2].Receipt.Notifications.Single().Severity);

            string emptyDbDirectory = Path.Combine(tempDirectory, "empty");
            Directory.CreateDirectory(emptyDbDirectory);
            string emptyDbPath = Path.Combine(emptyDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(emptyDbPath))
            {
            }
            BMSPlaylist emptyPlaylist;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> emptyNotifications;
            PlaylistWorkspaceViewModel emptyWorkspace = CreateBackupWorkspace(
                emptyDbPath,
                [],
                out emptyPlaylist,
                out emptyNotifications);
            string emptyPath = Path.Combine(tempDirectory, "empty-backup.sql");

            await emptyWorkspace.BackupPlaylistAsync(emptyPath);

            Assert.IsFalse(File.Exists(emptyPath));
            Assert.AreEqual(1, emptyNotifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                emptyNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                emptyNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup);

            string failureDbDirectory = Path.Combine(tempDirectory, "failure");
            Directory.CreateDirectory(failureDbDirectory);
            string failureDbPath = Path.Combine(failureDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(failureDbPath))
            {
            }
            BMSPlaylist failurePlaylist;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> failureNotifications;
            PlaylistWorkspaceViewModel failureWorkspace = CreateBackupWorkspace(
                failureDbPath,
                [new BMSTable { playlist_id = 2, name = "Failure", symbol = "F", bmt_sort = 1 }],
                out failurePlaylist,
                out failureNotifications);
            string invalidPath = Path.Combine(tempDirectory, "missing", "backup.sql");

            await failureWorkspace.BackupPlaylistAsync(invalidPath);

            Assert.IsFalse(File.Exists(invalidPath));
            Assert.AreEqual(1, failureNotifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failureNotification =
                failureNotifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                failureNotification.Severity);
            StringAssert.Contains(
                failureNotification.Message,
                BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup);
            StringAssert.Contains(failureNotification.Message, "missing");
            Assert.IsFalse(failureNotifications.Any(request => request.Receipt.Notifications.Any(
                notification => notification.Message.IndexOf(
                    BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup,
                    StringComparison.Ordinal) >= 0)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実SQL復元を正本・本番ツリー受信・必須hydration・LR2/BMT出力へ通し、新readerが出力を補正する前に観測します。続く新正本の明示編集保存と新readonly読取り、別表・song・利用者表・管理外ファイルの保全を確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? property = null;
        Task? restore = null;
        var auxiliary = new ConcurrentQueue<Task>();
        Exception? primaryFailure = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.Execute("INSERT INTO playlist (playlist_id, name, symbol) VALUES (1, 'Before restore', 'before');");
                    db.Execute("INSERT INTO song(path, hash, title) VALUES ('unrelated.bms', 'cccccccccccccccccccccccccccccccc', 'kept song');");
                    db.Execute("CREATE TABLE user_data(key TEXT PRIMARY KEY, value TEXT);");
                    db.Execute("INSERT INTO user_data VALUES ('custom', 'kept user value');");
                });
            }
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(tempDirectory, "LR2");
            LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            string outputBase = Path.Combine(tempDirectory, "custom");
            string bmtRoot = Path.Combine(tempDirectory, "beatoraja");
            string bmtOutput = Path.Combine(bmtRoot, "table");
            Directory.CreateDirectory(bmtOutput);
            File.WriteAllBytes(Path.Combine(bmtRoot, "beatoraja.jar"), []);
            File.WriteAllText(Path.Combine(bmtRoot, BeatorajaConfigService.ConfigFileName), "{\"tablepath\":\"table\",\"other\":42,\"tableURL\":[\"https://example.com/unmanaged\"]}");
            string unmanaged = Path.Combine(bmtOutput, "unmanaged.bmt");
            File.WriteAllBytes(unmanaged, [4, 2, 8]);
            string unrelated = Path.Combine(tempDirectory, "unrelated.txt");
            File.WriteAllBytes(unrelated, [9, 6, 1]);
            var outputSettings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2Root,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "root-output")
            };
            var bmtSettings = new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = true,
                BeatorajaRootPath = bmtRoot,
                RegisterBeatorajaBmtUrls = true
            };
            playlist = new TestBmsPlaylist(songDbPath, configOwner, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => bmtSettings, () => outputSettings,
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            { BMSTables = new ObservableCollection<BMSTable>([new BMSTable { playlist_id = 1, name = "Before restore", symbol = "before" }]) };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { auxiliary.Enqueue(Task.Run(work)); return true; };
            library = new TestBmsLibrary(songDbPath);
            TestBmsPlaylist accepted = playlist;
            var propertyService = new PlaylistPropertySaveService(() => accepted, () => library, () => null, () => outputSettings);
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => accepted, playlistLibraryProvider: () => library,
                propertySaveService: propertyService,
                referenceApplyScheduler: (_, work) => { auxiliary.Enqueue(Task.Run(work)); return true; },
                dispatchPresentation: action => TestUiDispatcherHost.Invoke(action),
                catalogNotificationQueue: action => TestUiDispatcherHost.Invoke(action),
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => accepted, () => accepted.BMSTables));

            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
            workspace.RebuildPlaylistSummaryView(runAsync: false);
            var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(workspace.PlaylistTreeTables)
                    && workspace.PlaylistTreeTables.Any(table => table.playlist_id == 2)) { replaced.TrySetResult(); }
            };
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            string backupPath = Path.Combine(tempDirectory, "restore.sql");
            int flags = (int)(LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder);
            string[] commands =
            [
                CreatePlaylistRestoreDump(2, "After restore", "after").Split('\v')[0],
                CreatePlaylistRestoreDump(3, "Other restored", "other").Split('\v')[0],
                "UPDATE playlist SET output_dir = 'RestoredA', ignore_folder_output = " + flags + ", is_bmt_output = 1, folder_order = '[\"Folder A\"]', bmt_sort = 0 WHERE playlist_id = 2;",
                "UPDATE playlist SET output_dir = 'RestoredB', ignore_folder_output = " + flags + ", is_bmt_output = 1, folder_order = '[\"Folder B\"]', bmt_sort = 1 WHERE playlist_id = 3;",
                "INSERT INTO playlist_entry(playlist_id, md5, title, artist, folder) VALUES (2, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Restored song A', 'Artist A', 'Folder A');",
                "INSERT INTO playlist_entry(playlist_id, md5, title, artist, folder) VALUES (3, 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'Restored song B', 'Artist B', 'Folder B');"
            ];
            File.WriteAllText(backupPath, string.Join("\v" + Environment.NewLine, commands), Encoding.UTF8);
            Assert.IsFalse(Directory.Exists(outputBase));
            restore = workspace.RestorePlaylistBackupAsync(backupPath);
            await restore;
            await TestUiDispatcherHost.AwaitNotificationAsync(replaced.Task, restore, "restored tree replacement");
            await playlist.StartupReadiness.WaitForRequiredPlaylistReadinessAsync();
            await playlist.WaitForPlaylistMutationIdleAsync();
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            while (auxiliary.TryDequeue(out Task? work)) { await work; }
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);

            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual("playlist restore notification", notifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                notifications[0].Receipt.Notifications.Single().Severity);
            CollectionAssert.AreEquivalent(new[] { "After restore", "Other restored" }, workspace.PlaylistTreeTables.Select(table => table.name).ToArray());
            CollectionAssert.AreEquivalent(new[] { 2, 3 }, workspace.PlaylistSummaryView.Select(row => row.PlaylistId ?? 0).ToArray());
            foreach ((int id, string directory, string folderName, string hash, string title) in new[]
            {
                (2, "RestoredA", "Folder A", new string('a', 32), "Restored song A"),
                (3, "RestoredB", "Folder B", new string('b', 32), "Restored song B")
            })
            {
                BMSTable current = playlist.BMSTables.Single(table => table.playlist_id == id);
                Assert.AreEqual(id, current.entries.Single().playlist_id);
                Assert.AreEqual(hash, current.entries.Single().md5);
                Assert.IsTrue(current.FolderNodes.Any(node => node.FolderName == folderName));
                string lr2 = Path.Combine(outputBase, directory, "0001.lr2folder");
                StringAssert.Contains(File.ReadAllText(lr2, Encoding.GetEncoding("shift_jis")), "#TITLE " + folderName);
                using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                { Assert.AreEqual(folderName, verify.Table<LR2SongDB.folder>().Single(row => row.path == lr2).title); }
                string bmt = Directory.GetFiles(bmtOutput, "*.bmt").Where(path => path != unmanaged).Single(path =>
                {
                    using var compressed = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
                    using var reader = new StreamReader(compressed);
                    return Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).Value<string>("name") == current.name;
                });
                using var content = new GZipStream(File.OpenRead(bmt), CompressionMode.Decompress);
                using var text = new StreamReader(content);
                var document = Newtonsoft.Json.Linq.JObject.Parse(text.ReadToEnd());
                Newtonsoft.Json.Linq.JToken song = document.SelectToken("folder[0].songs[0]") ?? throw new AssertFailedException("restored BMT entry missing");
                Assert.AreEqual(hash, song.Value<string>("md5"));
                Assert.AreEqual(title, song.Value<string>("title"));
            }
            // ここまでの出力を観測してから新readerを作るため、初期化による修復で復元不良を隠さない。
            BMSTable restoredA = playlist.BMSTables.Single(table => table.playlist_id == 2);
            property = await workspace.OpenPropertyDialogAsync(restoredA) ?? throw new AssertFailedException("restored property dialog unavailable");
            property.symbol = "edited-after-restore";
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await property.SaveAndApplyAsync());
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable restored = verify.Table<BMSTable>().Single(table => table.playlist_id == 2);
                Assert.AreEqual("After restore", restored.name);
                Assert.AreEqual("edited-after-restore", restored.symbol);
                Assert.AreEqual("other", verify.Table<BMSTable>().Single(table => table.playlist_id == 3).symbol);
                Assert.AreEqual(2, verify.Table<BMSTableEntry>().Single(entry => entry.md5 == new string('a', 32)).playlist_id);
                Assert.AreEqual("kept song", verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = 'unrelated.bms';"));
                Assert.AreEqual("kept user value", verify.ExecuteScalar<string>("SELECT value FROM user_data WHERE key = 'custom';"));
            }
            CollectionAssert.AreEqual(new byte[] { 4, 2, 8 }, File.ReadAllBytes(unmanaged));
            CollectionAssert.AreEqual(new byte[] { 9, 6, 1 }, File.ReadAllBytes(unrelated));
            Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (restore != null) { await CleanupAsync(() => restore); }
            if (playlist != null) { await CleanupAsync(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null) { await CleanupAsync(workspace.WaitForPlaylistSummaryDataBuildIdleAsync); }
            while (auxiliary.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { workspace?.RefreshPlaylistTreeTables(null); return Task.CompletedTask; });
            while (auxiliary.TryDequeue(out Task? work)) { await CleanupAsync(() => work); }
            await CleanupAsync(() => { property?.Dispose(); return Task.CompletedTask; });
            await CleanupAsync(() => { playlist?.RequestShutdown("normal-restore-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { library?.RequestShutdown("normal-restore-fixture"); return Task.CompletedTask; });
            await CleanupAsync(() => { if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); } return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = new AggregateException("normal restore cleanup", cleanupFailures);
                if (primaryFailure == null) { throw cleanupFailure; }
                Console.WriteLine(cleanupFailure);
            }
        }
    }

    // DB Monitor の再取得は process-wide な所有確認のため、他 fixture の DB 保持から隔離する。
    /// <summary>実DB復元確定、UI集合適用、UI operation完了を別gateで観測し、待機中のDB lock解放とP保持、競合変更拒否、完了後の次保存を確認します。任意URL補完の予約が失敗しても必須復元の成功を取り消しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_AwaitsUiCompletionAndRejectsCompetingWrites(bool failOptionalScheduling)
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-restore-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? restore = null;
        int scheduleCount = 0;
        var scheduledApplies = new List<Task>();
        Task? registration = null;
        Task? competingRestore = null;
        bool assertionsCompleted = false;

        async Task ApplyAsync(Action action)
        {
            await execute.Task;
            action();
            executed.TrySetResult();
            await complete.Task;
        }
        try
        {
            string dbPath = Path.Combine(directory, "song.db");
            using (var _ = new LR2SongDBExtended(dbPath)) { }
            var oldTable = new BMSTable { playlist_id = 11, name = "Old live", symbol = "O" };
            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                dbPath, [oldTable], out BMSPlaylist playlist, out List<PlaylistOperationNotificationPresentationRequestedEventArgs>? notifications,
                action =>
                {
                    Interlocked.Increment(ref scheduleCount);
                    Task task = ApplyAsync(action);
                    lock (scheduledApplies) { scheduledApplies.Add(task); }
                    accepted.TrySetResult();
                    return task;
                });
            int optionalSchedulingCalls = 0;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref optionalSchedulingCalls);
                if (failOptionalScheduling) { throw new IOException("restore optional URL completion scheduling failed"); }
                return false;
            };
            bool notificationAfterAdmissionRelease = false;
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) =>
            {
                notificationAfterAdmissionRelease = playlist.TryEnterPlaylistMutation(out IDisposable notificationLease);
                notificationLease?.Dispose();
            };
            bool collectionOnUi = true;
            playlist.BMSTables.CollectionChanged += (_, _) =>
                collectionOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess();
            string file = Path.Combine(directory, "restore.sql");
            File.WriteAllText(file, CreatePlaylistRestoreDump(22, "Restored live", "R"), Encoding.UTF8);
            restore = workspace.RestorePlaylistBackupAsync(file);
            await TestUiDispatcherHost.AwaitNotificationAsync(accepted.Task, restore, "playlist.restore-ui-accepted");
            Assert.IsFalse(restore.IsCompleted);
            Assert.AreSame(oldTable, playlist.BMSTables.Single());
            Assert.AreEqual(0, notifications.Count);
            bool lockAvailable = await Task.Run(() =>
            {
                bool acquired = LR2SongDBExtended.Lock(TimeSpan.FromSeconds(1));
                if (acquired) { LR2SongDBExtended.Unlock(); }
                return acquired;
            });
            Assert.IsTrue(lockAvailable, "UI completion 待機へ DB Monitor を持ち越さない。");
            using (var db = new LR2SongDBExtended(dbPath))
            {
                Assert.AreEqual(22, db.Table<BMSTable>().Single().playlist_id);
            }
            Assert.ThrowsException<InvalidOperationException>(() => playlist.CommitBMSTableHeaderToDB(oldTable));
            Assert.ThrowsException<InvalidOperationException>(() => playlist.RemoveBMSTable(oldTable));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => playlist.ReloadTablesAsync());
            registration = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(
                new BMSTable { name = "Rejected registration", Output_dir = "RejectedRegistration" },
                renameDuplicateName: false, reason: "restore_exclusion");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                registration);
            competingRestore = playlist.RestorePlaylistDumpAsync(CreatePlaylistRestoreDump(33, "Rejected", "X"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                competingRestore);
            execute.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(executed.Task, restore, "playlist.restore-ui-executed");
            Assert.AreEqual("Restored live", playlist.BMSTables.Single().name);
            Assert.IsTrue(collectionOnUi);
            Assert.IsFalse(restore.IsCompleted);
            Assert.AreEqual(0, notifications.Count);
            Assert.ThrowsException<InvalidOperationException>(() =>
                playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single()));
            complete.TrySetResult();
            Assert.IsTrue(await restore);
            Assert.IsTrue(notificationAfterAdmissionRelease, "結果通知へPを持ち越さない。");
            Assert.AreEqual(1, scheduleCount);
            Assert.AreEqual(1, optionalSchedulingCalls, "任意予約を再試行しない。");
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                notifications.Single().Receipt.Notifications.Single().Severity);
            playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single());
            assertionsCompleted = true;
        }
        finally
        {
            execute.TrySetResult();
            complete.TrySetResult();
            // 誤実装が競合 request を受理しても、先に全 gate を開き、その operation と callback を回収する。
            foreach (Task task in new[] { restore, registration, competingRestore }.OfType<Task>())
            {
                try { await task; }
                catch (InvalidOperationException) when (task == registration || task == competingRestore) { }
                catch when (!assertionsCompleted) { }
            }
            Task[] applies;
            lock (scheduledApplies) { applies = scheduledApplies.ToArray(); }
            foreach (Task apply in applies)
            {
                try { await apply; }
                catch when (!assertionsCompleted) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    // 実process lockの所有workerをfinallyで解放し、時間指定は停止監視にのみ使います。
    /// <summary>別workerの実DB lock待機中にUIから復元を開始して未完了Taskを返し、dispatcherの次処理が実行できてlock解放後に復元を完了します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistRestore_DatabaseLockWaitKeepsDispatcherResponsive()
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-restore-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var releaseLock = new ManualResetEventSlim();
        var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lockOwner = null;
        Task? restore = null;
        Task? dispatch = null;
        try
        {
            string dbPath = Path.Combine(directory, "song.db");
            using (var _ = new LR2SongDBExtended(dbPath)) { }
            CreateBackupWorkspace(dbPath, [], out BMSPlaylist playlist, out _);
            lockOwner = Task.Run(() =>
            {
                bool acquired = LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5));
                try
                {
                    if (!acquired) { throw new TimeoutException("Test lock acquisition failed."); }
                    lockEntered.TrySetResult();
                    releaseLock.Wait();
                }
                finally { if (acquired) { LR2SongDBExtended.Unlock(); } }
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(lockEntered.Task, lockOwner, "playlist-restore.lock-owner");
            // 新 async owner API は admission を含む同期 DB 前半を worker へ渡す。
            dispatch = TestUiDispatcherHost.Dispatcher.InvokeAsync(() =>
            {
                restore = playlist.RestorePlaylistDumpAsync(CreatePlaylistRestoreDump(44, "Worker DB", "W"));
                Assert.IsFalse(restore.IsCompleted);
            }).Task;
            await dispatch;
            bool marker = false;
            await TestUiDispatcherHost.Dispatcher.InvokeAsync(() => marker = true).Task;
            Assert.IsTrue(marker);
            Assert.IsFalse(restore!.IsCompleted);
            releaseLock.Set();
            await lockOwner;
            await restore;
            Assert.AreEqual("Worker DB", playlist.BMSTables.Single().name);
        }
        finally
        {
            releaseLock.Set();
            try
            {
                await Task.WhenAll(lockOwner ?? Task.CompletedTask, dispatch ?? Task.CompletedTask, restore ?? Task.CompletedTask);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    /// <summary>DB確定後のUI所属検査を失敗させ、確定DBと旧表示集合を区別し、一回のerror通知と終了不可の結果を返してDB lockを残しません。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_UiApplyFailureKeepsCommittedDatabaseAndOldCollection()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES (1, 'Before background restore', 'before', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);");
            }

            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                songDbPath,
                [new BMSTable { playlist_id = 1, name = "Before background restore", symbol = "before" }],
                out BMSPlaylist _,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                action => Task.Run(action),
                restoreUiThreadCheck: () => false);
            string backupPath = Path.Combine(tempDirectory, "restore.sql");
            File.WriteAllText(backupPath, CreatePlaylistRestoreDump(2, "Should not apply", "rejected"), Encoding.UTF8);

            Assert.IsFalse(await workspace.RestorePlaylistBackupAsync(backupPath));

            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable existing = verify.Table<BMSTable>().Single();
                Assert.AreEqual(2, existing.playlist_id);
                Assert.AreEqual("Should not apply", existing.name);
            }
            Assert.AreEqual("Before background restore", workspace.CapturePlaylistTreeTablesSnapshot().Single().name);
            Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>途中まで有効なSQL dumpの後半を失敗させ、実DBの表・行・courseと旧モデル参照を全て保持し、失敗通知後の次保存を受理します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_RollsBackInvalidDumpAndReleasesLock()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES (1, 'Keep on failure', 'keep', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);");
            }

            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                songDbPath,
                [new BMSTable { playlist_id = 1, name = "Keep on failure", symbol = "keep" }],
                out BMSPlaylist playlist,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications);
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.Execute("INSERT INTO playlist_entry (playlist_id, md5, title) VALUES (1, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Old entry');");
                seed.Execute("INSERT INTO playlist_course (playlist_id, course_order, course_json) VALUES (1, 0, '{old-course}');");
            }
            BMSTable oldLive = playlist.BMSTables.Single();
            string invalidBackupPath = Path.Combine(tempDirectory, "invalid.sql");
            File.WriteAllText(
                invalidBackupPath,
                CreatePlaylistRestoreDump(2, "Partial replacement", "P") + "\v" + Environment.NewLine + "THIS IS NOT SQL",
                Encoding.UTF8);

            Assert.IsFalse(await workspace.RestorePlaylistBackupAsync(invalidBackupPath));

            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            StringAssert.Contains(failure.Message, BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("Keep on failure", verify.Table<BMSTable>().Single().name);
                Assert.AreEqual("Old entry", verify.Table<LR2SongDBExtended.playlist_entry>().Single().title);
                Assert.AreEqual("{old-course}", verify.Table<LR2SongDBExtended.playlist_course>().Single().course_json);
            }
            Assert.AreSame(oldLive, playlist.BMSTables.Single());
            playlist.CommitBMSTableHeaderToDB(oldLive);
            Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>復元の最終検査を実L/P取得中に呼び、拒否ではdump読取り・DB変更・出力へ進まず、終了不可を返して両受付を解放します。</summary>
    /// <remarks>存在しないdumpと通知0で読取り前の拒否を識別します。設定候補ごとの拒否条件はSettings系列、実復元の成功・必須失敗は隣接ケースが分担します。</remarks>
    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_FinalCheckRefusesInsideAdmissionWithoutSideEffects()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspacePersistenceCommandTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsLibrary? library = null;
        TestBmsPlaylist? playlist = null;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            library = new TestBmsLibrary(songDbPath);
            int customOutputInputs = 0;
            int bmtOutputInputs = 0;
            var outputPort = new TestLr2PlaylistFolderSynchronizationPort(songDbPath);
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => { customOutputInputs++; return new CustomFolderOutputSettingsSnapshot(); },
                beatorajaBmtOptionsProvider: () => { bmtOutputInputs++; return new BeatorajaBmtOptionsSnapshot(); },
                lr2PlaylistFolderSynchronization: outputPort);
            var table = new BMSTable
            {
                playlist_id = 4203,
                name = "Keep on final refusal",
                symbol = "keep",
                entries = [new BMSTableEntry { md5 = new string('a', 32), title = "Keep entry", folder = "Folder" }]
            };
            playlist.BMSTables.Add(table);
            playlist.CommitBMSTableWithEntriesToDB(table);
            workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(playlist, library);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            customOutputInputs = 0;
            bmtOutputInputs = 0;
            outputPort.Operations.Clear();
            int checks = 0;
            bool libraryHeldDuringCheck = false;
            bool playlistHeldDuringCheck = false;
            Task<bool> RefuseBeforeExitAsync()
            {
                checks++;
                libraryHeldDuringCheck = !library.OperationAdmission.TryEnter(out IDisposable libraryLease);
                libraryLease?.Dispose();
                playlistHeldDuringCheck = !playlist.TryEnterPlaylistMutation(out IDisposable playlistLease);
                playlistLease?.Dispose();
                return Task.FromResult(false);
            }
            string missingDump = Path.Combine(directory, "missing.sql");
            Assert.IsFalse(File.Exists(missingDump));

            Assert.IsFalse(await workspace.RestorePlaylistBackupAsync(missingDump, RefuseBeforeExitAsync), "最終検査の拒否では終了を許可しません。");

            Assert.AreEqual(1, checks);
            Assert.IsTrue(libraryHeldDuringCheck, "最終検査は実L取得後に行います。");
            Assert.IsTrue(playlistHeldDuringCheck, "最終検査は実P取得後に行います。");
            Assert.AreEqual(0, notifications.Count, "読込みへ進むと存在しないdumpの失敗通知が発生します。");
            Assert.AreEqual(0, customOutputInputs);
            Assert.AreEqual(0, bmtOutputInputs);
            Assert.AreEqual(0, outputPort.Operations.Count);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTable persisted = verify.Table<BMSTable>().Single();
                Assert.AreEqual(4203, persisted.playlist_id);
                Assert.AreEqual("Keep on final refusal", persisted.name);
                Assert.AreEqual("keep", persisted.symbol);
                Assert.AreEqual("Keep entry", verify.Table<LR2SongDBExtended.playlist_entry>().Single().title);
            }
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.AreSame(table, workspace.CapturePlaylistTreeTablesSnapshot().Single());
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable nextLibraryLease));
            using (nextLibraryLease)
            {
                Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable nextPlaylistLease));
                nextPlaylistLease.Dispose();
            }
        }
        finally
        {
            workspace?.RefreshPlaylistTreeTables(null);
            playlist?.RequestShutdown("restore-final-refusal-fixture");
            library?.RequestShutdown("restore-final-refusal-fixture");
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    /// <summary>backup読取り失敗ではDB・モデル・公開一覧の既存表を保持し、L/P解放後にErrorを一回通知して終了不可を返します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_ReportsReadFailureWithoutAuthorizingExit()
    {
        using PlaylistWorkspaceTestPorts.OwnedPlaylistStore owned = PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore();
        var table = new BMSTable { playlist_id = 4202, name = "Keep on read failure", symbol = "keep" };
        owned.Store.BMSTables.Add(table);
        owned.Store.CommitBMSTableHeaderToDB(table);
        TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(owned.SongDbPath, MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            }));
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => owned.Store,
            playlistLibraryProvider: () => library);
        workspace.RefreshPlaylistTreeTables(owned.Store, library);
        List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
        bool libraryReleasedBeforeNotification = false;
        bool playlistReleasedBeforeNotification = false;
        void CaptureNotification(object? sender, PlaylistOperationNotificationPresentationRequestedEventArgs request)
        {
            notifications.Add(request);
            libraryReleasedBeforeNotification = library.OperationAdmission.TryEnter(out IDisposable libraryLease);
            libraryLease?.Dispose();
            playlistReleasedBeforeNotification = owned.Store.TryEnterPlaylistMutation(out IDisposable playlistLease);
            playlistLease?.Dispose();
        }
        workspace.PlaylistOperationNotificationPresentationRequested += CaptureNotification;
        try
        {
            Assert.AreSame(table, workspace.CapturePlaylistTreeTablesSnapshot().Single());
            string missingPath = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests) + "-" + Guid.NewGuid().ToString("N") + ".sql");

            Assert.IsFalse(await workspace.RestorePlaylistBackupAsync(missingPath));

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(owned.SongDbPath).OpenSongDbReadOnly())
            {
                BMSTable persisted = verify.Table<BMSTable>().Single();
                Assert.AreEqual(4202, persisted.playlist_id);
                Assert.AreEqual("Keep on read failure", persisted.name);
                Assert.AreEqual("keep", persisted.symbol);
            }
            Assert.AreSame(table, owned.Store.BMSTables.Single());
            BMSTable published = workspace.CapturePlaylistTreeTablesSnapshot().Single();
            Assert.AreSame(table, published);
            Assert.AreEqual(4202, published.playlist_id);
            Assert.AreEqual("Keep on read failure", published.name);
            Assert.AreEqual("keep", published.symbol);
            Assert.AreEqual(1, notifications.Count);
            Assert.IsTrue(libraryReleasedBeforeNotification);
            Assert.IsTrue(playlistReleasedBeforeNotification);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications.Single().Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            StringAssert.Contains(failure.Message, BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore);
            StringAssert.Contains(failure.Message, missingPath);
        }
        finally
        {
            workspace.PlaylistOperationNotificationPresentationRequested -= CaptureNotification;
            workspace.RefreshPlaylistTreeTables(null);
        }
    }

    /// <summary>空Data_urlでは実headerから選択dataへ到達し、主要項目・空receiptとシリアライズ中の正本URLを保ちます。同名の別ディレクトリと両保存先の予約文字も区別します。</summary>
    [DataTestMethod]
    [DataRow("header.json", "data.json")]
    [DataRow("header.json", "other/header.json")]
    [DataRow("見出し # %23/header # %23.json", "データ # %23/data # %23.json")]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_WritesJsonAndPreservesDataUrl(string headerRelativePath, string dataRelativePath)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            SerializationObservedTable table = CreateJsonExportTable();
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, headerRelativePath);
            string dataPath = Path.Combine(tempDirectory, dataRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(headerPath) ?? throw new InvalidOperationException());
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath) ?? throw new InvalidOperationException());
            table.DataUrlReadObserver = () => AssertJsonExportSourceUrls(table, null);

            dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, dataPath)));
            await workspace.ExportPlaylistTableAsync(table);

            AssertExportedJsonDataReference(headerPath, dataPath, absoluteFileReference: false);
            AssertJsonExportSourceUrls(table, null);
            Assert.IsTrue(notifications.Single().Receipt.IsEmpty);
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>異なるドライブへ実出力した参照は、rawドライブパスでなく明示file:絶対URIでdata内容へ到達します。ケース所有別名はTask・通知終端後に解除し、物理領域を最後に回収します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_DifferentDriveWritesAbsoluteFileReference()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            // Zは予約・照会・定義の前に除外し、別名の実領域はこのケース配下だけに置きます。
            using var drive = OwnedTestDrive.Create(tempDirectory, 'Z');
            try
            {
                drive.AssertOwned();
                SerializationObservedTable table = CreateJsonExportTable();
                var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
                PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
                List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
                workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
                string headerPath = Path.Combine(tempDirectory, "header.json");
                string dataPath = Path.Combine(drive.Root, "data.json");
                table.DataUrlReadObserver = () => AssertJsonExportSourceUrls(table, null);

                dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, dataPath)));
                await workspace.ExportPlaylistTableAsync(table);

                drive.AssertOwned();
                AssertExportedJsonDataReference(headerPath, dataPath, absoluteFileReference: true);
                AssertJsonExportSourceUrls(table, null);
                Assert.IsTrue(notifications.Single().Receipt.IsEmpty);
            }
            finally { drive.AssertOwned(); }
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>既存の絶対リモート参照と相対参照は選択したローカルdataへ置換せず、既存先の出力置換・主要項目・正本URL・空receiptを保ちます。</summary>
    [DataTestMethod]
    [DataRow("https://example.test/export-data.json")]
    [DataRow("../published/data%23.json")]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PreservesExistingDataReference(string existingDataReference)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            SerializationObservedTable table = CreateJsonExportTable();
            var persistedDataUrl = new Uri(existingDataReference, UriKind.RelativeOrAbsolute);
            table.Data_url = persistedDataUrl;
            table.DataUrlReadObserver = () => AssertJsonExportSourceUrls(table, persistedDataUrl);
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string otherDirectory = Path.Combine(tempDirectory, "other");
            Directory.CreateDirectory(otherDirectory);
            string dataPath = Path.Combine(otherDirectory, "header.json");
            File.WriteAllText(headerPath, "old header");
            File.WriteAllText(dataPath, "old data");

            dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, dataPath)));
            await workspace.ExportPlaylistTableAsync(table);

            var header = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(headerPath));
            var data = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(dataPath));
            Assert.AreEqual(existingDataReference, (string?)header["data_url"]);
            Assert.AreEqual("Export chart", (string?)data[0]["title"]);
            AssertJsonExportSourceUrls(table, persistedDataUrl);
            Assert.IsTrue(notifications.Single().Receipt.IsEmpty);
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>既存先の実Replace失敗と初回の保存失敗で旧内容または不在を保持し、header成功後のdata失敗を部分失敗として通知します。表示・診断故障でも次の明示出力へ進めます。</summary>
    [DataTestMethod]
    // 最初の四条件は旧テストの表示・診断故障の分離、最後は初回出力の不完全ファイル非公開を担います。
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    [DataRow(false, false, false)]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PublishesFileFailureAndPreservesHeaderFirstOrder(
        bool failNotification, bool failDiagnostics, bool existingDestination)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var table = new BMSTable { name = "Export failure", Folder_order = [] };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            var diagnostics = new List<(Exception Exception, string Diagnostic)>();
            var presentationFailure = new InvalidOperationException("JSON export receipt display failed");
            var diagnosticFailure = new InvalidOperationException("JSON export diagnostic failed");
            bool injectNotificationFailure = failNotification;
            bool injectDiagnosticFailure = failDiagnostics;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _,
                reloadFailureLog: (exception, diagnostic) =>
                {
                    diagnostics.Add((exception, diagnostic));
                    if (injectDiagnosticFailure) { throw diagnosticFailure; }
                }, playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                notifications.Add(request);
                if (injectNotificationFailure) { throw presentationFailure; }
            };
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string dataPath = Path.Combine(tempDirectory, existingDestination ? "data.json" : Path.Combine("missing", "data.json"));
            File.WriteAllText(headerPath, "old header");
            if (existingDestination) { File.WriteAllText(dataPath, "old data"); }
            // Delete共有を与えず、通常の直接上書きなら成功してしまう条件でReplace失敗を発生させます。
            using (FileStream? heldData = existingDestination
                ? new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
            {
                dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, dataPath)));
                await workspace.ExportPlaylistTableAsync(table);
            }
            Assert.AreEqual("Export failure", (string?)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(headerPath))["name"]);
            if (existingDestination) { Assert.AreEqual("old data", File.ReadAllText(dataPath)); }
            else { Assert.IsFalse(File.Exists(dataPath)); }
            AssertExportFailure("data", dataPath, BeMusicSeeker.Properties.Resources.Playlist_json_export_partial_failure);

            notifications.Clear();
            diagnostics.Clear();
            string failedHeader = existingDestination ? headerPath : Path.Combine(tempDirectory, "missing", "header.json");
            string untouchedData = Path.Combine(tempDirectory, "untouched-data.json");
            if (existingDestination)
            {
                File.WriteAllText(headerPath, "old header");
                File.WriteAllText(untouchedData, "untouched data");
            }
            using (FileStream? heldHeader = existingDestination
                ? new FileStream(failedHeader, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
            {
                dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(failedHeader, untouchedData)));
                await workspace.ExportPlaylistTableAsync(table);
            }
            if (existingDestination)
            {
                Assert.AreEqual("old header", File.ReadAllText(headerPath));
                Assert.AreEqual("untouched data", File.ReadAllText(untouchedData));
            }
            else
            {
                Assert.IsFalse(File.Exists(failedHeader));
                Assert.IsFalse(File.Exists(untouchedData));
            }
            AssertExportFailure("header", failedHeader, BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist);

            injectNotificationFailure = false;
            injectDiagnosticFailure = false;
            notifications.Clear();
            diagnostics.Clear();
            string freshData = Path.Combine(tempDirectory, "fresh-data.json");
            dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, freshData)));
            await workspace.ExportPlaylistTableAsync(table);
            Assert.AreEqual("Export failure", (string?)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(headerPath))["name"]);
            Assert.AreEqual(0, Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(freshData)).Count);
            Assert.IsNull(table.Data_url);
            Assert.IsTrue(notifications.Single().Receipt.IsEmpty);
            Assert.AreEqual(0, diagnostics.Count);

            void AssertExportFailure(string stage, string path, string message)
            {
                Assert.IsNull(table.Data_url);
                PlaylistOperationNotificationOwner.OperationNotification failure = notifications.Single().Receipt.Notifications.Single();
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
                Assert.AreEqual(message, failure.Message);
                Assert.AreEqual(1 + (failNotification ? 1 : 0), diagnostics.Count);
                Assert.IsInstanceOfType<IOException>(diagnostics[0].Exception);
                Assert.IsNotNull(diagnostics[0].Exception.InnerException, "元のI/O原因を保持します。");
                StringAssert.Contains(diagnostics[0].Diagnostic, "playlist_json_export_write_failed stage=" + stage);
                StringAssert.Contains(diagnostics[0].Diagnostic, "path=" + path);
                if (failNotification)
                {
                    Assert.AreSame(presentationFailure, diagnostics[1].Exception);
                    StringAssert.Contains(diagnostics[1].Diagnostic, "playlist table JSON export notification");
                }
            }
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>専用画面の取消と、Acceptedに混入した同一保存先をWorkspace入口で止め、既存ファイル・DB・正本を変更しません。条件網羅は保存先VMへ分担します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_CancellationAndInvalidAcceptancePreserveState(bool cancelled)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7820,
                name = "Unchanged",
                symbol = "KEEP",
                Folder_order = [],
                Header_url = new Uri("https://example.test/header.json"),
                Data_url = new Uri("https://example.test/data.json"),
                entries = [new BMSTableEntry { md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", title = "Kept entry" }]
            };
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(), null)
            { BMSTables = new ObservableCollection<BMSTable>([table]) };
            playlist.CommitBMSTableWithEntriesToDB(table);
            string beforeHeader = table.HeaderToJson();
            string beforeData = table.DataToJson();
            byte[] beforeDb = File.ReadAllBytes(songDbPath);
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistWorkspaceDialogService: dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string dataPath = Path.Combine(tempDirectory, "data.json");
            File.WriteAllText(headerPath, "existing header");
            File.WriteAllText(dataPath, "existing data");
            dialogs.JsonExportResults.Enqueue(new(cancelled ? UiDialogStatus.CancelledByUser : UiDialogStatus.Accepted,
                new(headerPath, Path.Combine(tempDirectory, ".", "HEADER.JSON"))));

            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual("existing header", File.ReadAllText(headerPath));
            Assert.AreEqual("existing data", File.ReadAllText(dataPath));
            Assert.AreEqual(beforeHeader, table.HeaderToJson());
            Assert.AreEqual(beforeData, table.DataToJson());
            CollectionAssert.AreEqual(beforeDb, File.ReadAllBytes(songDbPath));
            Assert.AreEqual(0, notifications.Count);
            if (!cancelled)
            {
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_json_export_paths_same, dialogs.LastMessageRequest.MessageBoxText);
            }
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>専用画面の表示失敗を元例外付きで返し、ファイルや出力receiptを作りません。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_DialogFailurePropagatesBeforeWriting()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            var table = new BMSTable { name = "Export dialog failure", Folder_order = [] };
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string dataPath = Path.Combine(tempDirectory, "data.json");
            File.WriteAllText(headerPath, "existing header");
            File.WriteAllText(dataPath, "existing data");
            var error = new IOException("export dialog unavailable");
            // 有効なパスが添付されていてもFailedをAcceptedとして使わないことを確認します。
            dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Failed, new(headerPath, dataPath), error: error));
            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.ExportPlaylistTableAsync(table));
            Assert.AreSame(error, failure.InnerException);
            Assert.AreEqual("existing header", File.ReadAllText(headerPath));
            Assert.AreEqual("existing data", File.ReadAllText(dataPath));
            Assert.AreEqual(0, notifications.Count);
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>summary buildの後発開始で先行tokenを取消し、modeを外れたら後発も取消して新buildを拒否し、両完了後にidleへ戻します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCancelsSupersededAndHiddenWork()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        workspace.IsPlaylistSummaryMode = true;

        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest first));
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest second));

        Assert.IsTrue(first.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(second.CancellationToken.IsCancellationRequested);
        Assert.IsTrue(second.Generation > first.Generation);

        workspace.IsPlaylistSummaryMode = false;

        Assert.IsTrue(second.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.TryBeginPlaylistSummaryDataBuild(out _));
        workspace.CompletePlaylistSummaryDataBuild(first);
        workspace.CompletePlaylistSummaryDataBuild(second);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
    }

    /// <summary>重なる二buildのidle待機は先行完了だけで終端せず、後発buildの完了まで待つことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistSummaryDataBuildIdleWaitIncludesEveryOverlappingBuild()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest first));
        Task idle = workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest second));

        workspace.CompletePlaylistSummaryDataBuild(first);
        Assert.IsFalse(idle.IsCompleted);
        workspace.CompletePlaylistSummaryDataBuild(second);

        await idle.ConfigureAwait(false);
    }

    /// <summary>同じ表・所持版の実count cacheは表示data世代が進んでも再利用し、明示count失効後だけ取得を拒否することを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheReusesContentKeyAcrossDataGenerations()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        var expected = new PlaylistSummaryCountResult
        {
            ScannedEntries = 4,
            TotalCharts = 3,
            OwnedCharts = 2
        };
        var table = new BMSTable
        {
            playlist_id = 1
        };
        Assert.IsTrue(workspace.CatalogSummaryOwner.TrySetTableCount(
            table,
            ownedSnapshotVersion: 1,
            countResult: expected,
            expectedGeneration: workspace.CatalogSummaryOwner.TableCountCacheGeneration));

        workspace.BeginPlaylistSummaryDataRebuildGeneration();
        workspace.BeginPlaylistSummaryDataRebuildGeneration();

        Assert.IsTrue(workspace.CatalogSummaryOwner.TryGetTableCount(table, ownedSnapshotVersion: 1, out PlaylistSummaryCountResult actual));
        Assert.AreEqual(expected.ScannedEntries, actual.ScannedEntries);
        Assert.AreEqual(expected.TotalCharts, actual.TotalCharts);
        Assert.AreEqual(expected.OwnedCharts, actual.OwnedCharts);

        workspace.CatalogSummaryOwner.InvalidateTableCounts();
        Assert.IsFalse(workspace.CatalogSummaryOwner.TryGetTableCount(table, ownedSnapshotVersion: 1, out _));
    }

    /// <summary>count cache失効前のbuild結果を保存しようとしても拒否し、古いcountを現cacheへ復活させないことを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheRejectsResultFromBuildBeforeInvalidation()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest staleBuild));
        var staleResult = new PlaylistSummaryCountResult
        {
            ScannedEntries = 4,
            TotalCharts = 3,
            OwnedCharts = 2
        };

        long tableCountGeneration = workspace.CatalogSummaryOwner.TableCountCacheGeneration;
        workspace.CatalogSummaryOwner.InvalidateTableCounts();

        var table = new BMSTable
        {
            playlist_id = 1
        };
        Assert.IsFalse(workspace.CatalogSummaryOwner.TrySetTableCount(
            table,
            ownedSnapshotVersion: 1,
            countResult: staleResult,
            expectedGeneration: tableCountGeneration));
        Assert.IsFalse(workspace.CatalogSummaryOwner.TryGetTableCount(table, ownedSnapshotVersion: 1, out _));
        workspace.CompletePlaylistSummaryDataBuild(staleBuild);
    }

    /// <summary>終了停止でactive buildを取消し、新buildと既存要求の表示適用を拒否して既存build完了後にidleへ戻します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCannotRestartAfterShutdownStop()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest activeBuild));
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();

        workspace.StopPlaylistSummaryDataBuild();

        Assert.IsTrue(activeBuild.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.TryBeginPlaylistSummaryDataBuild(out _));
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = activeBuild.Generation
        }));
        workspace.CompletePlaylistSummaryDataBuild(activeBuild);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
    }

    /// <summary>data更新は実行中buildを取消して表示だけの保留を優先度の低いものとして統合し、data保留を一度だけ消費することを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDeferredDataRefreshCancelsBuildAndDominatesPresentation()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest activeBuild));
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        long nextBuildGeneration = workspace.RequestPlaylistSummaryDataRefresh().NextBuildGeneration;
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        Assert.IsTrue(activeBuild.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, nextBuildGeneration);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryRefresh());
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.None,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
        workspace.CompletePlaylistSummaryDataBuild(activeBuild);
    }

    /// <summary>表示だけの保留も外部data更新指定ではdataとして消費し、summary modeを外れた保留は復帰時に再実行しないことを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshPrefersDataAndDropsPendingWorkOnModeExit()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        workspace.IsPlaylistSummaryMode = true;
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: true));

        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();
        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistSummaryMode = true;

        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.None,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
    }

    /// <summary>非表示のdata更新ではcacheを失効して予約せず、表示中の複数要求は次build世代のdata保留へ集約することを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDataRefreshRequestOwnsVisibilityAndDeferralDecision()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        long hiddenDataGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        long hiddenCacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;

        PlaylistSummaryDataRefreshRequestResult hidden = workspace.RequestPlaylistSummaryDataRefresh();

        Assert.IsFalse(hidden.Queued);
        Assert.AreEqual(0L, hidden.NextBuildGeneration);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > hiddenDataGeneration);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryRowsCacheGeneration > hiddenCacheGeneration);

        workspace.IsPlaylistSummaryMode = true;
        PlaylistSummaryDataRefreshRequestResult visible = workspace.RequestPlaylistSummaryDataRefresh();
        Assert.IsTrue(visible.Queued);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, visible.NextBuildGeneration);

        PlaylistSummaryDataRefreshRequestResult coalesced = workspace.RequestPlaylistSummaryDataRefresh();
        Assert.IsTrue(coalesced.Queued);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, coalesced.NextBuildGeneration);
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
    }

    /// <summary>shellが表示更新を保留する間はdata要求を保持し、許可後に消費してidleへ戻し、非表示要求は失効だけ行うことを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceSummaryDataRefreshUsesShellGateAndDrainsWhenAllowed()
    {
        bool deferred = true;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: _ => deferred);
        workspace.IsPlaylistSummaryMode = true;

        long initialGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        long deferredGeneration = workspace.RequestPlaylistSummaryDataRefresh(
            "test_deferred_summary_data",
            rebuildAsync: false);

        Assert.AreEqual(initialGeneration + 2L, deferredGeneration);
        Assert.IsTrue(
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false)
                == PlaylistSummaryDeferredRefreshKind.Data);

        deferred = false;
        long drainedGeneration = workspace.RequestPlaylistSummaryDataRefresh(
            "test_drained_summary_data",
            rebuildAsync: false);

        Assert.IsTrue(drainedGeneration > deferredGeneration);
        Assert.IsTrue(
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false)
                == PlaylistSummaryDeferredRefreshKind.None);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);

        workspace.IsPlaylistSummaryMode = false;
        long hiddenGenerationBefore = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        Assert.AreEqual(
            0L,
            workspace.RequestPlaylistSummaryDataRefresh(
                "test_hidden_summary_data",
                rebuildAsync: false));
        Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > hiddenGenerationBefore);
    }

    /// <summary>非表示では保留消費を開始せず、表示中の有効cacheに対する表示だけの保留は再buildなしでviewへ適用します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshDrainOwnsHiddenAndPresentationRoutes()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);

        Assert.AreEqual(
            0L,
            workspace.DrainDeferredPlaylistSummaryRefresh(
                dataRefreshRequired: true,
                rebuildAsync: false));

        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        var rows = new List<PlaylistSummaryRow> { new() };
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(rows, dataGeneration));
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        long presentationGenerationBefore = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.AreEqual(
            0L,
            workspace.DrainDeferredPlaylistSummaryRefresh(
                dataRefreshRequired: false,
                rebuildAsync: false));
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > presentationGenerationBefore);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
    }

    /// <summary>型付き構成の実dropからDB・参照確定を通し、操作通知内と実Task終端後の両方で保存値と参照表示を確認します。</summary>
    [TestMethod]
    public async Task PlaylistDropReferenceIndex_IsCommittedBeforeOperationNotification()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsLibrary? libraryForCleanup = null;
        TestBmsPlaylist? playlistForCleanup = null;
        PlaylistWorkspaceViewModel? workspaceForCleanup = null;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                playlist_id = 7815,
                name = "Drop target",
                symbol = "DROP",
                Output_dir = "DropTarget",
                Folder_order = ["Imported"]
            };
            var library = new TestBmsLibrary(songDbPath);
            libraryForCleanup = library;
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => new CustomFolderOutputSettingsSnapshot(),
                playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot())
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlistForCleanup = playlist;
            // 正規の初回保存でDBと参照ownerへ空表を登録してから、局所dropを始める。
            playlist.CommitBMSTableWithEntriesToDB(table);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspaceForCleanup = workspace;
            workspace.RefreshPlaylistTreeTables(playlist);

            const string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            ChartFile chart = (
                ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(md5, @"C:\Library\drop-chart.bms"))));
            var libraryRow = LibraryChartRow.FromChartFile(chart);
            int notifications = 0;
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) =>
            {
                using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == table.playlist_id && entry.md5 == md5));
                Assert.AreEqual("DROP", library.GetPlaylistReferenceDisplay(chart).Symbols);
                Assert.AreEqual("Drop target", library.GetPlaylistReferenceDisplay(chart).Names);
                notifications++;
            };

            await workspace.AddRowsToFolderAsync(
                [libraryRow],
                table,
                PlaylistFolderNode.CreateFolder("Imported"));

            Assert.AreEqual("DROP", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Drop target", library.GetPlaylistReferenceDisplay(chart).Names);
            Assert.AreEqual(1, notifications);
        }
        finally
        {
            if (workspaceForCleanup != null)
            {
                workspaceForCleanup.CancelDetailBuilds();
                await workspaceForCleanup.WaitForDetailBuildIdleAsync();
                await workspaceForCleanup.WaitForPlaylistSummaryDataBuildIdleAsync();
                workspaceForCleanup.Dispose();
            }
            playlistForCleanup?.RequestShutdown("playlist_drop_reference_test");
            libraryForCleanup?.RequestShutdown("playlist_drop_reference_test");
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同じ入力順の三譜面を一括と分割でrootへ追加し、独立した期待所属・folder順と実DBが一致して分割による分類のずれを検出します。</summary>
    [TestMethod]
    public async Task PlaylistRootFolderDrop_UsesPlannedFoldersAcrossBatchAndSplitInputs()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "playlist-root-drop-working-set-" + Guid.NewGuid().ToString("N"));
        string batchDirectory = Path.Combine(tempDirectory, "batch");
        string splitDirectory = Path.Combine(tempDirectory, "split");
        Directory.CreateDirectory(batchDirectory);
        Directory.CreateDirectory(splitDirectory);
        try
        {
            (_, _, BMSTable batchTable, PlaylistWorkspaceViewModel batchWorkspace,
                ChartFile batchChartA, ChartFile batchChartB, ChartFile batchChartC) = CreateRootDropWorkingSetFixture(batchDirectory, 7820);
            (_, _, BMSTable splitTable, PlaylistWorkspaceViewModel splitWorkspace,
                ChartFile splitChartA, ChartFile splitChartB, ChartFile splitChartC) = CreateRootDropWorkingSetFixture(splitDirectory, 7821);

            await batchWorkspace.AddRowsToFolderAsync(
                [
                    LibraryChartRow.FromChartFile(batchChartA),
                    LibraryChartRow.FromChartFile(batchChartB),
                    LibraryChartRow.FromChartFile(batchChartC)
                ],
                batchTable);

            await splitWorkspace.AddRowsToFolderAsync(
                [
                    LibraryChartRow.FromChartFile(splitChartA),
                    LibraryChartRow.FromChartFile(splitChartB)
                ],
                splitTable);
            await splitWorkspace.AddRowsToFolderAsync(
                [LibraryChartRow.FromChartFile(splitChartC)],
                splitTable);

            string[] batchFolders = [.. batchTable.folder_list.Where(folder => !string.IsNullOrWhiteSpace(folder))];
            string[] splitFolders = [.. splitTable.folder_list.Where(folder => !string.IsNullOrWhiteSpace(folder))];
            string[] batchEntries = [.. batchTable.GetEntriesExceptDummy()
                .Select(entry => entry.md5 + "|" + entry.folder)
                .OrderBy(value => value, StringComparer.Ordinal)];
            string[] splitEntries = [.. splitTable.GetEntriesExceptDummy()
                .Select(entry => entry.md5 + "|" + entry.folder)
                .OrderBy(value => value, StringComparer.Ordinal)];

            string[] expectedFolders = ["Alpha", "Zeta"];
            string[] expectedEntries = [
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|Zeta",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|Alpha",
                "cccccccccccccccccccccccccccccccc|Alpha"
            ];
            CollectionAssert.AreEqual(expectedFolders, batchFolders, "batch folders: " + string.Join(",", batchFolders));
            CollectionAssert.AreEqual(expectedFolders, splitFolders, "split folders: " + string.Join(",", splitFolders));
            CollectionAssert.AreEqual(expectedEntries, batchEntries, "batch entries: " + string.Join(",", batchEntries));
            CollectionAssert.AreEqual(expectedEntries, splitEntries, "split entries: " + string.Join(",", splitEntries));

            AssertPersistedRootDropEntries(
                Path.Combine(batchDirectory, "song.db"),
                7820,
                batchEntries);
            AssertPersistedRootDropEntries(
                Path.Combine(splitDirectory, "song.db"),
                7821,
                splitEntries);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>package内の既存hash一致を現folderへ統合し、packageなしの同タイトルは接尾辞付き別folderとして実保存して名前だけの誤統合を防ぎます。</summary>
    [TestMethod]
    public async Task PlaylistRootFolderDrop_MergesByPackageHashAndSuffixesNameCollisions()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "playlist-root-drop-classification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            const string existingHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string mergedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string sameTitleHash = "cccccccccccccccccccccccccccccccc";
            BMSTable table = new()
            {
                playlist_id = 7822,
                name = "Root classification",
                symbol = "ROOT-CLASSIFICATION",
                Output_dir = "RootClassification",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder,
                is_root_folder = true,
                Folder_order = ["Existing"],
                entries =
                [
                    new BMSTableEntry
                    {
                        md5 = existingHash,
                        title = "Existing source",
                        folder = "Existing"
                    }
                ]
            };
            string mergeDirectory = Path.Combine(tempDirectory, "merge-directory");
            string noPackageDirectory = Path.Combine(tempDirectory, "no-package-directory");
            ChartFile packageAnchor = CreatePlaylistDropBmsFile(
                existingHash,
                "Package anchor",
                Path.Combine(mergeDirectory, "anchor.bms"));
            ChartFile mergedFile = CreatePlaylistDropBmsFile(
                mergedHash,
                "Merged song",
                Path.Combine(mergeDirectory, "merged.bms"));
            ChartFile sameTitleFile = CreatePlaylistDropBmsFile(
                sameTitleHash,
                "Existing",
                Path.Combine(noPackageDirectory, "same-title.bms"));
            (_, TestBmsLibrary library, BMSTable activeTable, PlaylistWorkspaceViewModel workspace) =
                CreateRootDropStore(tempDirectory, table, [packageAnchor, mergedFile]);
            AssertPersistedRootDropEntries(Path.Combine(tempDirectory, "song.db"), 7822, [existingHash + "|Existing"]);

            await workspace.AddRowsToFolderAsync(
                [
                    LibraryChartRow.FromChartFile((mergedFile)),
                    LibraryChartRow.FromChartFile((sameTitleFile))
                ],
                activeTable);

            Assert.AreEqual(2, activeTable.folder_list.Count(folder => !string.IsNullOrWhiteSpace(folder)));
            Assert.IsTrue(activeTable.GetEntriesExceptDummy().Any(entry =>
                entry.md5 == existingHash && entry.folder == "Existing"));
            Assert.IsTrue(activeTable.GetEntriesExceptDummy().Any(entry =>
                entry.md5 == mergedHash && entry.folder == "Existing"));
            BMSTableEntry sameTitleEntry = activeTable.GetEntriesExceptDummy().Single(entry => entry.md5 == sameTitleHash);
            Assert.AreEqual("Existing (2)", sameTitleEntry.folder);

            AssertPersistedRootDropEntries(
                Path.Combine(tempDirectory, "song.db"),
                7822,
                [.. activeTable.GetEntriesExceptDummy()
                    .Select(entry => entry.md5 + "|" + entry.folder)
                    .OrderBy(value => value, StringComparer.Ordinal)]);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存済み詳細の同表root再分類・別表コピーで編集値を正本と新readonly DBへ保持し、表示用URLの材料化と重複先の上書きを防ぎます。未所持詳細も保持します。</summary>
    [DataTestMethod]
    [DataRow(false)] // 同表の表示前sourceを移動します。
    [DataRow(true)] // 別表の表示行をコピーし、コピー元も保持します。
    public async Task PlaylistRootFolderDrop_PreservesEditedEntryValuesAndExistingDuplicates(bool copyFromAnotherTable)
    {
        const string adoptedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string duplicateHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string anchorHash = "cccccccccccccccccccccccccccccccc";
        const string missingHash = "dddddddddddddddddddddddddddddddd";
        string tempDirectory = Path.Combine(Path.GetTempPath(), "playlist-root-drop-edited-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        BMSPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            static BMSTableEntry EditedEntry(string hash, string prefix, double level, string folder = "") => new()
            {
                md5 = hash,
                title = prefix + " title",
                folder = folder,
                memo = prefix + " memo",
                comment = prefix + " comment",
                Url = new Uri("https://example.com/" + prefix + "/url1"),
                Url_diff = new Uri("https://example.com/" + prefix + "/url2"),
                name_diff = prefix + " name_diff",
                level = level
            };
            BMSTableEntry adopted = EditedEntry(adoptedHash, "adopted", 17.25);
            BMSTableEntry duplicateInput = EditedEntry(duplicateHash, "discarded", 28.5);
            BMSTableEntry existing = EditedEntry(duplicateHash, "existing", 39.75, "Classified");
            BMSTableEntry missing = EditedEntry(missingHash, "missing", 41.5);
            BMSTable table = new()
            {
                playlist_id = 7824,
                name = "Edited drop target",
                Output_dir = "EditedDropTarget",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder,
                is_root_folder = true,
                Folder_order = ["Classified"],
                entries = [new BMSTableEntry { md5 = anchorHash, title = "Anchor", folder = "Classified" }, existing]
            };
            BMSTable source = copyFromAnotherTable
                ? new BMSTable { playlist_id = 7825, name = "Edited source", Output_dir = "EditedSource", entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder, entries = [adopted, duplicateInput, missing] }
                : table;
            if (!copyFromAnotherTable) { table.entries = [.. table.entries, adopted, duplicateInput, missing]; }
            string chartDirectory = Path.Combine(tempDirectory, "package");
            ChartFile adoptedChart = CreatePlaylistDropBmsFile(adoptedHash, "Catalog adopted", Path.Combine(chartDirectory, "adopted.bms"));
            ChartFile duplicateChart = CreatePlaylistDropBmsFile(duplicateHash, "Catalog duplicate", Path.Combine(chartDirectory, "duplicate.bms"));
            ChartFile anchorChart = CreatePlaylistDropBmsFile(anchorHash, "Anchor", Path.Combine(chartDirectory, "anchor.bms"));
            (playlist, library, _, workspace) = CreateRootDropStore(tempDirectory, table, [adoptedChart, duplicateChart, anchorChart]);
            if (copyFromAnotherTable)
            {
                playlist.BMSTables.Add(source);
                playlist.CommitBMSTableWithEntriesToDB(source);
            }
            adopted.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime/url1"), new Uri("https://example.com/runtime/url2"), overwriteExisting: true);
            Assert.AreNotEqual(adopted.Url, adopted.EffectiveUrl);
            PlaylistDetailSourceRow[] sourceRows = [new(adopted, adoptedChart), new(duplicateInput, duplicateChart), new(missing, resolvedChart: null)];
            object[] rows = [.. sourceRows.Select(row => copyFromAnotherTable ? (object)row.CreateViewRow() : row)];

            await workspace.AddRowsToFolderAsync(rows, table);

            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using LR2SongDBExtended database = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            static void AssertValues(BMSTable current, LR2SongDBExtended database, string hash, string prefix, double level, string folder)
            {
                PlaylistEntryValue value = current.CaptureEntriesReadSnapshot().Entries.Single(change => change.After.Md5 == hash).After;
                LR2SongDBExtended.playlist_entry persisted = database.Table<LR2SongDBExtended.playlist_entry>()
                    .Single(entry => entry.playlist_id == current.playlist_id && entry.md5 == hash);
                string[] expected = [prefix + " memo", prefix + " comment", "https://example.com/" + prefix + "/url1", "https://example.com/" + prefix + "/url2", prefix + " name_diff", folder];
                CollectionAssert.AreEqual(expected, new[] { value.Memo, value.Comment, value.Url.AbsoluteUri, value.UrlDiff.AbsoluteUri, value.NameDiff, value.Folder });
                CollectionAssert.AreEqual(expected, new[] { persisted.memo, persisted.comment, persisted.url, persisted.url_diff, persisted.name_diff, persisted.folder });
                Assert.AreEqual(level, value.Level);
                Assert.AreEqual(level, persisted.level);
            }
            AssertValues(table, database, adoptedHash, "adopted", 17.25, "Classified");
            AssertValues(table, database, duplicateHash, "existing", 39.75, "Classified");
            AssertValues(table, database, missingHash, "missing", 41.5, "");
            Assert.AreEqual(4, table.CaptureEntriesReadSnapshot().Entries.Count(change => !change.After.IsRemoved));
            Assert.AreEqual(4, database.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == table.playlist_id && !entry.is_removed));
            if (copyFromAnotherTable)
            {
                AssertValues(source, database, adoptedHash, "adopted", 17.25, "");
                AssertValues(source, database, duplicateHash, "discarded", 28.5, "");
                AssertValues(source, database, missingHash, "missing", 41.5, "");
            }
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                workspace.Dispose();
            }
            playlist?.RequestShutdown("playlist_root_drop_edited_test");
            library?.RequestShutdown("playlist_root_drop_edited_test");
            if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); }
        }
    }

    /// <summary>実外部同期で削除履歴だけとなったfolderへ同hash譜面を再追加し、履歴を保持しつつ新active行を別folderへ実保存します。</summary>
    [TestMethod]
    public async Task PlaylistRootFolderDrop_DoesNotClassifyIntoRemovedOnlyFolderHistory()
    {
        const string historyHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "playlist-root-drop-removed-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string scorePath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerPath,
                BmsPlaylistTestSupport.CreateUtf8BomBytes(
                    "{\r\n\"name\":\"Removed history source\",\r\n\"symbol\":\"REMOVED-HISTORY\",\r\n\"entry_type\":\"folder\",\r\n\"compat_prefix\":\"\",\r\n\"folder_order\":[\"Old\"],\r\n\"folder_sort_key\":\"\",\r\n\"folder_sort_ascending\":true,\r\n\"data_url\":\"./score.json\"\r\n}"));
            File.WriteAllBytes(
                scorePath,
                BmsPlaylistTestSupport.CreateUtf8BomBytes(
                    "[{\"md5\":\"" + historyHash + "\",\"title\":\"Old source\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSPlaylist playlist = new TestBmsPlaylist(
                songDbPath,
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));
            table.playlist_id = 7823;
            table.entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder;
            table.is_root_folder = true;
            table.Folder_order = ["Old"];
            BMSTableEntry oldEntry = table.entries.Single();
            oldEntry.folder = "Old";
            oldEntry.parent = table;
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            PersistPlaylistAggregate(songDbPath, table);

            File.WriteAllBytes(scorePath, BmsPlaylistTestSupport.CreateUtf8BomBytes("[]"));
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult reloadResults =
                await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_removed_only_folder_history");

            Assert.IsTrue(reloadResults.Single().Succeeded);
            BMSTable historyTable = playlist.BMSTables.Single();
            Assert.AreEqual(
                1,
                historyTable.entries.Count(entry => entry.md5 == historyHash && entry.is_removed),
                "The canonical reload must leave a removed history row for the drop to encounter.");
            Assert.AreEqual("Old", historyTable.entries.Single(entry => entry.md5 == historyHash).folder);
            historyTable.DisableExternalSync();

            string dropDirectory = Path.Combine(tempDirectory, "drop-directory");
            ChartFile packageAnchor = CreatePlaylistDropBmsFile(
                historyHash,
                "Package anchor",
                Path.Combine(dropDirectory, "anchor.bms"));
            ChartFile droppedFile = CreatePlaylistDropBmsFile(
                historyHash,
                "New song",
                Path.Combine(dropDirectory, "new-song.bms"));
            TestBmsLibrary library = new(songDbPath)
            {
                BmsCharts = [packageAnchor, droppedFile]
            };
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
                playlist,
                library);

            await workspace.AddRowsToFolderAsync(
                [LibraryChartRow.FromChartFile((droppedFile))],
                historyTable);

            BMSTableEntry? activeEntry = historyTable.entries.SingleOrDefault(
                entry => entry.md5 == historyHash && !entry.is_removed);
            Assert.IsNotNull(
                activeEntry,
                "A chart matching only removed folder history must remain as a new active entry.");
            Assert.AreNotEqual(
                "Old",
                activeEntry!.folder,
                "A removed-only folder must not become the root-drop classification target.");
            Assert.AreEqual(
                1,
                historyTable.entries.Count(entry => entry.md5 == historyHash && !entry.is_removed));
            Assert.AreEqual(
                1,
                historyTable.entries.Count(entry => entry.md5 == historyHash && entry.is_removed));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                    7823,
                    historyHash));
            Assert.AreNotEqual(
                "Old",
                verify.ExecuteScalar<string>(
                    "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                    7823,
                    historyHash));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実再同期でH@Bを履歴化してから明示再追加・移動・コピーし、新DB・参照表示・実LR2 COMMAND・gzip BMTまでBの生存所属一件と入力元の値を確認します。</summary>
    /// <remarks>履歴置換の分岐とDB前失敗はEntryCommitの局所テスト、DB後失敗・P寿命は既存Admission系列が分担します。Windowを作らず、受理Taskとbuild終端後にownerと固有tempを回収します。</remarks>
    [DataTestMethod]
    [DataRow("library")]
    [DataRow("move")]
    [DataRow("copy")]
    public async Task PlaylistDrop_RemovedDestinationUsesCurrentInputAndCompletesRequiredOutputs(string inputKind)
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string keptHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string tempDirectory = Path.Combine(Path.GetTempPath(), "playlist-drop-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        BMSPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string outputBase = Path.Combine(tempDirectory, "lr2");
            string bmtRoot = Path.Combine(tempDirectory, "beatoraja");
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderOutputBaseDirRootType = outputBase,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            ChartFile chart = CreatePlaylistDropBmsFile(hash, "Catalog H", Path.Combine(tempDirectory, "charts", "h.bms"));
            library = new TestBmsLibrary(songDbPath) { BmsCharts = [chart] };
            playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => outputSettings,
                playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = Path.Combine(bmtRoot, "table.json")
                },
                lr2PlaylistFolderSynchronization: BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string scorePath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllText(headerPath, "{\"name\":\"History destination\",\"symbol\":\"HISTORY\",\"entry_type\":\"folder\",\"compat_prefix\":\"\",\"folder_order\":[\"B\"],\"data_url\":\"./score.json\"}");
            string keptJson = "{\"md5\":\"" + keptHash + "\",\"title\":\"Kept K\",\"level\":\"B\"}";
            File.WriteAllText(scorePath, "[{\"md5\":\"" + hash + "\",\"title\":\"Old H\",\"level\":\"B\"}," + keptJson + "]");
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));
            table.playlist_id = 7826;
            table.Output_dir = "HistoryDestination";
            table.is_root_folder = false;
            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            BMSTableEntry old = table.entries.Single(entry => entry.md5 == hash);
            old.memo = "old history memo";
            old.adddate = new DateTime(2020, 1, 2, 3, 4, 5);
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);
            File.WriteAllText(scorePath, "[" + keptJson + "]");
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult reload = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table], reason: "test_explicit_add_to_removed_destination");
            Assert.IsTrue(reload.Single().Succeeded);
            table = playlist.BMSTables.Single();
            Assert.AreEqual(1, table.entries.Count(entry => entry.md5 == hash && entry.folder == "B" && entry.is_removed));
            Assert.AreEqual(1, table.entries.Count(entry => entry.md5 == keptHash && entry.folder == "B" && !entry.is_removed));
            table.DisableExternalSync();
            BMSTableEntry source = new()
            {
                md5 = hash,
                title = "Current H",
                folder = "A",
                memo = "current memo",
                comment = "current comment",
                Url = new Uri("https://example.com/current/main"),
                Url_diff = new Uri("https://example.com/current/diff"),
                name_diff = "current name diff",
                level = 17.25,
                adddate = new DateTime(2024, 5, 6, 7, 8, 9)
            };
            BMSTable? sourceTable = null;
            if (inputKind == "move")
            {
                table.entries = [.. table.entries, source];
                playlist.CommitBMSTableWithEntriesToDB(table);
            }
            else if (inputKind == "copy")
            {
                sourceTable = new BMSTable { playlist_id = 7827, name = "Current source", Output_dir = "CurrentSource", entries = [source] };
                playlist.BMSTables.Add(sourceTable);
                playlist.CommitBMSTableWithEntriesToDB(sourceTable);
            }
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("INSERT INTO song (hash, title, path, folder) VALUES (?, 'Catalog H', 'h.bms', 'package-h');", hash);
                db.Execute("INSERT INTO song (hash, title, path, folder) VALUES (?, 'Kept K', 'k.bms', 'package-k');", keptHash);
            }
            workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(playlist, library, () => outputSettings);
            source.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime/main"), new Uri("https://example.com/runtime/diff"), overwriteExisting: true);
            object row = inputKind == "library" ? LibraryChartRow.FromChartFile(chart) : new PlaylistDetailSourceRow(source, chart).CreateViewRow();
            DateTime started = DateTime.Now;
            await workspace.AddRowsToFolderAsync([row], table, PlaylistFolderNode.CreateFolder("B"));
            DateTime completed = DateTime.Now;
            await workspace.WaitForDetailBuildIdleAsync();
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            BMSTableEntry active = table.entries.Single(entry => entry.md5 == hash && entry.folder == "B");
            Assert.IsFalse(active.is_removed);
            Assert.IsFalse(table.entries.Any(entry => entry.md5 == hash && entry.folder == "A"));
            Assert.AreEqual(inputKind == "library" ? null : "current memo", active.memo);
            if (inputKind == "library")
            {
                Assert.IsTrue(active.adddate >= started && active.adddate <= completed);
                Assert.AreNotEqual(old.adddate, active.adddate);
            }
            else
            {
                Assert.AreEqual(source.adddate, active.adddate);
                CollectionAssert.AreEqual(new[] { "current comment", "current name diff", "https://example.com/current/main", "https://example.com/current/diff" },
                    new[] { active.comment, active.name_diff, active.Url.AbsoluteUri, active.Url_diff.AbsoluteUri });
                Assert.AreEqual(17.25, active.level);
            }
            Assert.IsTrue(library.GetPlaylistReferenceDisplay(chart).Names.Contains("History destination", StringComparison.Ordinal));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDBExtended.playlist_entry persisted = db.Table<LR2SongDBExtended.playlist_entry>()
                    .Single(entry => entry.playlist_id == table.playlist_id && entry.md5 == hash);
                Assert.IsFalse(persisted.is_removed);
                Assert.AreEqual("B", persisted.folder);
                Assert.AreEqual(active.memo, persisted.memo);
                Assert.AreEqual(active.comment, persisted.comment);
                Assert.AreEqual(active.name_diff, persisted.name_diff);
                Assert.AreEqual(active.url, persisted.url);
                Assert.AreEqual(active.url_diff, persisted.url_diff);
                Assert.AreEqual(active.level, persisted.level);
                // 既存のSQLite ticks保存精度で追加時刻を保持します。
                Assert.AreEqual(active.adddate, persisted.adddate);
                if (sourceTable != null)
                {
                    LR2SongDBExtended.playlist_entry copiedFrom = db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == sourceTable.playlist_id);
                    Assert.AreEqual("current memo", copiedFrom.memo);
                    Assert.AreEqual("A", copiedFrom.folder);
                    Assert.AreEqual(source.adddate, copiedFrom.adddate);
                    Assert.AreSame(source, sourceTable.entries.Single());
                }
                var commands = Directory.GetFiles(Path.Combine(outputBase, table.output_dir), "*.lr2folder", SearchOption.AllDirectories)
                    .Select(path => File.ReadAllText(path, Encoding.GetEncoding("shift_jis")))
                    .ToDictionary(text => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith("#TITLE ", StringComparison.Ordinal))[7..],
                        text => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith("#COMMAND ", StringComparison.Ordinal))[9..]);
                foreach (string title in new[] { "B", "History destination ALL" })
                {
                    CollectionAssert.AreEquivalent(new[] { hash, keptHash }, db.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + commands[title]).Select(song => song.hash).ToArray());
                }
            }
            string bmt = Directory.GetFiles(bmtRoot, "*.bmt", SearchOption.AllDirectories).Single();
            using (var compressed = new GZipStream(File.OpenRead(bmt), CompressionMode.Decompress))
            using (var reader = new StreamReader(compressed))
            {
                var document = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
                Newtonsoft.Json.Linq.JToken folder = document["folder"]?.Single(value => value.Value<string>("name") == "B")
                    ?? throw new AssertFailedException("BMT folder B missing");
                Assert.AreEqual(1, folder["songs"]?.Count(song => song.Value<string>("md5") == hash));
            }
            DateTime sameFolderDate = active.adddate;
            string? sameFolderMemo = active.memo;
            int sameFolderRevision = table.PlaylistEntriesRevision;
            await workspace.AddRowsToFolderAsync([new PlaylistDetailSourceRow(active, chart).CreateViewRow()], table, PlaylistFolderNode.CreateFolder("B"));
            Assert.AreSame(active, table.entries.Single(entry => entry.md5 == hash && entry.folder == "B"));
            Assert.AreEqual(sameFolderDate, active.adddate);
            Assert.AreEqual(sameFolderMemo, active.memo);
            Assert.AreEqual(sameFolderRevision, table.PlaylistEntriesRevision);
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                workspace.Dispose();
            }
            playlist?.RequestShutdown("playlist_drop_history_test");
            library?.RequestShutdown("playlist_drop_history_test");
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>Busyのdropを未開始として拒否し、実DB・LR2・BMT・参照更新を終端まで所有して、出力・準備失敗後も確定行を保持しDB失敗は別errorで通知します。</summary>
    [TestMethod]
    public async Task PlaylistDropAdmission_AwaitsRequiredOutputsAndPreservesCommittedFactsAcrossFailures()
    {
        static string ReadBmtText(string path)
        {
            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        TestBmsLibrary? libraryForCleanup = null;
        TestBmsPlaylist? playlistForCleanup = null;
        PlaylistWorkspaceViewModel? workspaceForCleanup = null;
        try
        {
            string outputBaseDirectory = Path.Combine(tempDirectory, "custom-folder-output");
            string bmtTablePath = Path.Combine(tempDirectory, "beatoraja", "table.json");
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBaseDirectory,
                LR2CustomFolderOutputBaseDirRootType = outputBaseDirectory,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var outputAdmission = new ChartFileOperationSynchronizer();
            Settings librarySettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            var library = new TestBmsLibrary(songDbPath, getLR2Config: null, _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(librarySettings),
                playlistOperationAdmission: outputAdmission);
            libraryForCleanup = library;
            int lr2SyncProbeCount = 0;
            int lr2SyncProbeLeaseUnavailableCount = 0;
            TestLr2PlaylistFolderSynchronizationPort synchronization = BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.SynchronizationStartProbe = mutationCapability =>
            {
                lr2SyncProbeCount++;
                Assert.IsNotNull(mutationCapability);
                bool acquired = outputAdmission.TryEnter(out IDisposable competingLease);
                using (competingLease)
                {
                    Assert.IsFalse(acquired);
                    lr2SyncProbeLeaseUnavailableCount++;
                }
            };
            var table = new BMSTable
            {
                playlist_id = 7816,
                name = "Drop admission target",
                symbol = "DROP-ADMISSION",
                Output_dir = "DropAdmission",
                Folder_order = ["Imported", "Fault", "PreparationFault", "DatabaseFault"]
            };
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => outputSettings,
                playlistUrlCompletionOptionsProvider: () => new PlaylistUrlCompletionOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = bmtTablePath
                },
                lr2PlaylistFolderSynchronization: synchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlistForCleanup = playlist;
            // 初回保存はLR2/BMTを出力せず、正規の空表登録をDBと参照ownerへ確定する。
            playlist.CommitBMSTableWithEntriesToDB(table);
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
                playlist,
                library,
                () => outputSettings);
            workspaceForCleanup = workspace;
            int uiInvalidationCount = 0;
            int uiEffectOutsideLeaseCount = 0;
            int notificationCount = 0;
            int notificationEffectOutsideLeaseCount = 0;
            bool notificationCallbackFailure = false;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            int legacyBmtScheduleCount = 0;

            workspace.PlaylistDetailReloadRefreshRequested +=
                (_, _) =>
                {
                    using (LibraryFileMutationLease callbackLease = library.TryBeginLibraryFileMutation(
                               "playlist_drop_ui_effect_probe",
                               showMessage: false))
                    {
                        if (callbackLease != null)
                        {
                            uiEffectOutsideLeaseCount++;
                        }
                    }
                    uiInvalidationCount++;
                };
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) =>
                {
                    if (request.RouteName == "playlist drop custom folder output notification")
                    {
                        using (LR2SongDBExtended committed = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                        {
                            foreach (BMSTableEntry current in table.GetEntriesExceptDummy())
                            {
                                Assert.AreEqual(1, committed.Table<LR2SongDBExtended.playlist_entry>().Count(entry =>
                                    entry.playlist_id == table.playlist_id && entry.md5 == current.md5));
                                Assert.AreEqual("DROP-ADMISSION", library.GetPlaylistReferenceDisplay(current.md5, current.sha256).Symbols);
                            }
                        }
                        notifications.Add(request);
                        using (LibraryFileMutationLease callbackLease = library.TryBeginLibraryFileMutation(
                                   "playlist_drop_notification_effect_probe",
                                   showMessage: false))
                        {
                            if (callbackLease != null)
                            {
                                notificationEffectOutsideLeaseCount++;
                            }
                        }
                        notificationCount++;
                        if (notificationCallbackFailure)
                        {
                            throw new InvalidOperationException("playlist drop secondary notification failure");
                        }
                    }
                };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => { legacyBmtScheduleCount++; return false; };
            const string md5 = "ffffffffffffffffffffffffffffffff";
            ChartFile chart = (
                ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(md5, Path.Combine(tempDirectory, "drop-chart.bms")))));
            var libraryRow = LibraryChartRow.FromChartFile(chart);
            const string outputFailureMd5 = "11111111111111111111111111111111";
            ChartFile outputFailureChart = (
                ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(outputFailureMd5, Path.Combine(tempDirectory, "drop-output-failure-chart.bms")))));
            var outputFailureLibraryRow = LibraryChartRow.FromChartFile(outputFailureChart);
            const string preparationFailureMd5 = "22222222222222222222222222222222";
            ChartFile preparationFailureChart = (
                ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(preparationFailureMd5, Path.Combine(tempDirectory, "drop-preparation-failure-chart.bms")))));
            var preparationFailureLibraryRow = LibraryChartRow.FromChartFile(preparationFailureChart);
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("Imported"));
            workspace.IsPlaylistDetailViewActive = true;

            Assert.IsTrue(outputAdmission.TryEnter(out IDisposable incumbent));
            using (incumbent)
            {
                Assert.IsNotNull(incumbent);
                await workspace.AddRowsToFolderAsync(
                        [libraryRow],
                        table,
                        PlaylistFolderNode.CreateFolder("Imported"));

                Assert.AreEqual(0, table.GetEntriesExceptDummy().Count());
                using (LR2SongDBExtended verifyBusy = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(0, verifyBusy.Table<LR2SongDBExtended.playlist_entry>().Count());
                }
                Assert.IsFalse(Directory.Exists(Path.Combine(outputBaseDirectory, table.Output_dir)));
                Assert.AreEqual(0, uiInvalidationCount);
                Assert.AreEqual(0, uiEffectOutsideLeaseCount);
                Assert.AreEqual(0, notificationEffectOutsideLeaseCount);
                Assert.AreEqual(0, legacyBmtScheduleCount);
                Assert.AreEqual(0, lr2SyncProbeCount);
                Assert.AreEqual(0, lr2SyncProbeLeaseUnavailableCount);
            }


            notificationCallbackFailure = true;
            int notificationCountBeforeFreshRequest = notificationCount;

            await workspace.AddRowsToFolderAsync(
                [libraryRow],
                table,
                PlaylistFolderNode.CreateFolder("Imported"));

            Assert.AreEqual(1, table.GetEntriesExceptDummy().Count());
            string outputDirectory = Path.Combine(outputBaseDirectory, table.Output_dir);
            string[] generatedFiles = Directory.GetFiles(
                outputDirectory,
                "*.lr2folder",
                SearchOption.AllDirectories);
            Assert.IsTrue(generatedFiles.Length > 0);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, verify.Table<LR2SongDBExtended.playlist_entry>().Count());
                Assert.IsTrue(verify.Table<LR2SongDB.folder>().Any(folder =>
                    generatedFiles.Any(path => string.Equals(path, folder.path, StringComparison.OrdinalIgnoreCase))));
            }
            Assert.AreEqual(1, uiInvalidationCount);
            Assert.AreEqual(1, uiEffectOutsideLeaseCount);
            Assert.AreEqual(notificationCountBeforeFreshRequest + 1, notificationCount);
            Assert.AreEqual(1, notificationEffectOutsideLeaseCount);

            Assert.AreEqual(1, lr2SyncProbeCount);
            Assert.AreEqual(1, lr2SyncProbeLeaseUnavailableCount);
            Assert.AreEqual(0, legacyBmtScheduleCount);
            string bmt = Directory.GetFiles(bmtTablePath, "*.bmt").Single();
            StringAssert.Contains(ReadBmtText(bmt), md5);

            synchronization.Failure = new InvalidOperationException("playlist drop output failure");
            int notificationCountBeforeOutputFailure = notificationCount;

            InvalidOperationException outputFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                workspace.AddRowsToFolderAsync(
                    [outputFailureLibraryRow],
                    table,
                    PlaylistFolderNode.CreateFolder("Fault")));

            Assert.AreEqual("playlist drop output failure", outputFailure.Message);
            Assert.AreSame(synchronization.Failure, outputFailure);

            Assert.AreEqual(2, lr2SyncProbeCount);
            Assert.AreEqual(2, lr2SyncProbeLeaseUnavailableCount);
            using (LibraryFileMutationLease releasedLease = library.TryBeginLibraryFileMutation(
                       "playlist_drop_output_failure_release_probe",
                       showMessage: false))
            {
                Assert.IsNotNull(releasedLease);
            }
            Assert.AreEqual(2, table.GetEntriesExceptDummy().Count());
            using (LR2SongDBExtended verifyOutputFailure = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, verifyOutputFailure.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
            Assert.AreEqual(2, uiInvalidationCount);
            Assert.AreEqual(2, uiEffectOutsideLeaseCount);
            Assert.AreEqual(notificationCountBeforeOutputFailure + 1, notificationCount);
            Assert.AreEqual(2, notificationEffectOutsideLeaseCount);
            PlaylistOperationNotificationPresentationRequestedEventArgs outputFailureNotification = notifications[^1];
            Assert.AreEqual(
                1,
                outputFailureNotification.Receipt.Notifications.Count(notification =>
                    notification.Severity is PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning
                        or PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error));
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                outputFailureNotification.Receipt.Notifications.Single().Severity);
            Assert.AreEqual("DROP-ADMISSION", library.GetPlaylistReferenceDisplay(outputFailureChart).Symbols);
            Assert.AreEqual("Drop admission target", library.GetPlaylistReferenceDisplay(outputFailureChart).Names);
            Assert.AreEqual(0, legacyBmtScheduleCount);
            StringAssert.Contains(ReadBmtText(bmt), outputFailureMd5);

            synchronization.Failure = null!;
            NotSupportedException preparationFailure = new("playlist drop preparation failure");
            synchronization.PhysicalSurfaceFactory = () => throw preparationFailure;
            int notificationCountBeforePreparationFailure = notificationCount;

            NotSupportedException caughtPreparationFailure = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
                workspace.AddRowsToFolderAsync(
                    [preparationFailureLibraryRow],
                    table,
                    PlaylistFolderNode.CreateFolder("PreparationFault")));

            Assert.AreEqual("playlist drop preparation failure", caughtPreparationFailure.Message);
            Assert.AreSame(preparationFailure, caughtPreparationFailure);

            Assert.AreEqual(3, table.GetEntriesExceptDummy().Count());
            using (LR2SongDBExtended verifyPreparationFailure = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(3, verifyPreparationFailure.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
            Assert.AreEqual(3, uiInvalidationCount);
            Assert.AreEqual(3, uiEffectOutsideLeaseCount);
            Assert.AreEqual(notificationCountBeforePreparationFailure + 1, notificationCount);
            Assert.AreEqual(3, notificationEffectOutsideLeaseCount);
            Assert.AreEqual(2, lr2SyncProbeCount);
            Assert.AreEqual(2, lr2SyncProbeLeaseUnavailableCount);
            using (LibraryFileMutationLease releasedPreparationLease = library.TryBeginLibraryFileMutation(
                       "playlist_drop_preparation_failure_release_probe",
                       showMessage: false))
            {
                Assert.IsNotNull(releasedPreparationLease);
            }
            Assert.AreEqual("DROP-ADMISSION", library.GetPlaylistReferenceDisplay(preparationFailureChart).Symbols);
            Assert.AreEqual("Drop admission target", library.GetPlaylistReferenceDisplay(preparationFailureChart).Names);
            Assert.AreEqual(0, legacyBmtScheduleCount);
            StringAssert.Contains(ReadBmtText(bmt), preparationFailureMd5);

            // DB 書込み拒否は出力側の warning を生成しないため、drop の終端が generic error を一度だけ補う。
            notificationCallbackFailure = false;
            int notificationCountBeforeDatabaseFailure = notificationCount;
            int notificationEffectCountBeforeDatabaseFailure = notificationEffectOutsideLeaseCount;
            using (var failDatabaseWrite = new LR2SongDBExtended(songDbPath))
            {
                failDatabaseWrite.Execute(
                    "CREATE TRIGGER playlist_drop_test_write_failure "
                    + "BEFORE INSERT ON playlist WHEN NEW.playlist_id = 7816 "
                    + "BEGIN SELECT RAISE(ABORT, 'playlist drop DB write failure'); END;");
            }
            const string databaseFailureMd5 = "33333333333333333333333333333333";
            ChartFile databaseFailureChart = (
                ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(databaseFailureMd5, Path.Combine(tempDirectory, "drop-database-failure-chart.bms")))));
            Exception? databaseFailure = null;
            try
            {
                await workspace.AddRowsToFolderAsync(
                    [LibraryChartRow.FromChartFile(databaseFailureChart)],
                    table,
                    PlaylistFolderNode.CreateFolder("DatabaseFault"));
            }
            catch (Exception exception)
            {
                databaseFailure = exception;
            }

            Assert.IsNotNull(databaseFailure);
            StringAssert.Contains((databaseFailure ?? throw new AssertFailedException("Database write was not rejected.")).Message,
                "playlist drop DB write failure");
            Assert.AreEqual(3, table.GetEntriesExceptDummy().Count());
            using (LR2SongDBExtended unchanged = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            { Assert.AreEqual(3, unchanged.Table<LR2SongDBExtended.playlist_entry>().Count()); }
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(databaseFailureChart).Symbols);
            Assert.AreEqual(notificationCountBeforeDatabaseFailure + 1, notificationCount);
            Assert.AreEqual(notificationEffectCountBeforeDatabaseFailure + 1, notificationEffectOutsideLeaseCount);
            PlaylistOperationNotificationPresentationRequestedEventArgs databaseFailureNotification = notifications[^1];
            Assert.AreEqual(1, databaseFailureNotification.Receipt.Notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification databaseFailureNotice =
                databaseFailureNotification.Receipt.Notifications.Single();
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                databaseFailureNotice.Severity);
            Assert.IsTrue(outputAdmission.TryEnter(out IDisposable releasedAfterDatabaseFailure),
                "DB失敗の通知終端後は同じPで次の明示操作を受け付けます。");
            releasedAfterDatabaseFailure.Dispose();
        }
        finally
        {
            if (workspaceForCleanup != null)
            {
                workspaceForCleanup.CancelDetailBuilds();
                await workspaceForCleanup.WaitForDetailBuildIdleAsync();
                await workspaceForCleanup.WaitForPlaylistSummaryDataBuildIdleAsync();
                workspaceForCleanup.Dispose();
            }
            playlistForCleanup?.RequestShutdown("playlist_drop_reference_test");
            libraryForCleanup?.RequestShutdown("playlist_drop_reference_test");
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>ドロップの通知結果の配送を保持する間はフォルダ改名をBusyで拒否し、配送解放後の次の明示改名だけを受理します。</summary>
    /// <remarks>空の通知結果でも到達する任意の購読処理を保持します。実ダイアログ表示ではなく、配送終端までの排他権の寿命を確認します。</remarks>
    [TestMethod]
    public async Task PlaylistMutationAdmission_RejectsCompetingEditUntilDropNotificationCompletes()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        Task? dropTask = null;
        Exception? primaryFailure = null;
        using var releaseSynchronization = new ManualResetEventSlim(false);
        try
        {
            string outputBaseDirectory = Path.Combine(tempDirectory, "custom-folder-output");
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBaseDirectory,
                LR2CustomFolderOutputBaseDirRootType = outputBaseDirectory,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var synchronizationStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            TestLr2PlaylistFolderSynchronizationPort synchronization = BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings,
                synchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>
                {
                    new BMSTable
                    {
                        playlist_id = 7817,
                        name = "Admission target",
                        symbol = "ADMISSION",
                        Output_dir = "AdmissionTarget",
                        Folder_order = ["Imported"]
                    }
                }
            };
            BMSTable table = playlist.BMSTables.Single();
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
                playlist,
                library,
                () => outputSettings);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                if (request.RouteName != "playlist drop custom folder output notification") { return; }
                synchronizationStarted.TrySetResult(true);
                releaseSynchronization.Wait();
            };
            PlaylistWorkspaceMutationRejectedEventArgs? rejected = null;
            workspace.MutationRejected += (_, request) => rejected = request;
            ChartFile chart = (
                ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(
                    CreateSongTableRow(
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempDirectory, "admission-chart.bms")))));

            dropTask = Task.Run(() => workspace.AddRowsToFolderAsync(
                [LibraryChartRow.FromChartFile(chart)],
                table,
                PlaylistFolderNode.CreateFolder("Imported")));
            // 到達前に導入が失敗した場合も観測し、実時間の制限では同期順序を判定しない。
            Task reached = await Task.WhenAny(synchronizationStarted.Task, dropTask);
            if (reached == dropTask)
            {
                await dropTask;
                Assert.IsTrue(synchronizationStarted.Task.IsCompletedSuccessfully);
            }
            await synchronizationStarted.Task;

            await workspace.RenameFolderAsync(
                table,
                PlaylistFolderNode.CreateFolder("Imported"),
                "Renamed");

            Assert.IsNotNull(rejected);
            Assert.AreEqual(PlaylistWorkspaceMutationKind.RenameFolder, rejected!.Kind);
            Assert.IsTrue(rejected!.IsBusy);
            Assert.IsFalse(rejected!.IsStale);
            Assert.IsTrue(table.entries.Any(entry => entry.folder == "Imported"));
            Assert.IsFalse(table.entries.Any(entry => entry.folder == "Renamed"));

            releaseSynchronization.Set();
            await dropTask!;
            await workspace.RenameFolderAsync(
                table,
                PlaylistFolderNode.CreateFolder("Imported"),
                "Renamed");

            Assert.IsTrue(table.entries.Any(entry => entry.folder == "Renamed"));
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            releaseSynchronization.Set();
            if (dropTask != null)
            {
                try
                {
                    await dropTask;
                }
                catch (Exception cleanupFailure) when (primaryFailure is not null)
                {
                    Trace.WriteLine("導入通知の後片付け失敗: " + cleanupFailure);
                }
            }
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>古い表参照の行削除を現表ID・hashへ解決し、同タイトル別hashをstaleとして拒否して一致hashだけを削除します。</summary>
    [TestMethod]
    public async Task PlaylistEntryRemoval_ResolvesStaleIdentityAndRejectsNameOnlyFallback()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            CustomFolderOutputSettingsSnapshot outputSettings = new();
            var currentEntry = new BMSTableEntry
            {
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                title = "Stable title",
                folder = "Folder"
            };
            var currentTable = new BMSTable
            {
                playlist_id = 7818,
                name = "Current table",
                entries = [currentEntry],
                Folder_order = ["Folder"]
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings,
                null)
            {
                BMSTables = new ObservableCollection<BMSTable>([currentTable])
            };
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
                playlist,
                library,
                () => outputSettings);
            List<PlaylistWorkspaceMutationRejectedEventArgs> rejections = [];
            workspace.MutationRejected += (_, request) => rejections.Add(request);
            var staleTable = new BMSTable
            {
                playlist_id = currentTable.playlist_id,
                name = "Old table"
            };
            var sameTitleDifferentHash = new BMSTableEntry
            {
                playlist_id = currentTable.playlist_id,
                parent = staleTable,
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                title = currentEntry.title,
                folder = "Folder"
            };

            await workspace.DeleteSelectedEntriesAsync([
                new PlaylistDetailSourceRow(sameTitleDifferentHash, resolvedChart: null)]);

            Assert.AreEqual(1, currentTable.GetEntriesExceptDummy().Count());
            Assert.IsTrue(rejections.Any(request =>
                request.Kind == PlaylistWorkspaceMutationKind.RemoveEntries
                && request.IsStale
                && !request.IsBusy));

            rejections.Clear();
            var staleEntry = new BMSTableEntry
            {
                playlist_id = currentTable.playlist_id,
                parent = staleTable,
                md5 = currentEntry.md5,
                folder = currentEntry.folder,
                title = currentEntry.title
            };
            await workspace.DeleteSelectedEntriesAsync([
                new PlaylistDetailSourceRow(staleEntry, resolvedChart: null)]);

            Assert.AreEqual(0, currentTable.GetEntriesExceptDummy().Count());
            Assert.AreEqual(0, rejections.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>通常playlist行のdropを許可し、summary混在・特殊folder・外部同期表を拒否して禁止対象へのdrop受付を防ぎます。</summary>
    [TestMethod]
    public void PlaylistWorkspaceDropPolicyRejectsMixedExternalAndSpecialTargets()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        var table = new BMSTable();
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder");
        PlaylistDetailRow playlistRow = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();
        var specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        Assert.IsTrue(workspace.CanAcceptDrop([playlistRow], table, PlaylistFolderNode.CreateFolder("Folder")));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow, new PlaylistSummaryRow()], table));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table, specialFolder));

        table.is_external_sync = true;
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table));
    }

    /// <summary>store未接続でも外部同期表への作成・改名・追加・削除を各種別の拒否として返し、永続化へ到達して未接続例外を起こしません。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceExternalMutationRejectsBeforePersistenceAccess()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        var table = new BMSTable { is_external_sync = true };
        var rejectedKinds = new List<PlaylistWorkspaceMutationKind>();
        workspace.MutationRejected += (_, request) => rejectedKinds.Add(request.Kind);

        await workspace.CreateFolderAsync(table);
        await workspace.RenameFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"), "Renamed");
        await workspace.AddRowsToFolderAsync([], table);
        await workspace.DeleteSelectedEntriesAsync([
            new PlaylistDetailSourceRow(new BMSTableEntry { parent = table }, resolvedChart: null)]);

        CollectionAssert.AreEqual(
            new[]
            {
                PlaylistWorkspaceMutationKind.CreateFolder,
                PlaylistWorkspaceMutationKind.RenameFolder,
                PlaylistWorkspaceMutationKind.AddEntries,
                PlaylistWorkspaceMutationKind.RemoveEntries
            },
            rejectedKinds);
    }

    /// <summary>空選択とnull・無関係なobjectを削除へ渡しても処理を開始せず、store未接続の永続化へ到達しないことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceDeleteSelectedEntries_IgnoresEmptyAndNonPlaylistRows()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        await workspace.DeleteSelectedEntriesAsync([]);
        await workspace.DeleteSelectedEntriesAsync([null!, new object()]);
    }

    /// <summary>特殊folderへの改名・削除・追加はstore未接続でも処理を開始せず、特殊選択を通常folderとして永続化へ渡しません。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSpecialFolderMutationIsIgnoredWithoutPersistenceAccess()
    {
        PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(PlaylistWorkspaceTestPorts.PlaylistStoreProvider, () => null!);
        var table = new BMSTable();
        var specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        await workspace.RenameFolderAsync(table, specialFolder, "Renamed");
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, specialFolder);
        await workspace.AddRowsToFolderAsync([], table, specialFolder);
    }

    /// <summary>空文字・空白名の実folderへ所持追加・同表移動・別表コピーし、root分類せず正確な所属キーと編集値をDBへ保存します。同一所属の再drop、非対象行と元ファイルも保持します。</summary>
    [DataTestMethod]
    [DataRow("", "library")]
    [DataRow("", "move")]
    [DataRow("", "copy")]
    [DataRow("   ", "library")]
    [DataRow("   ", "move")]
    [DataRow("   ", "copy")]
    public async Task PlaylistFolderDrop_EmptyNamedNodeKeepsExactMembership(string folderName, string inputKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-empty-folder-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        BMSPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string keptHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var sourceEntry = new BMSTableEntry
            {
                md5 = hash,
                title = "Edited title",
                folder = "Source",
                memo = "Input memo",
                comment = "Input comment",
                Url = new Uri("https://example.test/input"),
                Url_diff = new Uri("https://example.test/input-diff"),
                name_diff = "Input diff",
                level = 17.25,
                adddate = new DateTime(2025, 2, 3, 4, 5, 6)
            };
            var kept = new BMSTableEntry { md5 = keptHash, title = "Kept", folder = folderName, memo = "Kept memo", level = 23.5 };
            var target = new BMSTable
            {
                playlist_id = 7830,
                name = "Exact folder target",
                Output_dir = "ExactFolderTarget",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder,
                is_root_folder = true,
                Folder_order = [folderName, "Source"],
                entries = inputKind == "move" ? [kept, sourceEntry] : [kept]
            };
            string path = Path.Combine(directory, "charts", "input.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            byte[] bytes = Encoding.ASCII.GetBytes("#TITLE Input\n#BPM 120\n");
            File.WriteAllBytes(path, bytes);
            ChartFile chart = CreatePlaylistDropBmsFile(hash, "Catalog title", path);
            (playlist, library, _, workspace) = CreateRootDropStore(directory, target, [chart]);
            var sourceTable = new BMSTable
            {
                playlist_id = 7831,
                name = "Copy source",
                Output_dir = "CopySource",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder
            };
            if (inputKind == "copy")
            {
                sourceTable.entries = [sourceEntry];
                playlist.BMSTables.Add(sourceTable);
                playlist.CommitBMSTableWithEntriesToDB(sourceTable);
            }
            sourceEntry.ApplyRuntimeUrlCompletion(new Uri("https://example.test/runtime"), null, overwriteExisting: true);
            object row = inputKind == "library" ? LibraryChartRow.FromChartFile(chart) : new PlaylistDetailSourceRow(sourceEntry, chart).CreateViewRow();
            var node = PlaylistFolderNode.CreateFolder(folderName);

            await workspace.AddRowsToFolderAsync([row], target, node);

            BMSTableEntry current = target.entries.Single(entry => entry.md5 == hash && !entry.is_removed);
            Assert.AreEqual(folderName, current.folder);
            string[] expectedCharts = [hash + "|" + folderName, keptHash + "|" + folderName];
            CollectionAssert.AreEquivalent(expectedCharts, target.entries.Where(entry => !entry.is_removed && entry.md5 != BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
                .Select(entry => entry.md5 + "|" + entry.folder).ToArray());
            // 最後の譜面を移した通常名Sourceは、既存の空folder保持規則による専用placeholderだけを残します。
            string[] expectedPlaceholders = inputKind == "move" ? ["Source"] : [];
            CollectionAssert.AreEquivalent(expectedPlaceholders, target.entries.Where(entry => !entry.is_removed && entry.md5 == BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
                .Select(entry => entry.folder).ToArray());
            Assert.AreEqual("Kept memo", kept.memo);
            Assert.AreEqual(23.5, kept.level);
            if (inputKind != "library")
            {
                Assert.AreEqual("Input memo", current.memo);
                Assert.AreEqual("Input comment", current.comment);
                Assert.AreEqual("https://example.test/input", current.Url.AbsoluteUri);
                Assert.AreEqual("https://example.test/input-diff", current.Url_diff.AbsoluteUri);
                Assert.AreEqual("Input diff", current.name_diff);
                Assert.AreEqual(17.25, current.level);
                Assert.AreEqual(new DateTime(2025, 2, 3, 4, 5, 6), current.adddate);
            }
            PlaylistEntryValue before = current.CaptureValue();
            int revision = target.CaptureEntriesReadSnapshot().Revision;
            await workspace.AddRowsToFolderAsync([new PlaylistDetailSourceRow(current, chart).CreateViewRow()], target, node);
            Assert.AreSame(current, target.entries.Single(entry => entry.md5 == hash && !entry.is_removed));
            Assert.AreEqual(before, current.CaptureValue(), "同じ所属への再dropで値・日時を変更しません。");
            Assert.AreEqual(revision, target.CaptureEntriesReadSnapshot().Revision);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(Path.Combine(directory, "song.db")).OpenSongDbReadOnly();
            LR2SongDBExtended.playlist_entry saved = db.Table<LR2SongDBExtended.playlist_entry>()
                .Single(entry => entry.playlist_id == target.playlist_id && entry.md5 == hash && !entry.is_removed);
            CollectionAssert.AreEquivalent(expectedCharts, db.Table<LR2SongDBExtended.playlist_entry>()
                .Where(entry => entry.playlist_id == target.playlist_id && !entry.is_removed && entry.md5 != BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
                .Select(entry => entry.md5 + "|" + entry.folder).ToArray());
            CollectionAssert.AreEquivalent(expectedPlaceholders, db.Table<LR2SongDBExtended.playlist_entry>()
                .Where(entry => entry.playlist_id == target.playlist_id && !entry.is_removed && entry.md5 == BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
                .Select(entry => entry.folder).ToArray());
            Assert.AreEqual(folderName, saved.folder);
            Assert.AreEqual(current.memo, saved.memo);
            Assert.AreEqual(current.comment, saved.comment);
            Assert.AreEqual(current.Url?.AbsoluteUri ?? string.Empty, saved.url ?? string.Empty);
            Assert.AreEqual(current.Url_diff?.AbsoluteUri ?? string.Empty, saved.url_diff ?? string.Empty);
            Assert.AreEqual(current.level, saved.level);
            Assert.AreEqual(current.adddate, saved.adddate);
            Assert.AreEqual(0, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == target.playlist_id && entry.md5 == hash && entry.folder == "Source" && !entry.is_removed));
            LR2SongDBExtended.playlist_entry savedKept = db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == target.playlist_id && entry.md5 == keptHash);
            Assert.AreEqual(folderName, savedKept.folder);
            Assert.AreEqual("Kept memo", savedKept.memo);
            Assert.AreEqual(23.5, savedKept.level);
            if (inputKind == "copy")
            {
                Assert.AreEqual("Source", sourceEntry.folder);
                LR2SongDBExtended.playlist_entry savedSource = db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == sourceTable.playlist_id && entry.md5 == hash);
                Assert.AreEqual("Source", savedSource.folder);
                Assert.AreEqual("Input memo", savedSource.memo);
                Assert.AreEqual("https://example.test/input", savedSource.url);
                Assert.AreEqual("https://example.test/input-diff", savedSource.url_diff);
                Assert.AreEqual("Input comment", savedSource.comment);
                Assert.AreEqual("Input diff", savedSource.name_diff);
                Assert.AreEqual(17.25, savedSource.level);
                Assert.AreEqual(new DateTime(2025, 2, 3, 4, 5, 6), savedSource.adddate);
            }
            Assert.AreEqual(path, chart.Path);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                workspace.Dispose();
            }
            playlist?.RequestShutdown("playlist_empty_folder_drop_test");
            library?.RequestShutdown("playlist_empty_folder_drop_test");
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    /// <summary>通常の改名で失効した空名nodeへの追加をstaleとして拒否し、表rootの分類へ読み替えません。</summary>
    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task PlaylistFolderDrop_RenamedEmptyNodeIsRejectedAsStale(string folderName)
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-stale-empty-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var table = new BMSTable
        {
            playlist_id = 7832,
            name = "Stale folder",
            Output_dir = "StaleFolder",
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder,
            is_root_folder = true,
            Folder_order = [folderName],
            entries = [new BMSTableEntry { md5 = new string('b', 32), title = "Existing", folder = folderName }]
        };
        ChartFile chart = CreatePlaylistDropBmsFile(new string('a', 32), "Input", Path.Combine(directory, "input.bms"));
        (BMSPlaylist playlist, TestBmsLibrary library, _, PlaylistWorkspaceViewModel workspace) = CreateRootDropStore(directory, table, [chart]);
        try
        {
            var staleNode = PlaylistFolderNode.CreateFolder(folderName);
            await workspace.RenameFolderAsync(table, staleNode, "Renamed");
            var rejected = new List<PlaylistWorkspaceMutationRejectedEventArgs>();
            workspace.MutationRejected += (_, args) => rejected.Add(args);
            await workspace.AddRowsToFolderAsync([LibraryChartRow.FromChartFile(chart)], table, staleNode);
            Assert.AreEqual(1, rejected.Count);
            Assert.IsTrue(rejected[0].IsStale);
            Assert.AreEqual("Renamed", table.entries.Single().folder);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(Path.Combine(directory, "song.db")).OpenSongDbReadOnly();
            LR2SongDBExtended.playlist_entry saved = db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == table.playlist_id && !entry.is_removed);
            Assert.AreEqual(new string('b', 32), saved.md5);
            Assert.AreEqual("Renamed", saved.folder);
        }
        finally
        {
            workspace.CancelDetailBuilds();
            await workspace.WaitForDetailBuildIdleAsync();
            await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            workspace.Dispose();
            playlist.RequestShutdown("playlist_stale_empty_drop_test");
            library.RequestShutdown("playlist_stale_empty_drop_test");
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    private static (BMSPlaylist Playlist, TestBmsLibrary Library, BMSTable Table, PlaylistWorkspaceViewModel Workspace,
        ChartFile ChartA, ChartFile ChartB, ChartFile ChartC) CreateRootDropWorkingSetFixture(
        string tempDirectory,
        int playlistId)
    {
        string directoryA = Path.Combine(tempDirectory, "directory-a");
        string directoryB = Path.Combine(tempDirectory, "directory-b");
        string directoryC = Path.Combine(tempDirectory, "directory-c");
        ChartFile chartAFile = CreatePlaylistDropBmsFile(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Zeta",
            Path.Combine(directoryA, "zeta.bms"));
        ChartFile chartBFile = CreatePlaylistDropBmsFile(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "Alpha",
            Path.Combine(directoryB, "alpha.bms"));
        ChartFile chartCFile = CreatePlaylistDropBmsFile(
            "cccccccccccccccccccccccccccccccc",
            "Gamma",
            Path.Combine(directoryC, "gamma.bms"));
        ChartFile packageAnchorA = CreatePlaylistDropBmsFile(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Package anchor A",
            Path.Combine(directoryC, "package-anchor-a.bms"));
        ChartFile packageAnchorB = CreatePlaylistDropBmsFile(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "Package anchor B",
            Path.Combine(directoryC, "package-anchor-b.bms"));
        BMSTable table = new()
        {
            playlist_id = playlistId,
            name = "Root drop",
            symbol = "ROOT-DROP-" + playlistId,
            Output_dir = "RootDrop" + playlistId,
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.Folder,
            is_root_folder = true,
            Folder_order = []
        };
        (BMSPlaylist playlist, TestBmsLibrary library, BMSTable activeTable, PlaylistWorkspaceViewModel workspace) =
            CreateRootDropStore(tempDirectory, table, [chartAFile, chartBFile, chartCFile, packageAnchorA, packageAnchorB]);
        return (
            playlist,
            library,
            activeTable,
            workspace,
            (chartAFile),
            (chartBFile),
            (chartCFile));
    }

    private static (BMSPlaylist Playlist, TestBmsLibrary Library, BMSTable Table, PlaylistWorkspaceViewModel Workspace) CreateRootDropStore(
        string tempDirectory,
        BMSTable table,
        IEnumerable<ChartFile> libraryFiles)
    {
        string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        BMSPlaylist playlist = new TestBmsPlaylist(
            songDbPath,
            null,
            null,
            null,
            () => new PlaylistUrlCompletionOptionsSnapshot(),
            () => new BeatorajaBmtOptionsSnapshot(),
            () => new CustomFolderOutputSettingsSnapshot(),
            null)
        {
            BMSTables = new ObservableCollection<BMSTable>([table])
        };
        // 局所ドロップの前提となる既存所属をDBにも用意し、未変更行を全表保存で補わせません。
        playlist.CommitBMSTableWithEntriesToDB(table);
        TestBmsLibrary library = new(songDbPath)
        {
            BmsCharts = [.. libraryFiles ?? []]
        };
        PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
            playlist,
            library,
            () => new CustomFolderOutputSettingsSnapshot());
        return (playlist, library, table, workspace);
    }

    private static ChartFile CreatePlaylistDropBmsFile(string md5, string title, string path)
    {
        string[] values = CreateSongTableRow(md5, path);
        values[1] = title;
        return ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(values));
    }

    private static void AssertPersistedRootDropEntries(
        string songDbPath,
        int playlistId,
        IReadOnlyList<string> expectedEntries)
    {
        using var database = new LR2SongDBExtended(songDbPath);
        string[] persistedEntries = [.. database.Table<LR2SongDBExtended.playlist_entry>()
            .Where(entry => entry.playlist_id == playlistId && !entry.is_removed)
            .Select(entry => entry.md5 + "|" + entry.folder)
            .OrderBy(value => value, StringComparer.Ordinal)];
        CollectionAssert.AreEqual(expectedEntries.ToArray(), persistedEntries);
    }

    private static void AssertJsonExportSourceUrls(SerializationObservedTable table, Uri? dataUrl)
    {
        Assert.AreEqual(dataUrl, table.Data_url, "シリアライズ中も終端後も正本Data_urlを保持します。");
        Assert.AreEqual(new Uri("https://example.test/export-header.json"), table.Header_url);
        Assert.AreEqual(new Uri("https://example.test/export-page/"), table.Page_url);
    }

    private static SerializationObservedTable CreateJsonExportTable() => new()
    {
        name = "Export",
        symbol = "EX",
        Header_url = new Uri("https://example.test/export-header.json"),
        Page_url = new Uri("https://example.test/export-page/"),
        Folder_order = [],
        entries = [new BMSTableEntry { md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", title = "Export chart", folder = "12", level = 12 }]
    };

    private static void AssertExportedJsonDataReference(string headerPath, string dataPath, bool absoluteFileReference)
    {
        var header = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(headerPath));
        string dataReference = (string?)header["data_url"] ?? throw new AssertFailedException("data_urlを出力します。");
        Assert.IsFalse(string.IsNullOrEmpty(dataReference));
        var referenceUri = new Uri(dataReference, UriKind.RelativeOrAbsolute);
        Assert.AreEqual(absoluteFileReference, referenceUri.IsAbsoluteUri);
        if (absoluteFileReference)
        {
            Assert.IsTrue(dataReference.StartsWith("file:", StringComparison.OrdinalIgnoreCase),
                "rawドライブパスをfile URIの代わりに出力しません。");
            Assert.IsTrue(referenceUri.IsFile);
        }

        // 文書のcanonical file URIを基準にし、implicit-file baseの%二重escapeをoracleへ持ち込みません。
        var headerDocumentUri = new Uri(new Uri(Path.GetFullPath(headerPath)).AbsoluteUri);
        var resolvedDataUri = new Uri(headerDocumentUri, referenceUri);
        Assert.IsTrue(resolvedDataUri.IsAbsoluteUri);
        Assert.IsTrue(resolvedDataUri.IsFile);
        Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(dataPath), resolvedDataUri.LocalPath),
            "出力参照は選択したdata完全パスへ解決します。");
        var data = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(resolvedDataUri.LocalPath));
        Assert.AreEqual("Export", (string?)header["name"]);
        Assert.AreEqual("EX", (string?)header["symbol"]);
        Assert.AreEqual(1, data.Count);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (string?)data[0]["md5"]);
        Assert.AreEqual("Export chart", (string?)data[0]["title"]);
        Assert.AreEqual("12", (string?)data[0]["level"]);
    }

    private sealed class SerializationObservedTable : BMSTable
    {
        internal Action? DataUrlReadObserver { get; set; }

        public override string data_url
        {
            get { DataUrlReadObserver?.Invoke(); return base.data_url; }
            set { base.data_url = value; }
        }
    }

}
