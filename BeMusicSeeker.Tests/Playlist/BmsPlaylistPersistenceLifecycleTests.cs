using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>実SQLiteの保存・復元と、それを所有するplaylist/workspaceの通知・受付・必須準備を検証します。</summary>
/// <remarks>DBと出力の確定順は実SQLiteと必要なファイルで観測し、treeの実VM接続だけ共有UI構成を使います。画面操作は上位WPFケースが分担します。</remarks>
[TestClass]
public sealed class BmsPlaylistPersistenceLifecycleTests
{
    /// <summary>実DB保存境界で全件/一部失効の厳格保存を拒否し、一部だけの保存を防ぎます。入力空と非厳格の現行対象だけ保存する互換契約も同じDBで確認します。</summary>
    [TestMethod]
    public async Task CommitHeaders_CurrentTargetRequirementRejectsAllAndPartialRetirementButAllowsEmpty()
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        var active = new BMSTable { playlist_id = 7493, name = "Before", entries = [] };
        fixture.Playlist.BMSTables.Add(active);
        fixture.Playlist.CommitBMSTableHeadersToDB([active]);
        fixture.Playlist.BMSTables.Remove(fixture.Table);
        active.name = "After";
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Playlist.CommitBMSTableHeadersToDB([fixture.Table]));
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Playlist.CommitBMSTableHeadersToDB([active, fixture.Table]));
        Assert.AreEqual(0, fixture.Playlist.CommitBMSTableHeadersToDB([]).Count);
        Assert.IsTrue(fixture.Playlist.CanEnterPlaylistMutation);
        using (var db = new LR2SongDBExtended(fixture.SongDb))
        {
            Assert.AreEqual("Before", db.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = ?", active.playlist_id));
        }
        Assert.AreEqual(0, fixture.Playlist.CommitBMSTableHeadersToDB([fixture.Table], requireCurrentTarget: false).Count);
        Assert.AreEqual(1, fixture.Playlist.CommitBMSTableHeadersToDB([active, fixture.Table], requireCurrentTarget: false).Count);
        using var verify = new LR2SongDBExtended(fixture.SongDb);
        Assert.AreEqual("After", verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = ?", active.playlist_id));
        Assert.AreEqual("Prefix", verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = ?", fixture.Table.playlist_id));
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>出力状態の設定・ハッシュ・更新時刻を実SQLiteへ保存して再取得し、指定表の削除まで確認して保存項目の欠落や残存を検出します。</summary>
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

    /// <summary>旧保存mask 0x7FEを実SQLiteへ保存してスキーマ準備を再適用し、読戻しでも保存値を改変しないことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void EnsureSchema_PreservesLegacyCustomFolderOutputMask()
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                legacyMask,
                verify.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = ?;", playlistId));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>フォルダ名変更・作成・削除、行削除、dropを実DB triggerで失敗させ、表示中の参照と保存内容を復元して次の保存を受理することを確認します。</summary>
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
                    () => new PlaylistUrlCompletionOptionsSnapshot(),
                    () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = false },
                    () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                    CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                        songDbPath,
                        CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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

                Exception? failure = null;
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
                StringAssert.Contains(failure!.ToString(), "playlist mutation DB write failure", mutationKind);
                Assert.AreEqual(initialRevision, table.PlaylistEntriesRevision, mutationKind);
                Assert.AreEqual(initialLastUpdate, table.last_update, mutationKind);
                CollectionAssert.AreEqual(initialFolderOrder, table.Folder_order, mutationKind);
                CollectionAssert.AreEqual(
                    new BMSTableEntry[] { targetEntry, retainedEntry },
                    table.entries.ToArray(),
                    mutationKind);
                Assert.AreSame(table, targetEntry.parent, mutationKind);
                Assert.AreSame(table, retainedEntry.parent, mutationKind);
                CollectionAssert.AreEqual(initialEntryFolders, new[] { targetEntry.folder, retainedEntry.folder }, mutationKind);
                Assert.AreEqual("Target before failure", targetEntry.title, mutationKind);
                Assert.AreEqual("Retained before failure", retainedEntry.title, mutationKind);

                using (LR2SongDBExtended verifyFailedWrite = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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

                using (LR2SongDBExtended verifyFollowingWrite = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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

    /// <summary>表示中の表へフォルダ変更と行削除を行い、選択の追従と詳細再読込を公開して変更を古い表示へ取り残す誤りを検出します。</summary>
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
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
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) => detailReloadCount++;



            await workspace.RenameFolderAsync(table, PlaylistFolderNode.CreateFolder("Mutation"), "Renamed");
            Assert.AreEqual("Renamed", workspace.CapturePlaylistDetailSelection()!.FolderName);
            await workspace.CreateFolderAsync(table);
            await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Renamed"));
            Assert.AreEqual(string.Empty, workspace.CapturePlaylistDetailSelection()!.FolderName);
            await workspace.DeleteSelectedEntriesAsync([
                new PlaylistDetailSourceRow(entry, resolvedChart: null)]);

            Assert.AreEqual(4, detailReloadCount);

        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>異なる二表の選択行を一度に削除し、両方の実DBとモデルへの反映と一回の変更通知を確認して片方だけの保存や通知の分裂を検出します。</summary>
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([firstTable, secondTable])
            };
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
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
            Assert.AreEqual(1, notifications.Count, "一要求の二表削除は成功事実を一つの通知receiptへ集約します。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>実VMへ同じcollectionの更新とcollection自体の置換を配送し、tree参照・プロパティ通知・表示更新がそれぞれ追従することを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public void PlaylistTreeTables_TrackStoreReplacementAndPreserveCollectionIdentity()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([new BMSTable { name = "Initial" }])
            };
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            using SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
            List<string> workspacePropertyNames = [];
            int playlistTablesPresentationChangedCount = 0;
            viewModel.PlaylistWorkspace.PropertyChanged += (_, e) => workspacePropertyNames.Add(e.PropertyName!);
            viewModel.PlaylistWorkspace.PlaylistTablesPresentationChanged += (_, _) => playlistTablesPresentationChangedCount++;

            Assert.AreEqual(0, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);

            viewModel.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);

            Assert.AreSame(playlist.BMSTables, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            Assert.AreEqual(1, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);
            CollectionAssert.AreEqual(
                new[] { nameof(PlaylistWorkspaceViewModel.PlaylistTreeTables) },
                workspacePropertyNames);

            playlist.BMSTables.Add(new BMSTable { name = "Added" });
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreSame(playlist.BMSTables, viewModel.PlaylistWorkspace.PlaylistTreeTables);
            Assert.AreEqual(2, viewModel.PlaylistWorkspace.PlaylistTreeTables.Count);
            Assert.AreEqual(1, playlistTablesPresentationChangedCount);
            ObservableCollection<BMSTable> replacement = new(
                [new BMSTable { name = "Replacement" }]);
            playlist.BMSTables = replacement;
            TestUiDispatcherHost.ProcessQueuedPresentation();

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

    /// <summary>編集開始後に行または表を削除し、完了をstaleとして拒否して実DBを変えず、削除対象を再生成しないことを確認します。</summary>
    [TestMethod]
    // falseは表全体、trueは編集中の行だけを退役させる条件です。
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task DetailEdit_RejectsRemovedTargetWithoutResurrectingDatabase(bool removeEntryOnly)
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
            var library = new TestBmsLibrary(songDbPath, null, null, null,
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false }, settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            playlist.CommitBMSTableWithEntriesToDB(table);
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
                playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            var source = new PlaylistDetailSourceRow(entry, resolvedChart: null);
            workspace.DetailViewState.Source.Rows = [source];
            var context = new MainChartListCellEditContext(source.CreateViewRow(), nameof(PlaylistDetailRow.memo),
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            workspace.BeginDetailEdit(context);
            if (removeEntryOnly)
            {
                Assert.IsTrue(playlist.RemoveEntriesBMSTable([entry], table));
            }
            else
            {
                playlist.RemoveBMSTable(table);
            }
            string deletedDatabase = playlist.GetPlaylistDump();
            PlaylistWorkspaceMutationRejectedEventArgs? rejected = null;
            workspace.MutationRejected += (_, args) => rejected = args;

            await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "rejected memo", commit: true));

            Assert.IsNotNull(rejected);
            Assert.IsTrue(rejected.IsStale);
            Assert.IsFalse(rejected.IsBusy);
            Assert.AreEqual(deletedDatabase, playlist.GetPlaylistDump());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(removeEntryOnly ? 1 : 0, verify.Table<BMSTable>().Count(row => row.playlist_id == table.playlist_id));
            Assert.AreEqual(0, verify.Table<BMSTableEntry>().Count(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5));
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); }
        }
    }

    /// <summary>新しい表のdraftを作成・改名しても、明示登録前のモデル集合・実DB・変更通知へ漏れないことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CreateBMSTable_NewDraftDoesNotChangeCanonicalCollectionOrDatabase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            int notifications = 0;
            playlist.BMSTables.CollectionChanged += (_, _) => notifications++;
            BMSTable draft = playlist.CreateBMSTable();
            draft.name = "Unaccepted draft";
            Assert.IsNull(draft.playlist_id);
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<BMSTable>().Count());
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    /// <summary>実削除後のcollection通知を失敗させ、元の失敗を返しながら表と行の削除をモデル・DB双方で確定していることを確認します。</summary>
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
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>取得URIのない表を再読込対象から除外し、結果を生成せず対象数ゼロの進捗を通知することを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_SkipsTargetsWithoutAbsoluteUri()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            var table = new BMSTable
            {
                name = "NoUri",
                symbol = "N"
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            List<PlaylistSyncProgressSnapshot> snapshots = [];

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([table], progressCallback: snapshots.Add, reason: "test_skip_no_uri");

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

    /// <summary>正常JSONと破損JSONを同じ要求で取得し、各表の成功・失敗を戻り値とcallback双方へ届けて対象結果の欠落を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReturnsEachTargetOutcome()
    {
        testSettings.EnablePlaylistUrlCompletion = false;
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable goodTable = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(goodHeaderPath));
            var badTable = new BMSTable
            {
                name = "BadTarget",
                symbol = "B",
                Header_url = new Uri(badHeaderPath)
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { goodTable, badTable });
            var syncResults = new ConcurrentQueue<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>();

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([goodTable, badTable], syncResultCallback: syncResults.Enqueue, reason: "test_partial_failure");

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, syncResults.Count);
            foreach (PlaylistExternalSyncOwner.PlaylistReloadTargetResult result in results)
            {
                Assert.AreSame(result, syncResults.Single(reported => ReferenceEquals(reported.SourceTable, result.SourceTable)));
            }
            Assert.IsTrue(results.Any(result => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(results.Any(result => result.SourceTable == badTable && !result.Succeeded));
            Assert.IsTrue(syncResults.Any(result => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(syncResults.Any(result => result.SourceTable == badTable && !result.Succeeded));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>外部初期化は同一・包含候補を表ごとに除外します。除外表の旧先との再衝突も拒否し、安全な表だけをDB・正本へ反映します。</summary>
    [DataTestMethod]
    [DataRow("duplicate")]
    [DataRow("nested")]
    [DataRow("retained")]
    [TestCategory("Playlist")]
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_DoesNotTakeAnotherPendingTableCurrentOutputDir(string conflict)
    {
        testSettings.EnablePlaylistUrlCompletion = false;
        testSettings.OperationModeLR2DB = true;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output");
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "Root");
            testSettings.LR2RootPath = tempDirectory;
            string dataJsonPath = Path.Combine(tempDirectory, "score.json");
            string headerAPath = Path.Combine(tempDirectory, "header-a.json");
            string headerBPath = Path.Combine(tempDirectory, "header-b.json");
            File.WriteAllBytes(dataJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(headerAPath, CreateUtf8BomBytes("{\r\n\"name\":\"OutputB\",\r\n\"symbol\":\"A2\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(headerBPath, CreateUtf8BomBytes("{\r\n\"name\":\"OutputB\",\r\n\"symbol\":\"B2\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));

            if (conflict == "retained")
            {
                File.WriteAllBytes(headerBPath, CreateUtf8BomBytes("{\"name\":\"LocalA\",\"symbol\":\"B2\",\"data_url\":\"./score.json\"}"));
            }
            string headerCPath = Path.Combine(tempDirectory, "header-c.json");
            File.WriteAllBytes(headerCPath, CreateUtf8BomBytes("{\"name\":\"Safe\",\"symbol\":\"C2\",\"data_url\":\"./score.json\"}"));
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable tableA = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerAPath));
            BMSTable tableB = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerBPath));
            tableA.playlist_id = 9502;
            tableA.name = "LocalA";
            tableA.symbol = "A1";
            tableA.Output_dir = "LocalA";
            tableB.playlist_id = 9503;
            tableB.name = conflict == "duplicate" ? "OutputB" : "LocalB";
            tableB.symbol = "B1";
            tableB.Output_dir = conflict == "duplicate" ? "OutputB" : @"OutputB\Child";
            BMSTable tableC = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerCPath));
            tableC.playlist_id = 9504;
            tableC.symbol = "C1";
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { tableA, tableB, tableC });
            playlist.CommitBMSTableHeadersToDB(playlist.BMSTables);
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            List<string> warnings = [];
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings),
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
                [new PlaylistSummaryRow { TableRef = tableA }, new PlaylistSummaryRow { TableRef = tableB }, new PlaylistSummaryRow { TableRef = tableC }],
                new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                {
                    Name = true,
                    Symbol = true,
                    OutputDirectory = true
                });

            Assert.AreEqual("LocalA", tableA.name);
            Assert.AreEqual("A1", tableA.symbol);
            Assert.AreEqual("LocalA", tableA.Output_dir);
            Assert.AreEqual(conflict == "retained" ? "LocalB" : "OutputB", tableB.name);
            Assert.AreEqual(conflict == "retained" ? "B1" : "B2", tableB.symbol);
            Assert.AreEqual(conflict == "retained" ? @"OutputB\Child" : "OutputB", tableB.Output_dir);
            Assert.AreEqual("C2", tableC.symbol);
            Assert.AreEqual(conflict == "retained" ? 2 : 1, warnings.Count);
            using LR2SongDBExtended reader = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("A1", reader.Table<BMSTable>().Single(table => table.playlist_id == 9502).symbol);
            Assert.AreEqual(conflict == "retained" ? "B1" : "B2", reader.Table<BMSTable>().Single(table => table.playlist_id == 9503).symbol);
            Assert.AreEqual("C2", reader.Table<BMSTable>().Single(table => table.playlist_id == 9504).symbol);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>外部プロパティ初期化の実取得・保存後、ローカル追加行を含む既存表示・参照の更新をworkspaceへ公開します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_PublishesExternalPropertiesThroughWorkspace()
    {
        testSettings.EnablePlaylistUrlCompletion = false;
        testSettings.OperationModeLR2DB = false;
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9501;
            table.name = "Local Name";
            table.symbol = "L";
            table.ApplyEntryMutation(table.PrepareHeaderMutation(new PlaylistHeaderEdit
            { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = string.Empty }));
            table.Output_dir = "CustomOutput";
            table.DisableExternalSync();
            table.AddBMSTableEntriesToFolder([CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "★1")], "★1");
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
            }
            table.Folder_order = ["1", "★1"];
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);

            BMSLibrary library = new TestBmsLibrary(songDbPath, settings: testSettings);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, library);
            int detailReloadCount = 0;
            int keywordValueCandidatesChangedCount = 0;
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("1"));
            workspace.IsPlaylistDetailViewActive = true;
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) => detailReloadCount++;

            workspace.PlaylistKeywordValueCandidatesChanged += (_, _) => keywordValueCandidatesChangedCount++;


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


            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>接頭辞変更後の同じID・更新日時・ハッシュを持つ表へSQLを復元し、実出力の検索条件とfolder行が復元済み項目に一致するまで待つことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistRestore_RebuildsCurrentOutputAfterPrefixOnlyChange()
    {
        string directory = Path.Combine(Path.GetTempPath(), "playlist-restore-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string bmsRoot = Path.Combine(directory, "BMS");
            Directory.CreateDirectory(bmsRoot);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = Path.Combine(directory, "LR2");
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(bmsRoot, "CustomFolder");
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(testSettings.LR2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var table = new BMSTable
            {
                playlist_id = 8012,
                name = "RestorePrefix",
                symbol = "RP",
                Output_dir = "RestorePrefix",
                compat_prefix = "A",
                folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.NONE,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A1")],
                Folder_order = ["A1"]
            };
            table.entries.Single().playlist_id = table.playlist_id;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(table.entries.Single(), typeof(LR2SongDBExtended.playlist_entry));
                setup.InsertOrReplace(new LR2SongDB.song
                {
                    path = Path.Combine(bmsRoot, "chart.bms"),
                    hash = table.entries.Single().md5,
                    title = "Restored song"
                }, typeof(LR2SongDB.song));
            }
            var synchronization = new TestLr2PlaylistFolderSynchronizationPort(songDbPath)
            {
                PhysicalSurfaceFactory = () => Directory.Exists(testSettings.LR2CustomFolderOutputBaseDir)
                    ? CustomFolderOutputPhysicalSurface.FromEntries(
                        Directory.GetFiles(testSettings.LR2CustomFolderOutputBaseDir, "*.lr2folder", SearchOption.AllDirectories)
                            .Select(path => new FileInfo(path))
                            .Select(file => new RootFileEnumerationEntry(file.FullName, file.LastWriteTimeUtc, file.Length)),
                        discoveryComplete: true)
                    : CustomFolderOutputPhysicalSurface.Empty
            };
            playlist = new TestBmsPlaylist(songDbPath, configOwner, synchronization, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table]),
                StartupBackgroundTaskScheduler = (_, _, _, _) => false
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "restore_prefix_initial_output");
            string sql = playlist.GetPlaylistDump();
            DateTime lastUpdate = table.last_update;
            string outputPath = Path.Combine(testSettings.LR2CustomFolderOutputBaseDir, table.Output_dir, "0000.lr2folder");
            StringAssert.Contains(File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis")), "#TITLE A1");

            playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
            {
                [table] = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = "B" }
            }, capability: null);
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "restore_prefix_changed_output");
            Assert.AreEqual(lastUpdate, table.last_update);
            StringAssert.Contains(File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis")), "#TITLE B1");
            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
            CustomFolderOutputStatusRow beforeRestore = new PlaylistPersistenceRepository(songDbPath)
                .ReadCustomFolderOutputStatusRows()[table.playlist_id!.Value];
            Assert.AreEqual(lastUpdate.Ticks, beforeRestore.LastUpdateTicks);
            Assert.AreEqual(table.header_sha256 ?? string.Empty, beforeRestore.HeaderSha256);
            Assert.AreEqual(table.data_sha256 ?? string.Empty, beforeRestore.DataSha256);

            await playlist.RestorePlaylistDumpAsync(sql);

            BMSTable restored = playlist.BMSTables.Single();
            Assert.AreEqual("A", restored.compat_prefix);
            Assert.AreEqual(lastUpdate, restored.last_update);
            Assert.AreEqual("A1", restored.entries.Single().folder);
            string output = File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(output, "#TITLE A1");
            string command = output.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Single(line => line.StartsWith("#COMMAND ", StringComparison.Ordinal))["#COMMAND ".Length..];
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("A1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = ?;", table.playlist_id));
            Assert.AreEqual("A1", verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath).title);
            CollectionAssert.AreEqual(new[] { table.entries.Single().md5 },
                verify.Query<LR2SongDB.song>("SELECT song.* FROM song WHERE " + command).Select(row => row.hash).ToArray());
        }
        finally
        {
            playlist?.RequestShutdown("restore_prefix_test_cleanup");
            Directory.Delete(directory, recursive: true);
        }
    }

    // 復元確定後の出力先 I/O 失敗を、復元呼出元と必須準備の待機側へ同じ例外で伝える。
    /// <summary>実DBへの復元確定後に出力先I/Oを失敗させ、復元内容を保持しつつ呼出元と必須準備待機を同じ例外で終端させて待機の残留を検出します。</summary>
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
            // 配置検証ではなく、出力先の子にある通常ファイルとの衝突で I/O 失敗を起こす。
            string bmsRoot = Path.Combine(directory, "BMS");
            Directory.CreateDirectory(bmsRoot);
            LR2Config config = CreateLr2Config(lr2Root, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
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
                dbPath, configOwner, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => settings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(dbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            string backupPath = Path.Combine(directory, "restore.sql");
            File.WriteAllText(backupPath,
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(8011, "Restored root", "R")
                + "\v" + Environment.NewLine
                + "UPDATE playlist SET output_dir = 'BlockedRoot', is_root_folder = 1 WHERE playlist_id = 8011;");
            IOException restoreFailure = await Assert.ThrowsExceptionAsync<IOException>(() =>
                playlist.RestorePlaylistDumpAsync(File.ReadAllText(backupPath)));

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly())
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
                readiness);
            Assert.AreSame(restoreFailure, readinessFailure);
        }
        finally
        {
            // catch 欠落の negative control でも pending consumer を残さない。
            playlist?.RequestShutdown("restore_readiness_test_cleanup");
            if (readiness != null)
            {
                try { await readiness; }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>実DBのヘッダー・行を親要求で読み直し、公開中の再入復元を拒否して別hydration予約を作らず必須行読込の終端まで待つことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_ReloadsHeadersWithoutScoreInitialization()
    {
        testSettings.EnablePlaylistUrlCompletion = false;
        testSettings.OperationModeLR2DB = false;
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
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
            playlist.StartupBackgroundTaskScheduler = (taskName, _, _, _) =>
            {
                hydrationQueued |= taskName == "playlist_entries_hydration";
                return true;
            };

            Task? rejectedRestore = null;
            playlist.BMSTables.CollectionChanged += (_, _) =>
            {
                // 実collection公開中の再入操作は、新しいDB復元を開始しません。
                rejectedRestore ??= playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Rejected reload restore", "X"));
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedRestore, "restore rejection during publication");
                }
                catch (InvalidOperationException)
                {
                    // Busyによる明示失敗をevent終了後に対象Taskで検証します。
                }
            };
            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
            Assert.IsNotNull(rejectedRestore);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rejectedRestore!);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(7001, verify.Table<BMSTable>().Single().playlist_id);
            }

            Assert.IsFalse(hydrationQueued, "必須hydrationは親Taskが直接待ち、別予約を作りません。");
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationRequestedVersion);
            Assert.AreEqual(1, playlist.BMSTables.Count);
            Assert.AreEqual("ReloadedTable", playlist.BMSTables[0].name);
            Assert.IsTrue(playlist.BMSTables[0].ArePlaylistEntriesLoaded);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>二表の欠損出力と余分な出力を実ファイル・DBで修復し、現ファイル時刻を保ちながらreceipt、進捗の要求識別、失敗時の非active終端を確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_HydrationRepairRegeneratesBatchPrunesStaleRowsAndPreservesCurrentFiles()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = testSettings.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
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
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => repairSettings,
                synchronization, settings: testSettings)
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
            var capturedPhysicalSurface =
                CustomFolderOutputPhysicalSurface.FromEntries(physicalEntries, discoveryComplete: true);
            synchronization.PhysicalSurfaceFactory = () => capturedPhysicalSurface;
            PlaylistPersistenceRepository statusRepository = new(songDbPath);
            Dictionary<int, CustomFolderOutputStatusRow> seededStatusRows =
                statusRepository.ReadCustomFolderOutputStatusRows();
            Assert.IsTrue(seededStatusRows.TryGetValue(tableB.playlist_id!.Value, out CustomFolderOutputStatusRow? seededStatusB));
            seededStatusB!.PhysicalMtimeSignature = "stale-before-reload";
            statusRepository.PersistCustomFolderOutputStatusRows(seededStatusRows.Values);

            var repairProgress = new List<PlaylistSyncProgressSnapshot>();
            playlist.ProgressRequestFactory = (name, version) => new(7, 22, name, version);
            PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt? receipt = null;
            int completionVersionAtReceipt = -1;
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                completionVersionAtReceipt = playlist.PlaylistEntriesHydrationCompletedVersion;
            };
            playlist.CustomFolderOutputRepairProgressReporter = snapshot =>
            {
                repairProgress.Add(snapshot);
                throw new InvalidOperationException("deterministic progress observer failure");
            };

            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);

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

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleFile));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", missingFileA));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", missingFileB));
            }
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
            OperationProgressRequest repairRequest = receipt.ProgressRequest with { Source = "playlist_custom_folder_output_repair" };
            Assert.IsTrue(repairProgress.All(snapshot => snapshot.Source == "custom_folder_repair" && snapshot.Request == repairRequest));
            Assert.AreEqual(7L, repairRequest.Generation);
            Assert.AreEqual(22L, repairRequest.OperationToken);
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

            InvalidOperationException actualRepairFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => playlist.ReloadTablesAsync(exportBeatorajaBmt: false));

            Assert.AreSame(expectedRepairFailure, actualRepairFailure);
            Assert.IsTrue(repairProgress.Any(snapshot => snapshot.IsActive));
            OperationProgressRequest failedRepairRequest = repairProgress[0].Request;
            Assert.IsNotNull(failedRepairRequest);
            Assert.AreNotEqual(repairRequest, failedRepairRequest);
            Assert.AreEqual("playlist_custom_folder_output_repair", failedRepairRequest.Source);
            Assert.AreEqual(7L, failedRepairRequest.Generation);
            Assert.AreEqual(22L, failedRepairRequest.OperationToken);
            Assert.AreEqual(playlist.PlaylistEntriesHydrationRequestedVersion, failedRepairRequest.Version);
            Assert.IsTrue(repairProgress.All(snapshot => snapshot.Source == "custom_folder_repair" && snapshot.Request == failedRepairRequest));
            Assert.IsTrue(repairProgress.Take(repairProgress.Count - 1).All(snapshot => snapshot.IsActive));
            Assert.IsFalse(repairProgress[^1].IsActive);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>修復対象のない実再読込で、同じhydration要求を示す非active進捗だけを公開して架空の実行中表示を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_ZeroTargetRepairPublishesOnlyInactiveProgress()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = testSettings.LR2CustomFolderOutputBaseDirRootType,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => repairSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty), settings: testSettings);
            var repairProgress = new List<PlaylistSyncProgressSnapshot>();
            playlist.CustomFolderOutputRepairProgressReporter = repairProgress.Add;

            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);

            Assert.AreEqual(1, repairProgress.Count);
            Assert.AreEqual("custom_folder_repair", repairProgress[0].Source);
            Assert.IsNotNull(repairProgress[0].Request);
            Assert.AreEqual("playlist_custom_folder_output_repair", repairProgress[0].Request.Source);
            Assert.AreEqual(playlist.PlaylistEntriesHydrationRequestedVersion, repairProgress[0].Request.Version);
            Assert.IsFalse(repairProgress.Any(snapshot => snapshot.IsActive));
            Assert.IsFalse(repairProgress[^1].IsActive);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>P保有中の再読込と出力を拒否し、実DB・ファイル・同期回数を変えず、解放時の自動再実行を避けて次の明示再読込を受理します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadTables_BusyParentIsSideEffectFreeWithoutReservation()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDirectory = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDirectory;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDirectory,
                LR2CustomFolderOutputBaseDirRootType = testSettings.LR2CustomFolderOutputBaseDirRootType,
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

            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            var outputAdmission = new ChartFileOperationSynchronizer();
            var playlist = new TestBmsPlaylist(
                songDbPath,
                configOwner,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => repairSettings,
                synchronization,
                mutationAdmission: outputAdmission, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([persistedTable])
            };

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
            (long PlaylistCount, string Name, string Symbol, string OutputDirectory, long EntryCount, string EntryMd5, string EntryFolder, long FolderRowCount, long StatusRowCount) databaseBeforeRepair = SnapshotDatabase();
            int synchronizationOperationCountBeforeRepair = synchronization.Operations.Count;

            Assert.IsTrue(outputAdmission.TryEnter(out IDisposable heldOutput));
            using IDisposable incumbent = heldOutput;
            Task reloadTask = playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => reloadTask);

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



            Assert.AreEqual(0, synchronization.Operations.Count - synchronizationOperationCountBeforeRepair);
            Assert.AreEqual(databaseBeforeRepair, SnapshotDatabase());
            CollectionAssert.AreEqual(directoriesBeforeRepair, SnapshotDirectories());
            filesAfterRepair = SnapshotFiles();
            CollectionAssert.AreEquivalent(filesBeforeRepair.Keys.ToArray(), filesAfterRepair.Keys.ToArray());
            foreach (string path in filesBeforeRepair.Keys)
            {
                CollectionAssert.AreEqual(filesBeforeRepair[path], filesAfterRepair[path], path);
            }
            incumbent.Dispose();
            Assert.AreEqual(synchronizationOperationCountBeforeRepair, synchronization.Operations.Count, "P解放で任意修復を再実行しません。");
            await playlist.ReloadTablesAsync(exportBeatorajaBmt: false);
            Assert.IsTrue(synchronization.Operations.Count > synchronizationOperationCountBeforeRepair);
            Assert.IsTrue(Directory.GetFiles(Path.Combine(outputBaseDirectory, activeTable.Output_dir), "*.lr2folder").Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実初期化で完成receiptを版通知より先に公開し、捕捉後のモデル行削除でもreceiptの行集合と同じ参照を保ちます。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task InitializeAsync_PublishesLoadedReceiptBeforeCompletionVersion()
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
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            playlist.ProgressRequestFactory = (name, version) => new(8, 31, name, version);
            playlist.RequestProgressReporter = (request, running) => execution.Add((request, running));
            var requestNotifications = new List<OperationProgressRequest>();
            var completedNotifications = new List<OperationProgressRequest>();
            playlist.PlaylistEntriesHydrationRequested += (_, args) => requestNotifications.Add(args.Request);
            playlist.PlaylistEntriesHydrationCompleted += (_, args) => completedNotifications.Add(args.Request);
            PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt? receipt = null;
            int completedVersionAtReceipt = -1;
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                completedVersionAtReceipt = playlist.PlaylistEntriesHydrationCompletedVersion;
            };

            await playlist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false);

            Assert.IsNotNull(receipt);
            PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt completedReceipt = receipt!;
            Assert.AreEqual(1, completedReceipt.RequestVersion);
            var expectedRequest = new OperationProgressRequest(8, 31, "playlist_entries_hydration", 1);
            Assert.AreEqual(expectedRequest, completedReceipt.ProgressRequest);
            CollectionAssert.AreEqual(new[] { expectedRequest }, requestNotifications);
            CollectionAssert.AreEqual(new[] { expectedRequest }, completedNotifications);
            CollectionAssert.AreEqual(new[] { (expectedRequest, true), (expectedRequest, false) }, execution);
            Assert.AreEqual("Initialize", completedReceipt.Reason);
            Assert.AreEqual(1, completedReceipt.Tables.Count);
            Assert.AreSame(table, completedReceipt.Tables[0].Table);
            Assert.AreEqual(1, completedReceipt.Tables[0].Entries.Count);
            table.RemoveBMSTableEntries(table.entries.ToArray());
            Assert.AreEqual(1, completedReceipt.Tables[0].Entries.Count);
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

    /// <summary>必須receiptの受信先を失敗させ、P保有中に元例外で終端して完了版を進めず、受付を解放して次の明示初期化だけを実行します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task InitializeAsync_ConsumerFailureFaultsWithoutRetryAndReleasesAdmission()
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
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            int consumerCalls = 0;
            var failure = new InvalidOperationException("deterministic receipt consumer failure");
            EventHandler<PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceiptEventArgs> consumer = (_, _) =>
            {
                consumerCalls++;
                Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _), "Required publication still owns P.");
                throw failure;
            };
            playlist.PlaylistEntriesHydrationReceiptPublished += consumer;

            InvalidOperationException observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => playlist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false));
            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, consumerCalls);
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationRequestedVersion);
            Assert.AreEqual(0, playlist.PlaylistEntriesHydrationCompletedVersion);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
            playlist.PlaylistEntriesHydrationReceiptPublished -= consumer;
            await playlist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false);
            Assert.AreEqual(2, playlist.PlaylistEntriesHydrationCompletedVersion);
            Assert.AreEqual(1, consumerCalls, "A fresh request must not replay a failed receiver.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>単独モードで表名を実DBへ保存し、既存行を消さずにヘッダーだけを更新することを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableHeaderToDB_PersistsPlaylistNameInStandaloneMode()
    {
        testSettings.OperationModeLR2DB = false;
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            table.name = "AfterName";
            playlist.CommitBMSTableHeaderToDB(table);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("AfterName", verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = 7101;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 7101;"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>単独モードで互換prefixを変更し、ヘッダーと二行のフォルダ名を同じ実DBへ保存して部分更新を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitPlaylistHeaderEdits_PersistsPrefixRewrittenFoldersInStandaloneMode()
    {
        testSettings.OperationModeLR2DB = false;
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            DateTime lastUpdate = table.last_update;
            IReadOnlyList<PlaylistEntryCommitReceipt> receipts = playlist.CommitPlaylistHeaderEdits(
                new Dictionary<BMSTable, PlaylistHeaderEdit>
                { [table] = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = "★" } }, capability: null);
            Assert.AreEqual(lastUpdate, table.last_update);
            Assert.AreEqual(2, receipts.Single().Changes.Length);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            string firstFolder = verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = 7201 AND md5 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';");
            string secondFolder = verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = 7201 AND md5 = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';");
            CollectionAssert.AreEqual(new[] { "★1", "★2" }, new[] { firstFolder, secondFolder });
            Assert.AreEqual("★", verify.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id = 7201;"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>行未読込の表を保存する前に実DBから行を取得し、未読込を空集合として保存して既存行を消す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableWithEntriesToDB_HydratesUnloadedEntriesBeforeCommit()
    {
        testSettings.OperationModeLR2DB = false;
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
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { unloadedTable })
            };

            Assert.IsFalse(unloadedTable.ArePlaylistEntriesLoaded);
            playlist.CommitBMSTableWithEntriesToDB(unloadedTable);

            Assert.IsTrue(unloadedTable.ArePlaylistEntriesLoaded);
            Assert.AreEqual(1, unloadedTable.entries.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;",
                    playlistId,
                    persistedEntry.md5));
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
