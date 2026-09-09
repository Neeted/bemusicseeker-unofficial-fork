using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Ribbit.Util.Extensions;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
// Arbitrary filtered Quick runs share one testhost. This fixture mutates the
// process-global playlist URL completion, LR2 mode/root/output paths, Beatoraja
// output settings, and IR flag in Settings.Default.
[DoNotParallelize]
public sealed class BmsPlaylistPersistenceLifecycleTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void PlaylistPersistenceRepository_CustomFolderOutputStatusRoundTripsAndDeletes()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var repository = new PlaylistPersistenceRepository(songDbPath);
            var expected = new CustomFolderOutputStatusRow
            {
                PlaylistId = 7101,
                OutputDirectory = "C:\\CustomFolder\\Table",
                IsRootFolder = 1,
                IgnoreFolderOutput = 4,
                EntryType = 2,
                FolderSortKey = 3,
                FolderSortAscending = 0,
                EnableUnsent = 1,
                HeaderSha256 = "header",
                DataSha256 = "data",
                LastUpdateTicks = 123456789L,
                PhysicalMtimeSignature = "mtime"
            };

            repository.PersistCustomFolderOutputStatusRows([expected]);

            CustomFolderOutputStatusRow actual = repository.ReadCustomFolderOutputStatusRows().Single().Value;
            Assert.AreEqual(expected.PlaylistId, actual.PlaylistId);
            Assert.AreEqual(expected.OutputDirectory, actual.OutputDirectory);
            Assert.AreEqual(expected.IsRootFolder, actual.IsRootFolder);
            Assert.AreEqual(expected.IgnoreFolderOutput, actual.IgnoreFolderOutput);
            Assert.AreEqual(expected.EntryType, actual.EntryType);
            Assert.AreEqual(expected.FolderSortKey, actual.FolderSortKey);
            Assert.AreEqual(expected.FolderSortAscending, actual.FolderSortAscending);
            Assert.AreEqual(expected.EnableUnsent, actual.EnableUnsent);
            Assert.AreEqual(expected.HeaderSha256, actual.HeaderSha256);
            Assert.AreEqual(expected.DataSha256, actual.DataSha256);
            Assert.AreEqual(expected.LastUpdateTicks, actual.LastUpdateTicks);
            Assert.AreEqual(expected.PhysicalMtimeSignature, actual.PhysicalMtimeSignature);

            repository.DeleteCustomFolderOutputStatus(expected.PlaylistId);
            Assert.AreEqual(0, repository.ReadCustomFolderOutputStatusRows().Count);
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
    [TestCategory("Playlist")]
    public void EnsureSchema_DoesNotMigrateLastPlaySortOutputMaskOrCreateLegacyMarker()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            const int playlistId = 6101;
            const int legacyMask = 0x7FE;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = playlistId,
                    name = "LegacyMask",
                    symbol = "LM",
                    ignore_folder_output = (LR2SongDBExtended.playlist.CustomFolderType)legacyMask
                }, typeof(LR2SongDBExtended.playlist));
            }

            PlaylistPersistenceRepository.EnsureSchema(songDbPath);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                legacyMask,
                verify.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = ?;", playlistId));
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'app_schema_version';"));
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
    [TestCategory("Playlist")]
    public void PlaylistLocalMutation_DatabaseFailureRestoresLiveStateAndAllowsFollowingSave()
    {
        string[] mutationKinds = ["rename-folder", "create-folder", "remove-folder", "remove-entry", "drop"];
        foreach (string mutationKind in mutationKinds)
        {
            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "BmsPlaylistUpdateTests",
                "mutation-failure-" + mutationKind + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                string songDbPath = CreateTempSongDbPath(tempDirectory);
                PlaylistPersistenceRepository.EnsureSchema(songDbPath);
                const int playlistId = 9071;
                BMSTableEntry targetEntry = CreateEntry(
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "Original");
                targetEntry.playlist_id = playlistId;
                targetEntry.title = "Target before failure";
                BMSTableEntry retainedEntry = CreateEntry(
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "Keep");
                retainedEntry.playlist_id = playlistId;
                retainedEntry.title = "Retained before failure";
                BMSTable table = new()
                {
                    playlist_id = playlistId,
                    name = "Mutation failure target",
                    symbol = "MF",
                    last_update = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Local),
                    entries = [targetEntry, retainedEntry]
                };
                table.Folder_order = ["Original", "Keep"];
                using (var setup = new LR2SongDBExtended(songDbPath))
                {
                    setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    setup.InsertOrReplace(targetEntry, typeof(LR2SongDBExtended.playlist_entry));
                    setup.InsertOrReplace(retainedEntry, typeof(LR2SongDBExtended.playlist_entry));
                }

                var playlist = new TestBmsPlaylist(
                    songDbPath,
                    null,
                    null,
                    null,
                    null,
                    () => new PlaylistUrlCompletionOptionsSnapshot(),
                    () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = false },
                    () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                    CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                        songDbPath,
                        CustomFolderOutputPhysicalSurface.Empty))
                {
                    BMSTables = new ObservableCollection<BMSTable>([table])
                };

                int initialRevision = table.PlaylistEntriesRevision;
                DateTime initialLastUpdate = table.last_update;
                string[] initialFolderOrder = [.. table.Folder_order];
                string[] initialEntryFolders = [targetEntry.folder, retainedEntry.folder];
                const string triggerName = "playlist_mutation_test_write_failure";
                using (var failDatabaseWrite = new LR2SongDBExtended(songDbPath))
                {
                    failDatabaseWrite.Execute(
                        "CREATE TRIGGER " + triggerName + " "
                        + "BEFORE INSERT ON playlist WHEN NEW.playlist_id = 9071 "
                        + "BEGIN SELECT RAISE(ABORT, 'playlist mutation DB write failure'); END;");
                }

                Exception failure = null;
                try
                {
                    ApplyMutationForFailureCase(
                        playlist,
                        table,
                        targetEntry,
                        mutationKind,
                        "FailedFolder");
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                Assert.IsNotNull(failure, mutationKind);
                StringAssert.Contains(failure.ToString(), "playlist mutation DB write failure", mutationKind);
                Assert.AreEqual(initialRevision, table.PlaylistEntriesRevision, mutationKind);
                Assert.AreEqual(initialLastUpdate, table.last_update, mutationKind);
                CollectionAssert.AreEqual(initialFolderOrder, table.Folder_order, mutationKind);
                CollectionAssert.AreEqual(
                    new BMSTableEntry[] { targetEntry, retainedEntry },
                    table.entries,
                    mutationKind);
                Assert.AreSame(table, targetEntry.parent, mutationKind);
                Assert.AreSame(table, retainedEntry.parent, mutationKind);
                CollectionAssert.AreEqual(initialEntryFolders, new[] { targetEntry.folder, retainedEntry.folder }, mutationKind);
                Assert.AreEqual("Target before failure", targetEntry.title, mutationKind);
                Assert.AreEqual("Retained before failure", retainedEntry.title, mutationKind);

                using (var verifyFailedWrite = new LR2SongDBExtended(songDbPath))
                {
                    BMSTable persistedTable = verifyFailedWrite.Table<BMSTable>()
                        .Single(item => item.playlist_id == playlistId);
                    Assert.AreEqual("Mutation failure target", persistedTable.name, mutationKind);
                    CollectionAssert.AreEqual(initialFolderOrder, persistedTable.Folder_order, mutationKind);
                    List<BMSTableEntry> persistedEntries = [.. verifyFailedWrite.Table<BMSTableEntry>()
                        .Where(item => item.playlist_id == playlistId && !item.is_removed)
                        .OrderBy(item => item.md5, StringComparer.Ordinal)];
                    CollectionAssert.AreEqual(
                        new[] { targetEntry.md5, retainedEntry.md5 },
                        persistedEntries.Select(item => item.md5).ToArray(),
                        mutationKind);
                    Assert.IsTrue(persistedEntries.All(item =>
                        item.title is "Target before failure" or "Retained before failure"), mutationKind);
                }

                using (var removeFailureTrigger = new LR2SongDBExtended(songDbPath))
                {
                    removeFailureTrigger.Execute("DROP TRIGGER " + triggerName + ";");
                }

                ApplyMutationAfterFailure(playlist, table, targetEntry, mutationKind, "AfterFailure");

                using (var verifyFollowingWrite = new LR2SongDBExtended(songDbPath))
                {
                    BMSTable persistedTable = verifyFollowingWrite.Table<BMSTable>()
                        .Single(item => item.playlist_id == playlistId);
                    List<BMSTableEntry> persistedEntries = [.. verifyFollowingWrite.Table<BMSTableEntry>()
                        .Where(item => item.playlist_id == playlistId && !item.is_removed)];
                    AssertFollowingMutationState(
                        persistedTable,
                        persistedEntries,
                        targetEntry,
                        mutationKind);
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
    }

    private static void ApplyMutationForFailureCase(
        BMSPlaylist playlist,
        BMSTable table,
        BMSTableEntry targetEntry,
        string mutationKind,
        string folderName)
    {
        switch (mutationKind)
        {
            case "rename-folder":
                playlist.RenameFolderBMSTable(table, "Original", folderName);
                return;
            case "create-folder":
                playlist.CreateNewFolderBMSTable(table, folderName);
                return;
            case "remove-folder":
                playlist.RemoveFolderBMSTable(table, "Original");
                return;
            case "remove-entry":
                playlist.RemoveEntriesBMSTable([targetEntry], table);
                return;
            case "drop":
                BMSTableEntry droppedEntry = CreateEntry(
                    "cccccccccccccccccccccccccccccccc",
                    folderName);
                droppedEntry.playlist_id = table.playlist_id;
                PlaylistDropMutationResult result = playlist.ApplyPlaylistDropMutation(
                    table,
                    "PlaylistLocalMutation_DatabaseFailure",
                    folderName,
                    [],
                    [droppedEntry],
                    []);
                Assert.IsTrue(result.Applied);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutationKind), mutationKind, null);
        }
    }

    private static void ApplyMutationAfterFailure(
        BMSPlaylist playlist,
        BMSTable table,
        BMSTableEntry targetEntry,
        string mutationKind,
        string folderName)
    {
        switch (mutationKind)
        {
            case "rename-folder":
                playlist.RenameFolderBMSTable(table, "Original", folderName);
                return;
            case "create-folder":
                playlist.CreateNewFolderBMSTable(table, folderName);
                return;
            case "remove-folder":
                playlist.RemoveFolderBMSTable(table, "Original");
                return;
            case "remove-entry":
                playlist.RemoveEntriesBMSTable([targetEntry], table);
                return;
            case "drop":
                BMSTableEntry droppedEntry = CreateEntry(
                    "dddddddddddddddddddddddddddddddd",
                    folderName);
                droppedEntry.playlist_id = table.playlist_id;
                PlaylistDropMutationResult result = playlist.ApplyPlaylistDropMutation(
                    table,
                    "PlaylistLocalMutation_FollowingSave",
                    folderName,
                    [],
                    [droppedEntry],
                    []);
                Assert.IsTrue(result.Applied);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutationKind), mutationKind, null);
        }
    }

    private static void AssertFollowingMutationState(
        BMSTable persistedTable,
        IReadOnlyList<BMSTableEntry> persistedEntries,
        BMSTableEntry targetEntry,
        string mutationKind)
    {
        switch (mutationKind)
        {
            case "rename-folder":
                CollectionAssert.Contains(persistedTable.Folder_order, "AfterFailure", mutationKind);
                Assert.IsTrue(persistedEntries.Any(entry => entry.md5 == targetEntry.md5 && entry.folder == "AfterFailure"), mutationKind);
                Assert.IsFalse(persistedEntries.Any(entry => entry.folder == "FailedFolder"), mutationKind);
                return;
            case "create-folder":
                Assert.IsTrue(
                    persistedEntries.Any(entry =>
                        entry.md5 == "00000000000000000000000000000000"
                        && entry.folder == "AfterFailure"),
                    mutationKind);
                Assert.IsFalse(persistedEntries.Any(entry => entry.folder == "FailedFolder"), mutationKind);
                return;
            case "remove-folder":
                CollectionAssert.DoesNotContain(persistedTable.Folder_order, "Original", mutationKind);
                Assert.IsTrue(persistedEntries.Any(entry => entry.md5 == targetEntry.md5 && string.IsNullOrEmpty(entry.folder)), mutationKind);
                return;
            case "remove-entry":
                Assert.IsFalse(persistedEntries.Any(entry => entry.md5 == targetEntry.md5), mutationKind);
                Assert.IsTrue(persistedEntries.Any(entry => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), mutationKind);
                return;
            case "drop":
                Assert.IsTrue(persistedEntries.Any(entry => entry.md5 == "dddddddddddddddddddddddddddddddd" && entry.folder == "AfterFailure"), mutationKind);
                Assert.IsFalse(persistedEntries.Any(entry => entry.md5 == "cccccccccccccccccccccccccccccccc"), mutationKind);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutationKind), mutationKind, null);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceMutation_ReportsDetailContentChangeForCurrentTable()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                playlist_id = 9012,
                name = "WorkspaceMutation",
                symbol = "WM",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Mutation")]
            };
            BMSTableEntry entry = table.entries.Single();
            entry.folder = "Mutation";
            entry.parent = table;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath);
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
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
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => library,
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
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, dialogs);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("Mutation"));
            workspace.IsPlaylistDetailViewActive = true;

            int detailReloadCount = 0;
            int referenceSortInvalidationCount = 0;
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) => detailReloadCount++;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => referenceSortInvalidationCount++;
            long initialRevision = workspace.DetailViewState.Source.PlaylistContentRevision;

            await workspace.RenameFolderAsync(table, PlaylistFolderNode.CreateFolder("Mutation"), "Renamed");
            Assert.AreEqual("Renamed", workspace.CapturePlaylistDetailSelection()!.FolderName);
            await workspace.CreateFolderAsync(table);
            await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Renamed"));
            Assert.AreEqual(string.Empty, workspace.CapturePlaylistDetailSelection()!.FolderName);
            await workspace.DeleteSelectedEntriesAsync([
                new PlaylistDetailSourceRow(entry, resolvedChart: null)]);

            Assert.AreEqual(4, detailReloadCount);
            Assert.AreEqual(4, referenceSortInvalidationCount);
            Assert.AreEqual(initialRevision + detailReloadCount, workspace.DetailViewState.Source.PlaylistContentRevision);
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
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceDeleteSelectedEntries_GroupsRowsByParentAndPersistsEachTable()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable firstTable = new()
            {
                playlist_id = 9013,
                name = "First delete group",
                entries = [CreateEntry("11111111111111111111111111111111", "First")]
            };
            BMSTable secondTable = new()
            {
                playlist_id = 9014,
                name = "Second delete group",
                entries = [CreateEntry("22222222222222222222222222222222", "Second")]
            };
            BMSTableEntry firstEntry = firstTable.entries.Single();
            BMSTableEntry secondEntry = secondTable.entries.Single();
            firstEntry.playlist_id = firstTable.playlist_id;
            secondEntry.playlist_id = secondTable.playlist_id;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(firstTable, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(secondTable, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(firstEntry, typeof(LR2SongDBExtended.playlist_entry));
                setup.InsertOrReplace(secondEntry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([firstTable, secondTable])
            };
            var library = new TestBmsLibrary(songDbPath);
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
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
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => library,
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
                (exception, message) => { },
                (_, _) => false,
                (_, _) => false,
                PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler,
                PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck,
                dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);

            await workspace.DeleteSelectedEntriesAsync([
                new PlaylistDetailSourceRow(firstEntry, resolvedChart: null),
                new PlaylistDetailSourceRow(secondEntry, resolvedChart: null)]);

            Assert.IsFalse(firstTable.entries.Contains(firstEntry));
            Assert.IsFalse(secondTable.entries.Contains(secondEntry));
            Assert.AreEqual(2, notifications.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<BMSTableEntry>().Count(entry =>
                (entry.playlist_id == firstTable.playlist_id && entry.md5 == firstEntry.md5
                    || entry.playlist_id == secondTable.playlist_id && entry.md5 == secondEntry.md5)
                && !entry.is_removed));
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
    [TestCategory("Playlist")]
    public void PlaylistTreeTables_TrackStoreReplacementAndPreserveCollectionIdentity()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([new BMSTable { name = "Initial" }])
            };
            var viewModel = MainWindowViewModelTestFactory.Create();
            List<string> workspacePropertyNames = [];
            int playlistTablesPresentationChangedCount = 0;
            viewModel.PlaylistWorkspace.PropertyChanged += (_, e) => workspacePropertyNames.Add(e.PropertyName);
            viewModel.PlaylistWorkspace.PlaylistTablesPresentationChanged += (_, _) => playlistTablesPresentationChangedCount++;

            Assert.AreEqual(0, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);

            viewModel.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);

            Assert.AreSame(playlist.BMSTables, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            Assert.AreEqual(1, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);
            CollectionAssert.AreEqual(
                new[] { nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables) },
                workspacePropertyNames);

            playlist.BMSTables.Add(new BMSTable { name = "Added" });
            TestUiDispatcherHost.Drain();
            Assert.AreSame(playlist.BMSTables, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            Assert.AreEqual(2, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);
            Assert.AreEqual(1, playlistTablesPresentationChangedCount);
            ObservableCollection<BMSTable> replacement = new(
                [new BMSTable { name = "Replacement" }]);
            playlist.BMSTables = replacement;
            TestUiDispatcherHost.Drain();

            Assert.AreSame(replacement, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            CollectionAssert.AreEqual(
                new[]
                {
                    nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables),
                    nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables),
                    nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables)
                },
                workspacePropertyNames);
            Assert.AreEqual(2, playlistTablesPresentationChangedCount);
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
    [TestCategory("Playlist")]
    public async Task CommitBMSTableEntry_StaleDetailEditPreservesReloadedFields()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "stale-edit-header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "stale-edit-score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"StaleEdit\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./stale-edit-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9031;
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }

            BMSTableEntry staleEntry = table.entries.Single();
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"9\",\"url\":\"https://external.example/song\"}]"));
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([table], reason: "test_stale_detail_edit");

            Assert.IsTrue(results.Single().Succeeded);
            BMSTable reloadedTable = playlist.BMSTables.Single();
            staleEntry.comment = "user edit";
            playlist.CommitBMSTableEntry(staleEntry, nameof(PlaylistDetailRow.comment));

            BMSTableEntry activeEntry = reloadedTable.entries.Single(entry => !entry.is_removed);
            Assert.AreEqual(9d, activeEntry.level);
            Assert.AreEqual("user edit", activeEntry.comment);
            using var verify = new LR2SongDBExtended(songDbPath);
            BMSTableEntry storedEntry = verify.Table<BMSTableEntry>().Single(entry => entry.playlist_id == 9031 && !entry.is_removed);
            Assert.AreEqual(9d, storedEntry.level);
            Assert.AreEqual("user edit", storedEntry.comment);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_DoesNotResurrectRemovedPlaylist()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                playlist_id = 9032,
                name = "RemovedPlaylist",
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Removed")]
            };
            BMSTableEntry entry = table.entries.Single();
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };

            playlist.RemoveBMSTable(table);

            Assert.ThrowsException<InvalidOperationException>(() => playlist.CommitBMSTableEntry(entry));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<BMSTable>().Count(row => row.playlist_id == table.playlist_id));
            Assert.AreEqual(0, verify.Table<BMSTableEntry>().Count(row => row.playlist_id == table.playlist_id));
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
    [TestCategory("Playlist")]
    public void CreateBMSTable_CollectionNotificationFailureRollsBackVisibleMembership()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            playlist.BMSTables.CollectionChanged += (_, request) =>
            {
                if (request.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                {
                    throw new InvalidOperationException("test add notification failure");
                }
            };

            InvalidOperationException failure =
                Assert.ThrowsException<InvalidOperationException>(() => playlist.CreateBMSTable());

            Assert.AreEqual("test add notification failure", failure.Message);
            Assert.AreEqual(0, playlist.BMSTables.Count);
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
    [TestCategory("Playlist")]
    public void RemoveBMSTable_CollectionNotificationFailureLeavesVisibleAndDurableStateRemoved()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                playlist_id = 9033,
                name = "RemovalNotificationFailure",
                entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "Removed")]
            };
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(table.entries.Single(), typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlist.BMSTables.CollectionChanged += (_, request) =>
            {
                if (request.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                {
                    throw new InvalidOperationException("test remove notification failure");
                }
            };

            InvalidOperationException notificationFailure = Assert.ThrowsException<InvalidOperationException>(
                () => playlist.RemoveBMSTable(table));

            Assert.AreEqual("test remove notification failure", notificationFailure.Message);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<BMSTable>().Count(row => row.playlist_id == table.playlist_id));
            Assert.AreEqual(0, verify.Table<BMSTableEntry>().Count(row => row.playlist_id == table.playlist_id));
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
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_SkipsTargetsWithoutAbsoluteUri()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            var table = new BMSTable
            {
                name = "NoUri",
                symbol = "N"
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            List<PlaylistSyncProgressSnapshot> snapshots = [];

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([table], progressCallback: snapshots.Add, reason: "test_skip_no_uri");

            Assert.AreEqual(0, results.Count);
            Assert.IsTrue(snapshots.Count >= 2);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.TotalTableCount == 0));
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
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ContinuesAfterTargetFailure()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string goodHeaderPath = Path.Combine(tempDirectory, "good-header.json");
            string goodScorePath = Path.Combine(tempDirectory, "good-score.json");
            string badHeaderPath = Path.Combine(tempDirectory, "bad-header.json");
            File.WriteAllBytes(goodHeaderPath, CreateUtf8BomBytes("{\r\n\"name\":\"GoodTarget\",\r\n\"symbol\":\"G\",\r\n\"data_url\":\"./good-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(goodScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Good Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(badHeaderPath, CreateUtf8BomBytes("{ invalid json"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable goodTable = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(goodHeaderPath));
            var badTable = new BMSTable
            {
                name = "BadTarget",
                symbol = "B",
                Header_url = new Uri(badHeaderPath)
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { goodTable, badTable });
            List<PlaylistSyncAttemptResult> syncResults = [];

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([goodTable, badTable], syncResultCallback: syncResults.Add, reason: "test_partial_failure");

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, syncResults.Count);
            Assert.IsTrue(results.Any(result => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(results.Any(result => result.SourceTable == badTable && !result.Succeeded));
            Assert.IsTrue(syncResults.Any(result => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(syncResults.Any(result => result.SourceTable == badTable && !result.Succeeded));
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_DoesNotTakeAnotherPendingTableCurrentOutputDir()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = true;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string dataJsonPath = Path.Combine(tempDirectory, "score.json");
            string headerAPath = Path.Combine(tempDirectory, "header-a.json");
            string headerBPath = Path.Combine(tempDirectory, "header-b.json");
            File.WriteAllBytes(dataJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(headerAPath, CreateUtf8BomBytes("{\r\n\"name\":\"OutputB\",\r\n\"symbol\":\"A2\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(headerBPath, CreateUtf8BomBytes("{\r\n\"name\":\"OutputB\",\r\n\"symbol\":\"B2\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable tableA = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerAPath));
            BMSTable tableB = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerBPath));
            tableA.playlist_id = 9502;
            tableA.name = "LocalA";
            tableA.symbol = "A1";
            tableA.Output_dir = "LocalA";
            tableB.playlist_id = 9503;
            tableB.name = "OutputB";
            tableB.symbol = "B1";
            tableB.Output_dir = "OutputB";
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { tableA, tableB });
            var library = new TestBmsLibrary(songDbPath);
            List<string> warnings = [];
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = true },
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
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => library,
                () => null!,
                warnings.Add,
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            workspace.ConfigureCatalogNotificationQueue(action => action());
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            await workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                [new PlaylistSummaryRow { TableRef = tableA }, new PlaylistSummaryRow { TableRef = tableB }],
                new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                {
                    Name = true,
                    Symbol = true,
                    OutputDirectory = true
                });

            Assert.AreEqual("LocalA", tableA.name);
            Assert.AreEqual("A1", tableA.symbol);
            Assert.AreEqual("LocalA", tableA.Output_dir);
            Assert.AreEqual("OutputB", tableB.name);
            Assert.AreEqual("B2", tableB.symbol);
            Assert.AreEqual("OutputB", tableB.Output_dir);
            CollectionAssert.AreEqual(
                new[] { "playlist_summary_external_property_initialization_skipped reason=duplicate_output_dir table=LocalA outputDir=OutputB" },
                warnings);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_PublishesExternalPropertiesThroughWorkspace()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerJsonPath,
                CreateUtf8BomBytes("{\r\n\"name\":\"External:Name\",\r\n\"symbol\":\"😀\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9501;
            table.name = "Local Name";
            table.symbol = "L";
            table.compat_prefix = string.Empty;
            table.Output_dir = "CustomOutput";
            table.DisableExternalSync();
            table.entries[0].folder = "1";
            table.entries.Add(CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "★1"));
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
            }
            table.Folder_order = ["1", "★1"];
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);

            BMSLibrary library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, library);
            int detailReloadCount = 0;
            int referenceSortInvalidationCount = 0;
            int keywordValueCandidatesChangedCount = 0;
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("1"));
            workspace.IsPlaylistDetailViewActive = true;
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) => detailReloadCount++;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => referenceSortInvalidationCount++;
            workspace.PlaylistKeywordValueCandidatesChanged += (_, _) => keywordValueCandidatesChangedCount++;
            long initialDetailContentRevision = workspace.DetailViewState.Source.PlaylistContentRevision;

            await workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                {
                    Name = true,
                    Symbol = true,
                    CompatPrefix = true,
                    OutputDirectory = true
                });

            Assert.AreEqual("External:Name", table.name);
            Assert.AreEqual("😀", table.symbol);
            Assert.AreEqual("LEVEL ", table.compat_prefix);
            Assert.AreEqual(BMSTable.CreateDefaultOutputDirectoryName("External:Name"), table.Output_dir);
            Assert.IsFalse(table.is_external_sync);
            Assert.AreEqual("LEVEL 1", table.entries[0].folder);
            Assert.AreEqual("LEVEL ★1", table.entries[1].folder);
            CollectionAssert.AreEqual(new[] { "LEVEL 1", "LEVEL ★1" }, table.Folder_order);
            Assert.AreEqual("LEVEL 1", workspace.CapturePlaylistDetailSelection()!.FolderName);
            CollectionAssert.Contains(workspace.GetPlaylistKeywordValueCandidates().ToArray(), "External:Name");
            Assert.IsTrue(keywordValueCandidatesChangedCount > 0);
            Assert.AreEqual(1, detailReloadCount);
            Assert.AreEqual(1, referenceSortInvalidationCount);
            Assert.AreEqual(initialDetailContentRevision + 1, workspace.DetailViewState.Source.PlaylistContentRevision);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == 9501);
            Assert.AreEqual("External:Name", persisted.name);
            Assert.AreEqual("😀", persisted.symbol);
            Assert.AreEqual("LEVEL ", persisted.compat_prefix);
            Assert.IsNull(persisted.output_dir);
            Assert.AreEqual(
                "LEVEL 1",
                verify.ExecuteScalar<string>(
                    "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;",
                    9501,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.AreEqual(
                "LEVEL ★1",
                verify.ExecuteScalar<string>(
                    "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;",
                    9501,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    // U2-R5-F1: 復元後の実出力先 I/O failure を readiness consumer へ同じ原因で伝播する。
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistRestore_PostApplyOutputFailureTerminalizesReadiness()
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-restore-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        Task? readiness = null;
        try
        {
            string dbPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(dbPath);
            string lr2Root = Path.Combine(directory, "LR2");
            LR2Config config = CreateLr2Config(lr2Root, directory);
            string outputBase = Path.Combine(directory, "RootOutput");
            Directory.CreateDirectory(outputBase);
            File.WriteAllText(Path.Combine(outputBase, "BlockedRoot"), "directory creation collision");
            var settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2Root,
                LR2CustomFolderOutputBaseDirRootType = outputBase
            };
            playlist = new TestBmsPlaylist(
                dbPath, () => config, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => settings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(dbPath, CustomFolderOutputPhysicalSurface.Empty));
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            string backupPath = Path.Combine(directory, "restore.sql");
            File.WriteAllText(backupPath,
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(8011, "Restored root", "R")
                + "\v" + Environment.NewLine
                + "UPDATE playlist SET output_dir = 'BlockedRoot', is_root_folder = 1 WHERE playlist_id = 8011;");
            IOException restoreFailure = await Assert.ThrowsExceptionAsync<IOException>(() =>
                playlist.RestorePlaylistDumpAsync(File.ReadAllText(backupPath)));

            using (var verify = new LR2SongDBExtended(dbPath))
            {
                BMSTable restored = verify.Table<BMSTable>().Single();
                Assert.AreEqual(8011, restored.playlist_id);
                Assert.AreEqual("Restored root", restored.name);
                Assert.IsTrue(restored.is_root_folder);
            }
            Assert.AreEqual(8011, playlist.BMSTables.Single().playlist_id);
            Assert.AreEqual("Restored root", playlist.BMSTables.Single().name);

            readiness = playlist.StartupReadiness.WaitForRequiredPlaylistReadinessAsync();
            IOException readinessFailure = await Assert.ThrowsExceptionAsync<IOException>(() =>
                readiness.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreSame(restoreFailure, readinessFailure);
        }
        finally
        {
            // catch 欠落の negative control でも pending consumer を残さない。
            playlist?.RequestShutdown("restore_readiness_test_cleanup");
            if (readiness != null)
            {
                try { await readiness.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_ReloadsHeadersWithoutScoreInitialization()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var persistedTable = new BMSTable
            {
                playlist_id = 7001,
                name = "ReloadedTable",
                symbol = "R",
                Output_dir = "ReloadedTable"
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[]
                    {
                        new BMSTable
                        {
                            playlist_id = 1,
                            name = "OldTable",
                            symbol = "O",
                            Output_dir = "OldTable"
                        }
                    })
            };
            bool hydrationQueued = false;
            playlist.StartupBackgroundTaskScheduler = delegate
            {
                hydrationQueued = true;
                return true;
            };

            Task? rejectedRestore = null;
            playlist.BMSTables.CollectionChanged += (_, _) =>
            {
                // U2-R2: 実 collection apply の再入操作は新しい DB 復元を開始しない。
                rejectedRestore ??= playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Rejected reload restore", "X"));
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedRestore, "restore rejection during publication");
                }
                catch (InvalidOperationException)
                {
                    // 予約による明示失敗は event 終了後に対象 Task で検証する。
                }
            };
            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            Assert.IsNotNull(rejectedRestore);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rejectedRestore!);
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(7001, verify.Table<BMSTable>().Single().playlist_id);
            }

            Assert.IsTrue(hydrationQueued);
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationRequestedVersion);
            Assert.AreEqual(1, playlist.BMSTables.Count);
            Assert.AreEqual("ReloadedTable", playlist.BMSTables[0].name);
            Assert.IsFalse(playlist.BMSTables[0].ArePlaylistEntriesLoaded);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_HydrationRepairRegeneratesBatchPrunesStaleRowsAndPreservesCurrentFiles()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var tableA = new BMSTable
            {
                playlist_id = 7603,
                name = "Reload repair A",
                symbol = "RRA",
                Output_dir = "ReloadRepairA",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var tableB = new BMSTable
            {
                playlist_id = 7604,
                name = "Reload repair B",
                symbol = "RRB",
                Output_dir = "ReloadRepairB",
                ignore_folder_output = tableA.ignore_folder_output,
                entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")],
                Folder_order = ["Folder C"]
            };
            foreach (BMSTable table in new[] { tableA, tableB })
            {
                foreach (BMSTableEntry entry in table.entries)
                {
                    entry.playlist_id = table.playlist_id;
                }
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => repairSettings,
                synchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>([tableA, tableB])
            };

            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                foreach (BMSTable table in new[] { tableA, tableB })
                {
                    seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries)
                    {
                        seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                }
            }
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([tableA, tableB], "seed_reload_repair");
            string outputDirA = Path.Combine(outputBaseDir, tableA.Output_dir);
            string outputDirB = Path.Combine(outputBaseDir, tableB.Output_dir);
            string[] filesA = Directory.GetFiles(outputDirA, "*.lr2folder", SearchOption.AllDirectories);
            string[] filesB = Directory.GetFiles(outputDirB, "*.lr2folder", SearchOption.AllDirectories);
            Assert.IsTrue(filesA.Length >= 3);
            Assert.IsTrue(filesB.Length >= 2);
            string preservedFile = filesA[0];
            string missingFileA = filesA[^1];
            string missingFileB = filesB[^1];
            DateTime preservedTimestamp = new(2026, 6, 1, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(preservedFile, preservedTimestamp);
            File.Delete(missingFileA);
            File.Delete(missingFileB);
            string staleFile = Path.Combine(outputDirA, "stale.lr2folder");
            File.WriteAllText(staleFile, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.Insert(new LR2SongDB.folder
                {
                    path = staleFile,
                    title = "stale",
                    type = 2,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDirA)
                }, typeof(LR2SongDB.folder));
            }
            RootFileEnumerationEntry[] physicalEntries = filesA
                .Concat(filesB)
                .Append(staleFile)
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .Select(file => new RootFileEnumerationEntry(
                    file.FullName,
                    file.LastWriteTimeUtc,
                    file.Length))
                .ToArray();
            CustomFolderOutputPhysicalSurface capturedPhysicalSurface =
                CustomFolderOutputPhysicalSurface.FromEntries(physicalEntries, discoveryComplete: true);
            synchronization.PhysicalSurfaceFactory = () => capturedPhysicalSurface;
            PlaylistPersistenceRepository statusRepository = new(songDbPath);
            Dictionary<int, CustomFolderOutputStatusRow> seededStatusRows =
                statusRepository.ReadCustomFolderOutputStatusRows();
            Assert.IsTrue(seededStatusRows.TryGetValue(tableB.playlist_id!.Value, out CustomFolderOutputStatusRow? seededStatusB));
            seededStatusB!.PhysicalMtimeSignature = "stale-before-reload";
            statusRepository.PersistCustomFolderOutputStatusRows(seededStatusRows.Values);

            var scheduled = new List<(string Owner, Func<Task> Work)>();
            var repairProgress = new List<PlaylistSyncProgressSnapshot>();
            object schedulerSync = new();
            PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt? receipt = null;
            int completionVersionAtReceipt = -1;
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                completionVersionAtReceipt = playlist.PlaylistEntriesHydrationCompletedVersion;
            };
            playlist.StartupBackgroundTaskScheduler = (owner, _, _, work) =>
            {
                lock (schedulerSync)
                {
                    scheduled.Add((owner, work));
                }
                return true;
            };
            playlist.CustomFolderOutputRepairProgressReporter = snapshot =>
            {
                repairProgress.Add(snapshot);
                throw new InvalidOperationException("deterministic progress observer failure");
            };

            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            (string Owner, Func<Task> Work) hydrationWork;
            lock (schedulerSync)
            {
                hydrationWork = scheduled.Single(item => item.Owner == "playlist_entries_hydration");
            }
            await hydrationWork.Work();

            (string Owner, Func<Task> Work) repairWork;
            lock (schedulerSync)
            {
                repairWork = scheduled.Single(item => item.Owner == "playlist_custom_folder_output_repair");
            }
            await repairWork.Work();

            Assert.IsNotNull(receipt);
            Assert.AreEqual(1, receipt!.RequestVersion);
            Assert.AreEqual(0, completionVersionAtReceipt);
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationCompletedVersion);
            Assert.IsTrue(playlist.BMSTables.All(table => table.ArePlaylistEntriesLoaded));
            Assert.IsTrue(File.Exists(missingFileA));
            Assert.IsTrue(File.Exists(missingFileB));
            Assert.IsFalse(File.Exists(staleFile));
            Assert.AreEqual(preservedTimestamp, File.GetLastWriteTimeUtc(preservedFile));
            Assert.AreEqual("playlist_lr2folder_batch_sync", synchronization.Operations[^1]);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleFile));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", missingFileA));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", missingFileB));
            Dictionary<int, CustomFolderOutputStatusRow> statusRows =
                new PlaylistPersistenceRepository(songDbPath).ReadCustomFolderOutputStatusRows();
            Assert.IsTrue(statusRows.TryGetValue(tableA.playlist_id!.Value, out CustomFolderOutputStatusRow? statusA));
            Assert.IsTrue(statusRows.TryGetValue(tableB.playlist_id!.Value, out CustomFolderOutputStatusRow? statusB));
            Assert.AreEqual(tableA.Output_dir, Path.GetFileName(statusA!.OutputDirectory.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.AreEqual(tableB.Output_dir, Path.GetFileName(statusB!.OutputDirectory.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.IsFalse(string.IsNullOrWhiteSpace(statusA.PhysicalMtimeSignature));
            Assert.IsFalse(string.IsNullOrWhiteSpace(statusB.PhysicalMtimeSignature));
            PlaylistSyncProgressSnapshot[] activeProgress = repairProgress
                .Where(snapshot => snapshot.IsActive)
                .ToArray();
            Assert.IsTrue(activeProgress.Length > 0);
            Assert.AreEqual(1, activeProgress.Select(snapshot => snapshot.TotalTableCount).Distinct().Count());
            Assert.IsTrue(activeProgress.All(snapshot => snapshot.TotalTableCount > 0));
            Assert.IsTrue(activeProgress.All(snapshot => snapshot.CompletedTableCount > 0));
            Assert.IsTrue(activeProgress.All(snapshot => snapshot.CompletedTableCount < snapshot.TotalTableCount));
            Assert.IsTrue(activeProgress
                .Zip(activeProgress.Skip(1), (previous, current) =>
                    current.CompletedTableCount >= previous.CompletedTableCount)
                .All(nondecreasing => nondecreasing));
            Assert.IsTrue(repairProgress.Take(repairProgress.Count - 1).All(snapshot => snapshot.IsActive));
            Assert.IsFalse(repairProgress[^1].IsActive);

            repairProgress.Clear();
            Dictionary<int, CustomFolderOutputStatusRow> staleStatusRows =
                statusRepository.ReadCustomFolderOutputStatusRows();
            foreach (CustomFolderOutputStatusRow row in staleStatusRows.Values)
            {
                row.PhysicalMtimeSignature = "stale-before-failure";
            }
            statusRepository.PersistCustomFolderOutputStatusRows(staleStatusRows.Values);
            var expectedRepairFailure = new InvalidOperationException("deterministic startup repair failure");
            synchronization.Failure = expectedRepairFailure;

            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            (string Owner, Func<Task> Work) failingHydrationWork;
            lock (schedulerSync)
            {
                failingHydrationWork = scheduled.Last(item => item.Owner == "playlist_entries_hydration");
            }
            await failingHydrationWork.Work();

            (string Owner, Func<Task> Work) failingRepairWork;
            lock (schedulerSync)
            {
                failingRepairWork = scheduled.Last(item => item.Owner == "playlist_custom_folder_output_repair");
            }
            InvalidOperationException actualRepairFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                failingRepairWork.Work);

            Assert.AreSame(expectedRepairFailure, actualRepairFailure);
            Assert.IsTrue(repairProgress.Any(snapshot => snapshot.IsActive));
            Assert.IsTrue(repairProgress.Take(repairProgress.Count - 1).All(snapshot => snapshot.IsActive));
            Assert.IsFalse(repairProgress[^1].IsActive);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_ZeroTargetRepairPublishesOnlyInactiveProgress()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => repairSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty));
            var scheduled = new List<(string Owner, Func<Task> Work)>();
            var repairProgress = new List<PlaylistSyncProgressSnapshot>();
            playlist.StartupBackgroundTaskScheduler = (owner, _, _, work) =>
            {
                scheduled.Add((owner, work));
                return true;
            };
            playlist.CustomFolderOutputRepairProgressReporter = repairProgress.Add;

            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            await scheduled.Single(item => item.Owner == "playlist_entries_hydration").Work();
            await scheduled.Single(item => item.Owner == "playlist_custom_folder_output_repair").Work();

            Assert.IsTrue(repairProgress.Count > 0);
            Assert.IsFalse(repairProgress.Any(snapshot => snapshot.IsActive));
            Assert.IsFalse(repairProgress[^1].IsActive);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_BackgroundRepairBusyAdmissionIsNonInteractiveAndSideEffectFree()
    {
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDirectory = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDirectory;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDirectory,
                LR2CustomFolderOutputBaseDirRootType = Settings.Default.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            const int playlistId = 7611;
            BMSTable persistedTable = new()
            {
                playlist_id = playlistId,
                name = "Busy repair",
                symbol = "BR",
                Output_dir = "BusyRepair",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Busy folder")],
                Folder_order = ["Busy folder"]
            };
            foreach (BMSTableEntry entry in persistedTable.entries)
            {
                entry.playlist_id = playlistId;
            }
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in persistedTable.entries)
                {
                    seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }

            var synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            var admissionRequests = new List<(string Operation, bool ShowMessage)>();
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => repairSettings,
                synchronization,
                mutationLeaseProviderWithMessage: (operation, showMessage) =>
                {
                    admissionRequests.Add((operation, showMessage));
                    return null;
                })
            {
                BMSTables = new ObservableCollection<BMSTable>([persistedTable])
            };

            var scheduled = new List<(string Owner, Func<Task> Work)>();
            playlist.StartupBackgroundTaskScheduler = (owner, _, _, work) =>
            {
                scheduled.Add((owner, work));
                return true;
            };

            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            (string Owner, Func<Task> Work) hydrationWork = scheduled.Single(
                item => item.Owner == "playlist_entries_hydration");
            await hydrationWork.Work();
            (string Owner, Func<Task> Work) repairWork = scheduled.Single(
                item => item.Owner == "playlist_custom_folder_output_repair");

            Dictionary<string, byte[]> SnapshotFiles()
                => Directory.EnumerateFiles(tempDirectory, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(path, songDbPath, StringComparison.OrdinalIgnoreCase)
                        && !path.StartsWith(songDbPath + "-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        path => Path.GetRelativePath(tempDirectory, path),
                        File.ReadAllBytes,
                        StringComparer.OrdinalIgnoreCase);
            string[] SnapshotDirectories()
                => Directory.EnumerateDirectories(tempDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(tempDirectory, path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            (long PlaylistCount, string Name, string Symbol, string OutputDirectory,
                long EntryCount, string EntryMd5, string EntryFolder, long FolderRowCount,
                long StatusRowCount) SnapshotDatabase()
            {
                using var db = new LR2SongDBExtended(songDbPath);
                return (
                    db.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<string>("SELECT symbol FROM playlist WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<string>("SELECT output_dir FROM playlist WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<string>("SELECT md5 FROM playlist_entry WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = ?;", playlistId),
                    db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"),
                    db.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_custom_folder_output_status;"));
            }

            Dictionary<string, byte[]> filesBeforeRepair = SnapshotFiles();
            string[] directoriesBeforeRepair = SnapshotDirectories();
            var databaseBeforeRepair = SnapshotDatabase();
            int synchronizationOperationCountBeforeRepair = synchronization.Operations.Count;

            static Task ExecuteCapturedWork(Func<Task> work)
            {
                try
                {
                    return work();
                }
                catch (Exception exception)
                {
                    return Task.FromException(exception);
                }
            }

            Task repairTask = ExecuteCapturedWork(repairWork.Work);
            Assert.IsTrue(
                repairTask.IsCompleted,
                "Busy background repair must terminalize immediately without waiting or scheduling a retry.");
            try
            {
                await repairTask;
            }
            catch (InvalidOperationException)
            {
                // A busy background admission may fail explicitly or be mapped
                // to a skipped terminal result, but it must never wait or retry.
            }

            Assert.AreEqual(1, admissionRequests.Count);
            Assert.AreEqual("playlist_custom_folder_output_repair", admissionRequests[0].Operation);
            Assert.IsFalse(admissionRequests[0].ShowMessage);
            Assert.AreEqual(0, synchronization.Operations.Count - synchronizationOperationCountBeforeRepair);
            Assert.AreEqual(databaseBeforeRepair, SnapshotDatabase());
            CollectionAssert.AreEqual(directoriesBeforeRepair, SnapshotDirectories());
            Dictionary<string, byte[]> filesAfterRepair = SnapshotFiles();
            CollectionAssert.AreEquivalent(filesBeforeRepair.Keys.ToArray(), filesAfterRepair.Keys.ToArray());
            foreach (string path in filesBeforeRepair.Keys)
            {
                CollectionAssert.AreEqual(filesBeforeRepair[path], filesAfterRepair[path], path);
            }

            BMSTable activeTable = playlist.BMSTables.Single();
            Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFolder(activeTable));
            Assert.AreEqual(2, admissionRequests.Count);
            Assert.AreEqual("ReOutputCustomFolder", admissionRequests[1].Operation);
            Assert.IsTrue(admissionRequests[1].ShowMessage);
            Assert.AreEqual(0, synchronization.Operations.Count - synchronizationOperationCountBeforeRepair);
            Assert.AreEqual(databaseBeforeRepair, SnapshotDatabase());
            CollectionAssert.AreEqual(directoriesBeforeRepair, SnapshotDirectories());
            filesAfterRepair = SnapshotFiles();
            CollectionAssert.AreEquivalent(filesBeforeRepair.Keys.ToArray(), filesAfterRepair.Keys.ToArray());
            foreach (string path in filesBeforeRepair.Keys)
            {
                CollectionAssert.AreEqual(filesBeforeRepair[path], filesAfterRepair[path], path);
            }
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void QueueDeferredPlaylistEntriesHydration_PublishesReceiptBeforeCompletionVersion()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            const int playlistId = 7051;
            BMSTable persistedTable = new()
            {
                playlist_id = playlistId,
                name = "HydrationReceipt",
                symbol = "HR",
                Output_dir = "HydrationReceipt"
            };
            BMSTableEntry persistedEntry = CreateEntry(
                "dddddddddddddddddddddddddddddddd",
                "Receipt folder");
            persistedEntry.playlist_id = playlistId;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
                db.InsertOrReplace(persistedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }

            BMSTable table = new()
            {
                playlist_id = playlistId,
                name = persistedTable.name,
                symbol = persistedTable.symbol,
                Output_dir = persistedTable.Output_dir
            };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            Task scheduledWork = null!;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                scheduledWork = work();
                return true;
            };
            PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt receipt = null!;
            int completedVersionAtReceipt = -1;
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                completedVersionAtReceipt = playlist.PlaylistEntriesHydrationCompletedVersion;
            };

            playlist.QueueDeferredPlaylistEntriesHydration("receipt_test");
            Assert.IsNotNull(scheduledWork);
            scheduledWork.GetAwaiter().GetResult();

            Assert.IsNotNull(receipt);
            Assert.AreEqual(1, receipt.RequestVersion);
            Assert.AreEqual("receipt_test", receipt.Reason);
            Assert.AreEqual(1, receipt.Tables.Count);
            Assert.AreSame(table, receipt.Tables[0].Table);
            Assert.AreEqual(1, receipt.Tables[0].Entries.Count);
            table.entries.Clear();
            Assert.AreEqual(1, receipt.Tables[0].Entries.Count);
            Assert.AreEqual(0, completedVersionAtReceipt);
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationCompletedVersion);
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
    [TestCategory("Playlist")]
    public void QueueDeferredPlaylistEntriesHydration_ConsumerFailureFaultsWithoutRetry()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                playlist_id = 7052,
                name = "HydrationFailure",
                symbol = "HF",
                Output_dir = "HydrationFailure"
            };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            Task scheduledWork = null!;
            int schedulerCalls = 0;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                schedulerCalls++;
                scheduledWork = work();
                return true;
            };
            int consumerCalls = 0;
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, _) =>
            {
                consumerCalls++;
                throw new InvalidOperationException("deterministic receipt consumer failure");
            };

            playlist.QueueDeferredPlaylistEntriesHydration("consumer_failure");

            Assert.IsNotNull(scheduledWork);
            Assert.ThrowsException<InvalidOperationException>(
                () => scheduledWork.GetAwaiter().GetResult());
            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, consumerCalls);
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationRequestedVersion);
            Assert.AreEqual(0, playlist.PlaylistEntriesHydrationCompletedVersion);
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
    [TestCategory("Playlist")]
    public void PlaylistHydrationReceipt_DoesNotCaptureUnloadedCurrentTable()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSTable table = new()
            {
                playlist_id = 7053,
                name = "UnloadedReceipt",
                symbol = "UR",
                Output_dir = "UnloadedReceipt"
            };
            table.MarkEntriesNotLoaded();
            var owner = new PlaylistEntriesHydrationOwner(
                new PlaylistPersistenceRepository(songDbPath),
                () => new PlaylistEntriesHydrationOwner.PlaylistHydrationTableSnapshot(1, [table]),
                _ => new MemoryStream(),
                () => false,
                () => null,
                _ => { },
                (_, _) => { });
            var source = new PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt(
                generation: 1,
                shutdownEpoch: 0,
                requestVersion: 1,
                reason: "unloaded_receipt",
                tables: [],
                continuation: null);

            Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateReceiptForCurrentTables(source, continuation: null));
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
    [TestCategory("Playlist")]
    public void PlaylistHydrationFailure_DoesNotMergeFailedContinuationIntoIndependentRequest()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSTable table = new()
            {
                playlist_id = 7054,
                name = "ContinuationIsolation",
                symbol = "CI",
                Output_dir = "ContinuationIsolation",
                entries = []
            };
            var scheduledWorks = new List<Func<Task>>();
            var owner = new PlaylistEntriesHydrationOwner(
                new PlaylistPersistenceRepository(songDbPath),
                () => new PlaylistEntriesHydrationOwner.PlaylistHydrationTableSnapshot(1, [table]),
                _ => new MemoryStream(),
                () => false,
                () => (_, _, _, work) =>
                {
                    scheduledWorks.Add(work);
                    return true;
                },
                _ => { },
                (_, _) => { });
            int receiptCount = 0;
            PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent secondReceiptContinuation = null!;
            owner.HydrationReceiptPublished += (_, eventArgs) =>
            {
                if (Interlocked.Increment(ref receiptCount) == 1)
                {
                    owner.QueueDeferredPlaylistEntriesHydration(
                        "independent",
                        new PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent(
                            runExternalSyncAfterHydration: false,
                            queueBeatorajaBmtExportAfterHydration: true,
                            runCustomFolderOutputRepairAfterHydration: false,
                            verifyRootOutputDirectoryRows: false));
                    throw new InvalidOperationException("deterministic composition failure");
                }

                secondReceiptContinuation = eventArgs.Receipt.Continuation;
            };

            owner.QueueDeferredPlaylistEntriesHydration(
                "initial",
                new PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent(
                    runExternalSyncAfterHydration: true,
                    queueBeatorajaBmtExportAfterHydration: false,
                    runCustomFolderOutputRepairAfterHydration: false,
                    verifyRootOutputDirectoryRows: false));

            Assert.AreEqual(1, scheduledWorks.Count);
            Assert.ThrowsException<InvalidOperationException>(
                () => scheduledWorks[0]().GetAwaiter().GetResult());
            Assert.AreEqual(2, scheduledWorks.Count);

            scheduledWorks[1]().GetAwaiter().GetResult();

            Assert.IsNotNull(secondReceiptContinuation);
            Assert.IsFalse(secondReceiptContinuation.RunExternalSyncAfterHydration);
            Assert.IsTrue(secondReceiptContinuation.QueueBeatorajaBmtExportAfterHydration);
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
    [TestCategory("Playlist")]
    public void CommitBMSTableHeaderToDB_PersistsPlaylistNameInStandaloneMode()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7101,
                name = "BeforeName",
                symbol = "BN",
                Output_dir = "BeforeName"
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, folder) VALUES (7101, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Song', '1');");
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            table.name = "AfterName";
            playlist.CommitBMSTableHeaderToDB(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual("AfterName", verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = 7101;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 7101;"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableWithEntriesToDB_PersistsPrefixRewrittenFoldersInStandaloneMode()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7201,
                name = "PrefixTable",
                symbol = "PT",
                Output_dir = "PrefixTable",
                compat_prefix = "LEVEL ",
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "LEVEL 1"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "LEVEL 2")
                ],
                Folder_order = ["LEVEL 2", "LEVEL 1"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            table.compat_prefix = "★";
            Assert.IsTrue(table.RewriteCompatibleFolderPrefix("LEVEL ", table.compat_prefix));
            playlist.CommitBMSTableWithEntriesToDB(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            string firstFolder = verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = 7201 AND md5 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';");
            string secondFolder = verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = 7201 AND md5 = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';");
            CollectionAssert.AreEqual(new[] { "★1", "★2" }, new[] { firstFolder, secondFolder });
            Assert.AreEqual("★", verify.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id = 7201;"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableWithEntriesToDB_HydratesUnloadedEntriesBeforeCommit()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            const int playlistId = 7251;
            BMSTable persistedTable = new()
            {
                playlist_id = playlistId,
                name = "HydratedTable",
                symbol = "HT",
                Output_dir = "HydratedTable"
            };
            BMSTableEntry persistedEntry = CreateEntry(
                "cccccccccccccccccccccccccccccccc",
                "Hydrated folder");
            persistedEntry.playlist_id = playlistId;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
                db.InsertOrReplace(persistedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }

            BMSTable unloadedTable = new()
            {
                playlist_id = playlistId,
                name = persistedTable.name,
                symbol = persistedTable.symbol,
                Output_dir = persistedTable.Output_dir
            };
            unloadedTable.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { unloadedTable })
            };

            Assert.IsFalse(unloadedTable.ArePlaylistEntriesLoaded);
            playlist.CommitBMSTableWithEntriesToDB(unloadedTable);

            Assert.IsTrue(unloadedTable.ArePlaylistEntriesLoaded);
            Assert.AreEqual(1, unloadedTable.entries.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;",
                    playlistId,
                    persistedEntry.md5));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
