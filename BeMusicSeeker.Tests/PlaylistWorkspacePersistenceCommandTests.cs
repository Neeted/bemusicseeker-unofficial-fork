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
        PlaylistOperationNotificationPresentationRequestedEventArgs observed = null;
        workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => observed = request;
        workspace.ReportBmtOutputFailures(Array.AsReadOnly(new[]
        {
            new BmtTableExportService.FileOperationFailure("owned-output/locked.bmt", "sharing-denied")
        }));
        Assert.IsNotNull(observed);
        var notification = observed.Receipt.Notifications.Single();
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
                [new BMSTable { playlist_id = 1, name = "Backup", symbol = "B", bmt_sort = 1 }],
                out successPlaylist,
                out successNotifications);
            string expectedDump = successPlaylist.GetPlaylistDump();
            string successPath = Path.Combine(tempDirectory, "backup.sql");

            await successWorkspace.BackupPlaylistAsync(successPath);

            Assert.IsTrue(File.Exists(successPath));
            Assert.AreEqual(expectedDump, File.ReadAllText(successPath));
            Assert.AreEqual(1, successNotifications.Count);
            Assert.AreEqual("playlist backup notification", successNotifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                successNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                successNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup);

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

    [TestMethod]
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_RejectsBackgroundSchedulerBeforeDatabaseApply()
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
            StringAssert.Contains(failure.Message, "Playlist restore requires the configured UI thread.");
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                BMSTable existing = verify.Table<BMSTable>().Single();
                Assert.AreEqual(1, existing.playlist_id);
                Assert.AreEqual("Before background restore", existing.name);
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
                out BMSPlaylist _,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications);
            string invalidBackupPath = Path.Combine(tempDirectory, "invalid.sql");
            File.WriteAllText(
                invalidBackupPath,
                string.Join("\v" + Environment.NewLine, ["THIS IS NOT SQL", string.Empty, string.Empty]),
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

        await idle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
                Output_dir = "DropTarget"
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
                Output_dir = "DropAdmission"
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

}
