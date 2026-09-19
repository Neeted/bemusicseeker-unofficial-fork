using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
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

[TestClass]
public sealed class PlaylistWorkspacePersistenceCommandTests
{
    // BMT-N: production callback の受信境界から既存 presentation event へ配送する。
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
        var notification = observed!.Receipt.Notifications.Single();
        Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, notification.Severity);
        StringAssert.Contains(notification.Message, "owned-output/locked.bmt");
        StringAssert.Contains(notification.Message, "sharing-denied");
    }

    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

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
            using (var verifyRoundTrip = new LR2SongDBExtended(roundTripDbPath))
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

            string previousBackupPath = Path.Combine(tempDirectory, "previous-backup.sql");
            byte[] previousBackupBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("previous backup bytes");
            File.WriteAllBytes(previousBackupPath, previousBackupBytes);
            Exception? publishFailure = null;
            using (var lockedDestination = new FileStream(
                previousBackupPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                try
                {
                    await successWorkspace.BackupPlaylistAsync(previousBackupPath);
                }
                catch (Exception exception)
                {
                    publishFailure = exception;
                }
            }
            Assert.IsNotNull(publishFailure);
            CollectionAssert.AreEqual(previousBackupBytes, File.ReadAllBytes(previousBackupPath));
            Assert.AreEqual(2, successNotifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                successNotifications[1].Receipt.Notifications.Single().Severity);

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

            Exception? backupFailure = null;
            try
            {
                await failureWorkspace.BackupPlaylistAsync(invalidPath);
            }
            catch (Exception exception)
            {
                backupFailure = exception;
            }
            Assert.IsNotNull(backupFailure);

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
            StringAssert.Contains(failureNotification.Message, backupFailure!.Message);
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

    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess()
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
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES (1, 'Before restore', 'before', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);");
            }

            int schedulerCallCount = 0;
            BMSPlaylist? scheduledPlaylist = null;
            BMSPlaylist playlist;
            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                songDbPath,
                [new BMSTable { playlist_id = 1, name = "Before restore", symbol = "before" }],
                out playlist,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                action =>
                {
                    schedulerCallCount++;
                    action();
                    return Task.CompletedTask;
                });
            scheduledPlaylist = playlist;
            string backupPath = Path.Combine(tempDirectory, "restore.sql");
            File.WriteAllText(backupPath, CreatePlaylistRestoreDump(2, "After restore", "after"), Encoding.UTF8);

            await workspace.RestorePlaylistBackupAsync(backupPath);

            Assert.AreEqual(1, schedulerCallCount);
            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual("playlist restore notification", notifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                notifications[0].Receipt.Notifications.Single().Severity);
            Assert.AreEqual("After restore", playlist.BMSTables.Single().name);
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                BMSTable restored = verify.Table<BMSTable>().Single();
                Assert.AreEqual(2, restored.playlist_id);
                Assert.AreEqual("After restore", restored.name);
            }
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

    // U2-R1/R2/R5: 実 workspace 入口で DB commit、UI 実行、operation completion を分けて観測する。
    // DB Monitor の再取得は process-wide な所有確認のため、他 fixture の DB 保持から隔離する。
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_AwaitsUiCompletionAndRejectsCompetingWrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-restore-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? restore = null;
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
                dbPath, [oldTable], out BMSPlaylist playlist, out var notifications,
                action =>
                {
                    Interlocked.Increment(ref scheduleCount);
                    Task task = ApplyAsync(action);
                    lock (scheduledApplies) { scheduledApplies.Add(task); }
                    accepted.TrySetResult();
                    return task;
                });
            bool collectionOnUi = true;
            playlist.BMSTables.CollectionChanged += (_, _) =>
                collectionOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess();
            string file = Path.Combine(directory, "restore.sql");
            File.WriteAllText(file, CreatePlaylistRestoreDump(22, "Restored live", "R"), Encoding.UTF8);
            restore = workspace.RestorePlaylistBackupAsync(file);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
            Assert.ThrowsException<InvalidOperationException>(() => playlist.ReloadTables());
            registration = playlist.ExternalSyncOwner.RegistrateExternalTableAsync(
                new BMSTable { name = "Rejected registration", Output_dir = "RejectedRegistration" },
                renameDuplicateName: false, reason: "restore_exclusion");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                registration.WaitAsync(TimeSpan.FromSeconds(5)));
            competingRestore = playlist.RestorePlaylistDumpAsync(CreatePlaylistRestoreDump(33, "Rejected", "X"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                competingRestore.WaitAsync(TimeSpan.FromSeconds(5)));
            execute.TrySetResult();
            await executed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("Restored live", playlist.BMSTables.Single().name);
            Assert.IsTrue(collectionOnUi);
            Assert.IsFalse(restore.IsCompleted);
            Assert.AreEqual(0, notifications.Count);
            Assert.ThrowsException<InvalidOperationException>(() =>
                playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single()));
            complete.TrySetResult();
            await restore;
            Assert.AreEqual(1, scheduleCount);
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
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (InvalidOperationException) when (task == registration || task == competingRestore) { }
                catch when (!assertionsCompleted) { }
            }
            Task[] applies;
            lock (scheduledApplies) { applies = scheduledApplies.ToArray(); }
            foreach (Task apply in applies)
            {
                try { await apply.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch when (!assertionsCompleted) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    // U2-R1: 実 process lock の所有 worker は finally で解放する。時間指定は deadlock の watchdog のみ。
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
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Test lock cleanup watchdog."); }
                }
                finally { if (acquired) { LR2SongDBExtended.Unlock(); } }
            });
            await lockEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 新 async owner API は admission を含む同期 DB 前半を worker へ渡す。
            dispatch = TestUiDispatcherHost.Dispatcher.InvokeAsync(() =>
            {
                restore = playlist.RestorePlaylistDumpAsync(CreatePlaylistRestoreDump(44, "Worker DB", "W"));
                Assert.IsFalse(restore.IsCompleted);
            }).Task;
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            bool marker = false;
            await TestUiDispatcherHost.Dispatcher.InvokeAsync(() => marker = true).Task.WaitAsync(TimeSpan.FromSeconds(5));
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
            if (lockOwner != null) { await lockOwner.WaitAsync(TimeSpan.FromSeconds(10)); }
            if (dispatch != null) { await dispatch.WaitAsync(TimeSpan.FromSeconds(10)); }
            if (restore != null) { await restore.WaitAsync(TimeSpan.FromSeconds(10)); }
            Directory.Delete(directory, recursive: true);
        }
    }

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

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.RestorePlaylistBackupAsync(backupPath));

            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            using (var verify = new LR2SongDBExtended(songDbPath))
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

            Exception? restoreFailure = null;
            try
            {
                await workspace.RestorePlaylistBackupAsync(invalidBackupPath);
            }
            catch (Exception ex)
            {
                restoreFailure = ex;
            }
            Assert.IsNotNull(restoreFailure);

            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            StringAssert.Contains(failure.Message, BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore);
            StringAssert.Contains(failure.Message, restoreFailure!.Message);
            using (var verify = new LR2SongDBExtended(songDbPath))
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

    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_PropagatesFileReadFailureWithoutReceipt()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
        workspace.PlaylistOperationNotificationPresentationRequested +=
            (_, request) => notifications.Add(request);
        string missingPath = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests) + "-" + Guid.NewGuid().ToString("N") + ".sql");

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => workspace.RestorePlaylistBackupAsync(missingPath));

        Assert.AreEqual(0, notifications.Count);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_WritesJsonAndPreservesDataUrl()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            BMSTable table = new BMSTable
            {
                name = "Export",
                symbol = "EX",
                Folder_order = []
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string dataPath = Path.Combine(tempDirectory, "data.json");
            Uri originalDataUrl = table.Data_url;
            table.Data_url = new Uri(Path.GetFileName(dataPath), UriKind.Relative);
            string expectedHeader = table.HeaderToJson();
            string expectedData = (string)table.DataToJson();
            table.Data_url = originalDataUrl;

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, dataPath));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.IsTrue(File.Exists(headerPath));
            Assert.IsTrue(File.Exists(dataPath));
            Assert.AreEqual(expectedHeader, File.ReadAllText(headerPath));
            Assert.AreEqual(expectedData, File.ReadAllText(dataPath));
            Assert.IsNull(table.Data_url);
            Assert.AreEqual(1, notifications.Count);
            Assert.IsTrue(notifications[0].Receipt.IsEmpty);
            Assert.AreEqual(2, dialogs.SaveFilePickerRequests.Count);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Save_header_file, dialogs.SaveFilePickerRequests[0].Title);
            Assert.AreEqual("header.json", dialogs.SaveFilePickerRequests[0].FileName);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.SaveFilePickerRequests[0].Filter);
            Assert.AreEqual(".json", dialogs.SaveFilePickerRequests[0].DefaultExtension);
            Assert.IsTrue(dialogs.SaveFilePickerRequests[0].AddExtension);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Save_data_file, dialogs.SaveFilePickerRequests[1].Title);
            Assert.AreEqual("data.json", dialogs.SaveFilePickerRequests[1].FileName);
            Assert.AreEqual(".json", dialogs.SaveFilePickerRequests[1].DefaultExtension);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.SaveFilePickerRequests[1].Filter);
            Assert.IsTrue(dialogs.SaveFilePickerRequests[1].AddExtension);

            Uri persistedHeaderUrl = new Uri("https://example.test/export-header.json");
            Uri persistedDataUrl = new Uri("https://example.test/export-data.json");
            table.Header_url = persistedHeaderUrl;
            table.Data_url = persistedDataUrl;
            string persistedHeaderPath = Path.Combine(tempDirectory, "persisted-header.json");
            string persistedDataPath = Path.Combine(tempDirectory, "persisted-data.json");
            string expectedPersistedHeader = table.HeaderToJson();

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, persistedHeaderPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, persistedDataPath));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual(persistedDataUrl, table.Data_url);
            Assert.AreEqual(expectedPersistedHeader, File.ReadAllText(persistedHeaderPath));
            Assert.AreEqual(expectedData, File.ReadAllText(persistedDataPath));
            Assert.AreEqual(2, notifications.Count);
            Assert.IsTrue(notifications[1].Receipt.IsEmpty);
            Assert.AreEqual("export-header.json", dialogs.SaveFilePickerRequests[2].FileName);
            Assert.AreEqual(".json", dialogs.SaveFilePickerRequests[2].DefaultExtension);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.SaveFilePickerRequests[2].Filter);
            Assert.IsTrue(dialogs.SaveFilePickerRequests[2].AddExtension);
            Assert.AreEqual("export-data.json", dialogs.SaveFilePickerRequests[3].FileName);
            Assert.AreEqual(".json", dialogs.SaveFilePickerRequests[3].DefaultExtension);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.SaveFilePickerRequests[3].Filter);
            Assert.IsTrue(dialogs.SaveFilePickerRequests[3].AddExtension);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PublishesFileFailureAndPreservesHeaderFirstOrder()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            BMSTable table = new BMSTable { name = "Export failure", Folder_order = [] };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, "partial-header.json");
            string dataDirectory = Path.Combine(tempDirectory, "data-directory");
            Directory.CreateDirectory(dataDirectory);

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, dataDirectory));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.IsTrue(File.Exists(headerPath));
            Assert.IsTrue(Directory.Exists(dataDirectory));
            Assert.IsNull(table.Data_url);
            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure =
                notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                failure.Severity);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist, failure.Message);

            notifications.Clear();
            string invalidHeaderPath = Path.Combine(tempDirectory, "missing", "header.json");
            string validDataPath = Path.Combine(tempDirectory, "unwritten-data.json");

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, invalidHeaderPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, validDataPath));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.IsFalse(File.Exists(invalidHeaderPath));
            Assert.IsFalse(File.Exists(validDataPath));
            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                notifications[0].Receipt.Notifications.Single().Severity);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist,
                notifications[0].Receipt.Notifications.Single().Message);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PickerCancellationStopsBeforeWriting()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            BMSTable table = new BMSTable { name = "Export cancelled", Folder_order = [] };
            string headerPath = Path.Combine(tempDirectory, "cancelled-header.json");
            string dataPath = Path.Combine(tempDirectory, "cancelled-data.json");

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual(1, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual(3, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PickerFailurePropagatesBeforeWriting()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            BMSTable table = new BMSTable { name = "Export picker failure", Folder_order = [] };
            string headerPath = Path.Combine(tempDirectory, "failed-header.json");
            string dataPath = Path.Combine(tempDirectory, "failed-data.json");
            var headerError = new IOException("header picker unavailable");

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: headerError));
            InvalidOperationException headerException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.ExportPlaylistTableAsync(table));
            Assert.AreSame(headerError, headerException.InnerException);
            Assert.AreEqual(1, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            var dataError = new IOException("data picker unavailable");
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: dataError));
            InvalidOperationException dataException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.ExportPlaylistTableAsync(table));
            Assert.AreSame(dataError, dataException.InnerException);
            Assert.AreEqual(3, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyDoesNotPublishSelectionWithoutPendingRestore()
    {
        var restoreRequests = new List<PlaylistSummarySelectionRestoreRequest>();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        workspace.PlaylistSummarySelectionRestoreRequested += restoreRequests.Add;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        Assert.AreEqual(0, restoreRequests.Count);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCancelsSupersededAndHiddenWork()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };

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

    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheReusesContentKeyAcrossDataGenerations()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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

    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheRejectsResultFromBuildBeforeInvalidation()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCannotRestartAfterShutdownStop()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
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

    [TestMethod]
    public void PlaylistWorkspaceDeferredDataRefreshCancelsBuildAndDominatesPresentation()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
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

    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshIsAtomicWithExternalDataPriorityAndModeExit()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
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

    [TestMethod]
    public void PlaylistWorkspaceDataRefreshRequestOwnsVisibilityAndDeferralDecision()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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

    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshDrainOwnsHiddenAndPresentationRoutes()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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

    [TestMethod]
    public async Task PlaylistDropReferenceIndex_IsUpdatedBeforeInvalidationNotification()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
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
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
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
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            const string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            ChartFile chart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(CreateSongTableRow(md5, @"C:\Library\drop-chart.bms")));
            LibraryChartRow libraryRow = LibraryChartRow.FromChartFile(chart);
            PlaylistReferenceDisplay? displayObservedDuringInvalidation = null;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) =>
                displayObservedDuringInvalidation = library.GetPlaylistReferenceDisplay(chart);

            await workspace.AddRowsToFolderAsync(
                [libraryRow],
                table,
                PlaylistFolderNode.CreateFolder("Imported"));

            Assert.IsNotNull(displayObservedDuringInvalidation);
            Assert.AreEqual("DROP", displayObservedDuringInvalidation.Symbols);
            Assert.AreEqual("Drop target", displayObservedDuringInvalidation.Names);
            Assert.AreEqual("DROP", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Drop target", library.GetPlaylistReferenceDisplay(chart).Names);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

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
            BMSFile packageAnchor = CreatePlaylistDropBmsFile(
                existingHash,
                "Package anchor",
                Path.Combine(mergeDirectory, "anchor.bms"));
            BMSFile mergedFile = CreatePlaylistDropBmsFile(
                mergedHash,
                "Merged song",
                Path.Combine(mergeDirectory, "merged.bms"));
            BMSFile sameTitleFile = CreatePlaylistDropBmsFile(
                sameTitleHash,
                "Existing",
                Path.Combine(noPackageDirectory, "same-title.bms"));
            (_, TestBmsLibrary library, BMSTable activeTable, PlaylistWorkspaceViewModel workspace) =
                CreateRootDropStore(tempDirectory, table, [packageAnchor, mergedFile]);

            await workspace.AddRowsToFolderAsync(
                [
                    LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(mergedFile)),
                    LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(sameTitleFile))
                ],
                activeTable);

            Assert.AreEqual(2, activeTable.folder_list.Count(folder => !string.IsNullOrWhiteSpace(folder)));
            Assert.IsTrue(activeTable.GetEntriesExceptDummy().Any(entry =>
                entry.md5 == existingHash && entry.folder == "Existing"));
            Assert.IsTrue(activeTable.GetEntriesExceptDummy().Any(entry =>
                entry.md5 == mergedHash && entry.folder == "Existing"));
            BMSTableEntry sameTitleEntry = activeTable.GetEntriesExceptDummy().Single(entry => entry.md5 == sameTitleHash);
            Assert.AreEqual("Existing (2)", sameTitleEntry.folder);
            Assert.IsTrue(activeTable.GetEntriesExceptDummy().Any(entry =>
                entry.folder == "Existing" && entry.md5 == existingHash));

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
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> reloadResults =
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
            BMSFile packageAnchor = CreatePlaylistDropBmsFile(
                historyHash,
                "Package anchor",
                Path.Combine(dropDirectory, "anchor.bms"));
            BMSFile droppedFile = CreatePlaylistDropBmsFile(
                historyHash,
                "New song",
                Path.Combine(dropDirectory, "new-song.bms"));
            TestBmsLibrary library = new(songDbPath)
            {
                BMSFiles = [packageAnchor, droppedFile]
            };
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
                playlist,
                library);

            await workspace.AddRowsToFolderAsync(
                [LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsFile(droppedFile))],
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

            using var verify = new LR2SongDBExtended(songDbPath);
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

    [TestMethod]
    public async Task PlaylistDropAdmission_IsAtomicAndConvergesAfterLeaseRelease()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
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
            var library = new TestBmsLibrary(songDbPath);
            int leaseAttemptCount = 0;
            int lr2SyncProbeCount = 0;
            int lr2SyncProbeLeaseUnavailableCount = 0;
            var synchronization = BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.SynchronizationStartProbe = mutationCapability =>
            {
                lr2SyncProbeCount++;
                Assert.IsNotNull(mutationCapability);
                using (LibraryFileMutationLease competingLease = library.TryBeginLibraryFileMutation(
                           "playlist_drop_lr2_sync_probe_conflict",
                           showMessage: false))
                {
                    Assert.IsNull(competingLease);
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
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = bmtTablePath
                },
                () => outputSettings,
                synchronization,
                mutationLeaseProviderWithMessage: (operation, showMessage) =>
                {
                    leaseAttemptCount++;
                    return library.TryBeginLibraryFileMutation(operation, showMessage);
                })
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
                playlist,
                library,
                () => outputSettings);
            int referenceInvalidationCount = 0;
            int referenceEffectOutsideLeaseCount = 0;
            int uiInvalidationCount = 0;
            int uiEffectOutsideLeaseCount = 0;
            int notificationCount = 0;
            int notificationEffectOutsideLeaseCount = 0;
            bool notificationCallbackFailure = false;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            int bmtScheduleCount = 0;
            int bmtEffectOutsideLeaseCount = 0;
            List<Func<Task>> scheduledBmtWork = [];
            workspace.PlaylistReferenceSortInvalidationRequested +=
                (_, _) =>
                {
                    using (LibraryFileMutationLease callbackLease = library.TryBeginLibraryFileMutation(
                               "playlist_drop_reference_effect_probe",
                               showMessage: false))
                    {
                        if (callbackLease != null)
                        {
                            referenceEffectOutsideLeaseCount++;
                        }
                    }
                    referenceInvalidationCount++;
                };
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
            playlist.StartupBackgroundTaskScheduler = (owner, _, _, work) =>
            {
                if (owner == "beatoraja_bmt_export")
                {
                    using (LibraryFileMutationLease callbackLease = library.TryBeginLibraryFileMutation(
                               "playlist_drop_bmt_effect_probe",
                               showMessage: false))
                    {
                        if (callbackLease != null)
                        {
                            bmtEffectOutsideLeaseCount++;
                        }
                    }
                    bmtScheduleCount++;
                    scheduledBmtWork.Add(work);
                    return true;
                }
                return false;
            };
            const string md5 = "ffffffffffffffffffffffffffffffff";
            ChartFile chart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(CreateSongTableRow(md5, Path.Combine(tempDirectory, "drop-chart.bms"))));
            LibraryChartRow libraryRow = LibraryChartRow.FromChartFile(chart);
            const string outputFailureMd5 = "11111111111111111111111111111111";
            ChartFile outputFailureChart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(CreateSongTableRow(
                    outputFailureMd5,
                    Path.Combine(tempDirectory, "drop-output-failure-chart.bms"))));
            LibraryChartRow outputFailureLibraryRow = LibraryChartRow.FromChartFile(outputFailureChart);
            const string preparationFailureMd5 = "22222222222222222222222222222222";
            ChartFile preparationFailureChart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(CreateSongTableRow(
                    preparationFailureMd5,
                    Path.Combine(tempDirectory, "drop-preparation-failure-chart.bms"))));
            LibraryChartRow preparationFailureLibraryRow = LibraryChartRow.FromChartFile(preparationFailureChart);
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("Imported"));
            workspace.IsPlaylistDetailViewActive = true;

            using (LibraryFileMutationLease incumbent = library.TryBeginLibraryFileMutation(
                       "playlist_drop_incumbent",
                       showMessage: false))
            {
                Assert.IsNotNull(incumbent);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    workspace.AddRowsToFolderAsync(
                        [libraryRow],
                        table,
                        PlaylistFolderNode.CreateFolder("Imported")));

                Assert.AreEqual(0, table.GetEntriesExceptDummy().Count());
                using (var verifyBusy = new LR2SongDBExtended(songDbPath))
                {
                    Assert.AreEqual(0, verifyBusy.Table<LR2SongDBExtended.playlist_entry>().Count());
                }
                Assert.IsFalse(Directory.Exists(Path.Combine(outputBaseDirectory, table.Output_dir)));
                Assert.AreEqual(0, referenceInvalidationCount);
                Assert.AreEqual(0, referenceEffectOutsideLeaseCount);
                Assert.AreEqual(0, uiInvalidationCount);
                Assert.AreEqual(0, uiEffectOutsideLeaseCount);
                Assert.AreEqual(0, notificationEffectOutsideLeaseCount);
                Assert.AreEqual(0, bmtScheduleCount);
                Assert.AreEqual(0, bmtEffectOutsideLeaseCount);
                Assert.AreEqual(0, lr2SyncProbeCount);
                Assert.AreEqual(0, lr2SyncProbeLeaseUnavailableCount);
            }
            Assert.AreEqual(1, leaseAttemptCount);

            notificationCallbackFailure = true;
            int notificationCountBeforeFreshRequest = notificationCount;
            int leaseAttemptsBeforeFreshRequest = leaseAttemptCount;
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
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, verify.Table<LR2SongDBExtended.playlist_entry>().Count());
                Assert.IsTrue(verify.Table<LR2SongDB.folder>().Any(folder =>
                    generatedFiles.Any(path => string.Equals(path, folder.path, StringComparison.OrdinalIgnoreCase))));
            }
            Assert.AreEqual(1, referenceInvalidationCount);
            Assert.AreEqual(1, referenceEffectOutsideLeaseCount);
            Assert.AreEqual(1, uiInvalidationCount);
            Assert.AreEqual(1, uiEffectOutsideLeaseCount);
            Assert.AreEqual(notificationCountBeforeFreshRequest + 1, notificationCount);
            Assert.AreEqual(1, notificationEffectOutsideLeaseCount);
            Assert.AreEqual(leaseAttemptsBeforeFreshRequest + 1, leaseAttemptCount);
            Assert.AreEqual(1, bmtScheduleCount);
            Assert.AreEqual(1, bmtEffectOutsideLeaseCount);
            Assert.AreEqual(1, lr2SyncProbeCount);
            Assert.AreEqual(1, lr2SyncProbeLeaseUnavailableCount);
            Assert.AreEqual(1, scheduledBmtWork.Count);
            await scheduledBmtWork[0]();

            synchronization.Failure = new InvalidOperationException("playlist drop output failure");
            int notificationCountBeforeOutputFailure = notificationCount;
            int leaseAttemptsBeforeOutputFailure = leaseAttemptCount;
            InvalidOperationException outputFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                workspace.AddRowsToFolderAsync(
                    [outputFailureLibraryRow],
                    table,
                    PlaylistFolderNode.CreateFolder("Fault")));

            Assert.AreEqual("playlist drop output failure", outputFailure.Message);
            Assert.AreSame(synchronization.Failure, outputFailure);
            Assert.AreEqual(leaseAttemptsBeforeOutputFailure + 1, leaseAttemptCount);
            Assert.AreEqual(2, bmtScheduleCount);
            Assert.AreEqual(2, bmtEffectOutsideLeaseCount);
            Assert.AreEqual(2, lr2SyncProbeCount);
            Assert.AreEqual(2, lr2SyncProbeLeaseUnavailableCount);
            Assert.AreEqual(2, scheduledBmtWork.Count);
            using (LibraryFileMutationLease releasedLease = library.TryBeginLibraryFileMutation(
                       "playlist_drop_output_failure_release_probe",
                       showMessage: false))
            {
                Assert.IsNotNull(releasedLease);
            }
            Assert.AreEqual(2, table.GetEntriesExceptDummy().Count());
            using (var verifyOutputFailure = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, verifyOutputFailure.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
            Assert.AreEqual(2, referenceInvalidationCount);
            Assert.AreEqual(2, referenceEffectOutsideLeaseCount);
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
            await scheduledBmtWork[1]();

            synchronization.Failure = null!;
            NotSupportedException preparationFailure = new("playlist drop preparation failure");
            synchronization.PhysicalSurfaceFactory = () => throw preparationFailure;
            int notificationCountBeforePreparationFailure = notificationCount;
            int leaseAttemptsBeforePreparationFailure = leaseAttemptCount;
            NotSupportedException caughtPreparationFailure = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
                workspace.AddRowsToFolderAsync(
                    [preparationFailureLibraryRow],
                    table,
                    PlaylistFolderNode.CreateFolder("PreparationFault")));

            Assert.AreEqual("playlist drop preparation failure", caughtPreparationFailure.Message);
            Assert.AreSame(preparationFailure, caughtPreparationFailure);
            Assert.AreEqual(leaseAttemptsBeforePreparationFailure + 1, leaseAttemptCount);
            Assert.AreEqual(3, table.GetEntriesExceptDummy().Count());
            using (var verifyPreparationFailure = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(3, verifyPreparationFailure.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
            Assert.AreEqual(3, referenceInvalidationCount);
            Assert.AreEqual(3, referenceEffectOutsideLeaseCount);
            Assert.AreEqual(3, uiInvalidationCount);
            Assert.AreEqual(3, uiEffectOutsideLeaseCount);
            Assert.AreEqual(notificationCountBeforePreparationFailure + 1, notificationCount);
            Assert.AreEqual(3, notificationEffectOutsideLeaseCount);
            Assert.AreEqual(3, bmtScheduleCount);
            Assert.AreEqual(3, bmtEffectOutsideLeaseCount);
            Assert.AreEqual(2, lr2SyncProbeCount);
            Assert.AreEqual(2, lr2SyncProbeLeaseUnavailableCount);
            Assert.AreEqual(3, scheduledBmtWork.Count);
            using (LibraryFileMutationLease releasedPreparationLease = library.TryBeginLibraryFileMutation(
                       "playlist_drop_preparation_failure_release_probe",
                       showMessage: false))
            {
                Assert.IsNotNull(releasedPreparationLease);
            }
            Assert.AreEqual("DROP-ADMISSION", library.GetPlaylistReferenceDisplay(preparationFailureChart).Symbols);
            Assert.AreEqual("Drop admission target", library.GetPlaylistReferenceDisplay(preparationFailureChart).Names);
            await scheduledBmtWork[2]();

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
            ChartFile databaseFailureChart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(CreateSongTableRow(
                    databaseFailureMd5,
                    Path.Combine(tempDirectory, "drop-database-failure-chart.bms"))));
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
            StringAssert.Contains(databaseFailure!.Message, "playlist drop DB write failure");
            Assert.AreEqual(notificationCountBeforeDatabaseFailure + 1, notificationCount);
            Assert.AreEqual(notificationEffectCountBeforeDatabaseFailure + 1, notificationEffectOutsideLeaseCount);
            PlaylistOperationNotificationPresentationRequestedEventArgs databaseFailureNotification = notifications[^1];
            Assert.AreEqual(1, databaseFailureNotification.Receipt.Notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification databaseFailureNotice =
                databaseFailureNotification.Receipt.Notifications.Single();
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                databaseFailureNotice.Severity);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

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
            var synchronization = BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.SynchronizationStartProbe = _ =>
            {
                synchronizationStarted.TrySetResult(true);
                releaseSynchronization.Wait();
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
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
            PlaylistWorkspaceMutationRejectedEventArgs? rejected = null;
            workspace.MutationRejected += (_, request) => rejected = request;
            ChartFile chart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(
                    CreateSongTableRow(
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        Path.Combine(tempDirectory, "admission-chart.bms"))));

            dropTask = workspace.AddRowsToFolderAsync(
                [LibraryChartRow.FromChartFile(chart)],
                table,
                PlaylistFolderNode.CreateFolder("Imported"));
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

    [TestMethod]
    public void PlaylistWorkspaceDropPolicyRejectsMixedExternalAndSpecialTargets()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
        var table = new BMSTable();
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder");
        var playlistRow = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();
        var specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        Assert.IsTrue(workspace.CanAcceptDrop([playlistRow], table, PlaylistFolderNode.CreateFolder("Folder")));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow, new PlaylistSummaryRow()], table));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table, specialFolder));

        table.is_external_sync = true;
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table));
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExternalMutationRejectsBeforePersistenceAccess()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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

    [TestMethod]
    public async Task PlaylistWorkspaceDeleteSelectedEntries_IgnoresEmptyAndNonPlaylistRows()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        await workspace.DeleteSelectedEntriesAsync([]);
        await workspace.DeleteSelectedEntriesAsync([null!, new object()]);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSpecialFolderMutationIsIgnoredWithoutPersistenceAccess()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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
        var table = new BMSTable();
        PlaylistFolderNode specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        await workspace.RenameFolderAsync(table, specialFolder, "Renamed");
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, specialFolder);
        await workspace.AddRowsToFolderAsync([], table, specialFolder);
    }

    private static (BMSPlaylist Playlist, TestBmsLibrary Library, BMSTable Table, PlaylistWorkspaceViewModel Workspace,
        ChartFile ChartA, ChartFile ChartB, ChartFile ChartC) CreateRootDropWorkingSetFixture(
        string tempDirectory,
        int playlistId)
    {
        string directoryA = Path.Combine(tempDirectory, "directory-a");
        string directoryB = Path.Combine(tempDirectory, "directory-b");
        string directoryC = Path.Combine(tempDirectory, "directory-c");
        BMSFile chartAFile = CreatePlaylistDropBmsFile(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Zeta",
            Path.Combine(directoryA, "zeta.bms"));
        BMSFile chartBFile = CreatePlaylistDropBmsFile(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "Alpha",
            Path.Combine(directoryB, "alpha.bms"));
        BMSFile chartCFile = CreatePlaylistDropBmsFile(
            "cccccccccccccccccccccccccccccccc",
            "Gamma",
            Path.Combine(directoryC, "gamma.bms"));
        BMSFile packageAnchorA = CreatePlaylistDropBmsFile(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Package anchor A",
            Path.Combine(directoryC, "package-anchor-a.bms"));
        BMSFile packageAnchorB = CreatePlaylistDropBmsFile(
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
            ChartFileProjection.FromBmsFile(chartAFile),
            ChartFileProjection.FromBmsFile(chartBFile),
            ChartFileProjection.FromBmsFile(chartCFile));
    }

    private static (BMSPlaylist Playlist, TestBmsLibrary Library, BMSTable Table, PlaylistWorkspaceViewModel Workspace) CreateRootDropStore(
        string tempDirectory,
        BMSTable table,
        IEnumerable<BMSFile> libraryFiles)
    {
        string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(tempDirectory);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        BMSPlaylist playlist = new TestBmsPlaylist(
            songDbPath,
            null,
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
        TestBmsLibrary library = new(songDbPath)
        {
            BMSFiles = [.. libraryFiles ?? []]
        };
        PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(
            playlist,
            library,
            () => new CustomFolderOutputSettingsSnapshot());
        return (playlist, library, table, workspace);
    }

    private static BMSFile CreatePlaylistDropBmsFile(string md5, string title, string path)
    {
        string[] values = CreateSongTableRow(md5, path);
        values[1] = title;
        return BMSFile.FromSongTableRawValues(values);
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

}
