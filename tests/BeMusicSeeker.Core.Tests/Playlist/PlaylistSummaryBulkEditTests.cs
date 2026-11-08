using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>一括編集の混在・保存互換・実保存失敗と、property/settingsの出力種別接続を検証します。</summary>
/// <remarks>判断とSQLiteの失敗回復は画面なしで検証し、compiled Binding、Automation、resizeとスクロールの到達は実WPFで観測します。WPFの共通資源を使うケースだけ非並列です。</remarks>
[TestClass]
public sealed class PlaylistSummaryBulkEditTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>登録済みfileの設定非継承取得もCompatibleで名前・記号を初期化し、既存entry/adddate/memo・日時・prefix・出力先を保ちます。</summary>
    [TestMethod]
    public async Task BulkExternalProperties_InvalidOptionalDatesStillInitializeNameAndSymbol()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistSummaryBulkEditTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tasks = new ConcurrentQueue<Task>();
        TestBmsPlaylist? playlist = null; TestBmsLibrary? library = null; PlaylistWorkspaceViewModel? workspace = null;
        Exception? primary = null;
        try
        {
            string path = CreateTempSongDbPath(databaseScope, directory); PlaylistPersistenceRepository.EnsureSchema(databaseScope, path);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"External name\",\"symbol\":\"EX\",\"last_update\":\"invalid header\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"title\":\"external row\",\"adddate\":1700000000}]");
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => { values.OperationModeLR2DB = false; values.EnablePlaylistUrlCompletion = false; });
            playlist = new TestBmsPlaylist(databaseScope, path, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }, settings: settings, uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) => { tasks.Enqueue(Task.Run(work)); return true; };
            library = new TestBmsLibrary(databaseScope, path, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: settings);
            DateTime update = new(2020, 1, 2), added = new(2019, 3, 4);
            var entry = new BMSTableEntry { title = "local row", adddate = added, memo = "local memo" };
            var table = new BMSTable
            {
                playlist_id = 7930,
                name = "Manual name",
                symbol = "M",
                Header_url = new Uri(header),
                Data_url = new Uri(data),
                compat_prefix = "P",
                Output_dir = "Fixed",
                last_update = update,
                is_bmt_output = false,
                entries = [entry]
            };
            playlist.BMSTables.Add(table); playlist.CommitBMSTableWithEntriesToDB(table);
            workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
            await workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync([new PlaylistSummaryRow { TableRef = table }],
                new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions { Name = true, Symbol = true });
            Assert.AreEqual("External name", table.name); Assert.AreEqual("EX", table.symbol);
            Assert.AreSame(entry, table.entries.Single()); Assert.AreEqual(added, entry.adddate); Assert.AreEqual("local memo", entry.memo);
            Assert.AreEqual(update, table.last_update); Assert.AreEqual("P", table.compat_prefix); Assert.AreEqual("Fixed", table.Output_dir);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), path).OpenSongDbReadOnly();
            BMSTable saved = db.Table<BMSTable>().Single(); BMSTableEntry savedEntry = db.Table<BMSTableEntry>().Single();
            Assert.AreEqual("External name", saved.name); Assert.AreEqual("EX", saved.symbol); Assert.AreEqual(update, saved.last_update);
            Assert.AreEqual(added, savedEntry.adddate); Assert.AreEqual("local memo", savedEntry.memo); Assert.AreEqual("local row", savedEntry.title);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<Exception>();
            async Task Clean(Func<Task> work) { try { await work(); } catch (Exception error) { failures.Add(error); } }
            if (playlist != null) { await Clean(playlist.WaitForPlaylistMutationIdleAsync); }
            if (workspace != null)
            {
                await Clean(workspace.WaitForDetailBuildIdleAsync); await Clean(workspace.WaitForPlaylistSummaryDataBuildIdleAsync); await Clean(workspace.WaitForPlaylistReloadCleanupIdleAsync);
                await Clean(() => { workspace.RefreshPlaylistTreeTables(null); workspace.Dispose(); return Task.CompletedTask; });
            }
            while (tasks.TryDequeue(out Task? task)) { await Clean(() => task); }
            playlist?.RequestShutdown("date-property-initialization"); library?.RequestShutdown("date-property-initialization");
            await Clean(() => { Directory.Delete(directory, recursive: true); return Task.CompletedTask; });
            if (failures.Count > 0) { if (primary == null) { throw new AggregateException(failures); } Console.WriteLine(new AggregateException(failures)); }
        }
    }

    /// <summary>六つの実反映入口で全件退役と無変更対象を含む一部退役を拒否し、実DB・管理BMT・生存正本と入力を保持します。別storeはBMTで代表確認し、同ID別参照は実再同期ケースへ分担します。通信前拒否は外部headerを削除して識別します。</summary>
    [TestMethod]
    [DataRow("bmt")]
    [DataRow("sync")]
    [DataRow("root")]
    [DataRow("mask")]
    [DataRow("base")]
    [DataRow("external")]
    public async Task BulkApply_RejectsEntireStaleSelectionBeforeChanges(string operation)
    {
        foreach (string staleKind in operation == "bmt" ? new[] { "all", "partial", "store" } : new[] { "all", "partial" })
        {
            await using var fixture = new PlaylistPrefixCommitTestFixture(enableBmtOutput: true, modelUiScheduler: new TestUiScheduler(() => null));
            BMSTable first = fixture.Table;
            first.custom_folder_output_base_name = "Old";
            first.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None;
            var second = new BMSTable
            {
                playlist_id = 7493,
                name = "Second",
                entries = [],
                is_bmt_output = true,
                custom_folder_output_base_name = "Old",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None,
                Header_url = first.Header_url,
                Data_url = first.Data_url
            };
            fixture.Playlist.BMSTables.Add(second);
            fixture.Playlist.CommitBMSTableHeadersToDB([first, second]);
            await fixture.Playlist.BmtOutput.ExportTablesAsync([first, second], "stale_bulk_setup");
            BMSPlaylist currentStore = fixture.Playlist;
            using PlaylistWorkspaceViewModel workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
                playlistStoreProvider: () => currentStore, playlistLibraryProvider: () => fixture.Library);
            PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel draft = workspace.OpenSummaryBulkEditDialog([new PlaylistSummaryRow { TableRef = first }, new PlaylistSummaryRow { TableRef = second }])
                ?? throw new AssertFailedException("一括編集を準備できません。");
            draft.BmtOutputOption = draft.OffOption;
            draft.ExternalSyncOption = draft.OnOption;
            draft.RootFolderOption = draft.OnOption;
            draft.OutputUserFolder = false;
            draft.OutputBaseOption = draft.OutputBaseOptions.Single(option => !option.IsNoChange);
            draft.InitializeNameFromExternal = true;
            if (staleKind == "partial")
            {
                second.is_bmt_output = false;
                second.EnableExternalSync();
                second.is_root_folder = true;
                second.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
                second.custom_folder_output_base_name = null;
                second.name = "Changed";
            }
            if (staleKind == "all") { fixture.Playlist.BMSTables.Clear(); }
            else if (staleKind == "partial") { fixture.Playlist.BMSTables.Remove(second); }
            else
            {
                currentStore = new TestBmsPlaylist(fixture.DatabaseScope, fixture.SongDb, new TestLr2PlaylistFolderSynchronizationPort(fixture.DatabaseScope, fixture.SongDb), new TestUiScheduler(() => null), settings: TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false));
                currentStore.BMSTables = new ObservableCollection<BMSTable>([first, second]);
            }
            string[] beforeModel = fixture.Playlist.BMSTables.Select(CaptureBulkHeader).ToArray();
            string[] beforeDb;
            using (LR2SongDBExtended db = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb)) { beforeDb = db.Table<BMSTable>().OrderBy(table => table.playlist_id).Select(CaptureBulkHeader).ToArray(); }
            string bmtRoot = Path.GetDirectoryName(fixture.BmtManifestPath) ?? throw new AssertFailedException();
            Dictionary<string, byte[]> beforeFiles = Directory.GetFiles(bmtRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            File.Delete(fixture.HeaderPath);
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ApplyBulkDraftAsync(draft, operation), operation + "/" + staleKind);
                CollectionAssert.AreEqual(beforeModel, fixture.Playlist.BMSTables.Select(CaptureBulkHeader).ToArray());
                using LR2SongDBExtended db = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb);
                CollectionAssert.AreEqual(beforeDb, db.Table<BMSTable>().OrderBy(table => table.playlist_id).Select(CaptureBulkHeader).ToArray());
                CollectionAssert.AreEquivalent(beforeFiles.Keys.ToArray(), Directory.GetFiles(bmtRoot, "*", SearchOption.AllDirectories));
                foreach (KeyValuePair<string, byte[]> file in beforeFiles) { CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key)); }
                Assert.AreSame(draft.OffOption, draft.BmtOutputOption);
                Assert.AreSame(draft.OnOption, draft.ExternalSyncOption);
                Assert.AreSame(draft.OnOption, draft.RootFolderOption);
                Assert.AreEqual(false, draft.OutputUserFolder);
                Assert.IsTrue(draft.InitializeNameFromExternal);
                Assert.IsTrue(currentStore.CanEnterPlaylistMutation);
            }
            finally
            {
                if (!ReferenceEquals(currentStore, fixture.Playlist)) { currentStore.RequestShutdown("stale_bulk_test"); }
            }
        }
    }

    /// <summary>実外部再同期で同IDの別正本へ置換した後は古いBMT編集を拒否し、旧選択から開き直した編集だけがDBと実出力を変更します。</summary>
    [TestMethod]
    public async Task BulkBmtApply_ExternalReplacementRejectsOldDraftAndReopenedDraftSaves()
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(enableBmtOutput: true, modelUiScheduler: new TestUiScheduler(() => null));
        var oldRow = new PlaylistSummaryRow { TableRef = fixture.Table };
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel draft = fixture.Workspace.OpenSummaryBulkEditDialog([oldRow]) ?? throw new AssertFailedException();
        draft.BmtOutputOption = draft.OffOption;
        File.WriteAllText(fixture.HeaderPath, "{\"name\":\"Replacement\",\"symbol\":\"NEW\",\"data_url\":\"data.json\"}");
        PlaylistExternalSyncOwner.PlaylistReloadOperationResult reload = await fixture.Playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([fixture.Table]);
        reload.ThrowIfFailed();
        BMSTable current = fixture.Playlist.BMSTables.Single();
        Assert.AreNotSame(fixture.Table, current);
        Assert.AreEqual(fixture.Table.playlist_id, current.playlist_id);
        string root = Path.GetDirectoryName(fixture.BmtManifestPath) ?? throw new AssertFailedException();
        Dictionary<string, byte[]> before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.IsTrue(before.Keys.Any(path => path.EndsWith(".bmt", StringComparison.OrdinalIgnoreCase)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(draft.ApplyBmtOutputAsync);
        Assert.IsTrue(current.is_bmt_output);
        using (LR2SongDBExtended db = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb)) { Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT is_bmt_output FROM playlist WHERE playlist_id = ?", current.playlist_id)); }
        foreach (KeyValuePair<string, byte[]> file in before) { CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key)); }
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel reopened = fixture.Workspace.OpenSummaryBulkEditDialog([oldRow]) ?? throw new AssertFailedException();
        reopened.BmtOutputOption = reopened.OffOption;
        await reopened.ApplyBmtOutputAsync();
        Assert.IsFalse(current.is_bmt_output);
        using (LR2SongDBExtended db = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb)) { Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT is_bmt_output FROM playlist WHERE playlist_id = ?", current.playlist_id)); }
        Assert.IsFalse(Directory.GetFiles(root, "*.bmt", SearchOption.AllDirectories).Any());
    }

    private static string CaptureBulkHeader(BMSTable table) => string.Join("|", table.playlist_id, table.name,
        table.is_bmt_output, table.is_external_sync, table.is_root_folder, table.ignore_folder_output,
        table.custom_folder_output_base_name, table.output_dir, table.symbol, table.compat_prefix);

    private static Task ApplyBulkDraftAsync(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel draft, string operation) => operation switch
    {
        "bmt" => draft.ApplyBmtOutputAsync(),
        "sync" => draft.ApplyExternalSyncAsync(),
        "root" => draft.ApplyRootFolderAsync(),
        "mask" => draft.ApplyCustomFolderOutputTypesAsync(),
        "base" => draft.ApplyOutputBaseAsync(),
        "external" => draft.ApplyExternalPropertyInitializationAsync(),
        _ => throw new AssertFailedException(operation)
    };

    /// <summary>実外部属性初期化のprefix候補をDBと詳細へ配送し、全体/folder選択と次sort要求の終端を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BulkExternalPrefix_UsesCommittedEntryReceiptForExistingDetail(bool selectedFolder)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(modelUiScheduler: new TestUiScheduler(() => null));
        BMSTable table = fixture.Table;
        if (selectedFolder) { fixture.Workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("P1")); }
        await fixture.Refresh();
        BMSTableEntry target = table.entries[0];
        DateTime date = table.last_update;
        Task selectionRefresh = Task.CompletedTask;
        fixture.Workspace.PlaylistDetailReloadRefreshRequested += (_, _) => { selectionRefresh = fixture.Refresh(); };
        await fixture.Workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync([new PlaylistSummaryRow { TableRef = table }],
            new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions { CompatPrefix = true });
        await selectionRefresh;
        Assert.AreEqual("Q", table.compat_prefix);
        Assert.AreEqual(date, table.last_update);
        Assert.AreSame(target, table.entries[0]);
        Assert.AreEqual("current 0", target.memo);
        CollectionAssert.AreEqual(new[] { "Q1", "Q2", "QX", "QQ3" }, table.entries.Select(entry => entry.folder).ToArray());
        CollectionAssert.AreEqual(new[] { "QQ3", "QX", "Q2", "Q1" }, table.Folder_order.ToArray());
        Assert.AreEqual(selectedFolder ? "Q1" : null, fixture.Workspace.CapturePlaylistDetailSelection().FolderName);
        Assert.AreEqual(table.PlaylistEntriesRevision, fixture.Workspace.DetailViewState.Source.LastBuiltPlaylistRevision);
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Folder), ListSortDirection.Descending);
        await fixture.Refresh();
        Assert.AreEqual(selectedFolder ? 1 : 4, fixture.Workspace.DetailViewState.View.Rows.Count);
        Assert.IsTrue(fixture.Workspace.DetailViewState.View.Rows.Cast<PlaylistDetailRow>().All(row => row.Folder.StartsWith("Q", StringComparison.Ordinal)));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual("Q", db.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id=7492;"));
        Assert.AreEqual("Q1", db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=7492 AND title='entry 0';"));
    }

    /// <summary>bulk prefixの実DB前拒否と確定後通知故障を分け、旧状態維持/確定差分配送を実DBと既存詳細で確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BulkExternalPrefix_FailuresRespectDatabaseCommitBoundary(bool afterCommit)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(modelUiScheduler: new TestUiScheduler(() => null));
        await fixture.Refresh();
        int version = fixture.Table.PlaylistEntriesRevision;
        DateTime date = fixture.Table.last_update;
        int notifications = 0;
        fixture.Table.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(BMSTable.Folder_order)) { return; }
            notifications++;
            using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
            Assert.AreEqual("Q", db.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id=7492;"));
            if (afterCommit) { throw new InvalidOperationException("bulk prefix notification failed"); }
        };
        if (!afterCommit) { fixture.Execute("CREATE TRIGGER fail_bulk_prefix BEFORE INSERT ON playlist BEGIN SELECT RAISE(ABORT, 'bulk prefix DB failed'); END;"); }
        Exception? failure = null;
        try
        {
            await fixture.Workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync([new PlaylistSummaryRow { TableRef = fixture.Table }],
                new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions { CompatPrefix = true });
        }
        catch (Exception caught) { failure = caught; }
        Assert.IsNotNull(failure);
        Assert.AreEqual(afterCommit ? "Q" : "P", fixture.Table.compat_prefix);
        Assert.AreEqual(afterCommit ? "Q1" : "P1", fixture.Table.entries[0].folder);
        Assert.AreEqual(version + (afterCommit ? 1 : 0), fixture.Table.PlaylistEntriesRevision);
        Assert.AreEqual(date, fixture.Table.last_update);
        Assert.AreEqual(afterCommit ? 1 : 0, notifications);
        Assert.AreEqual(fixture.Table.PlaylistEntriesRevision, fixture.Workspace.DetailViewState.Source.LastBuiltPlaylistRevision);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(afterCommit ? "Q" : "P", verify.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id=7492;"));
        Assert.AreEqual(afterCommit ? "Q1" : "P1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=7492 AND title='entry 0';"));
    }


    /// <summary>四bulk入口のcatalog予約失敗でも必要BMTを試し、header配送中はPを保持します。BMT失敗が先行する場合はその原因を維持します。</summary>
    [TestMethod]
    [DataRow("types", false)]
    [DataRow("sync", false)]
    [DataRow("root", false)]
    [DataRow("base", false)]
    [DataRow("types", true)]
    [DataRow("sync", true)]
    [DataRow("root", true)]
    [DataRow("base", true)]
    public async Task BulkHeaderCatalogQueueFailure_DoesNotSkipOutputOrHeaderDelivery(string route, bool failOutput)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(enableBmtOutput: true, modelUiScheduler: new TestUiScheduler(() => null));
        fixture.Workspace.Dispose();
        if (route == "base")
        {
            fixture.Table.custom_folder_output_base_name = "retired";
            fixture.Playlist.CommitBMSTableHeaderToDB(fixture.Table);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        var catalogFailure = new InvalidOperationException("catalog queue rejected");
        async Task Apply(Action action)
        {
            if (hold) { entered.TrySetResult(); await release.Task; }
            action();
        }
        using PlaylistWorkspaceViewModel workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
            playlistStoreProvider: () => fixture.Playlist, playlistLibraryProvider: () => fixture.Library,
            restoreUiApplyScheduler: Apply,
            catalogNotificationQueue: action =>
            {
                if (hold) { catalogEntered.TrySetResult(); throw catalogFailure; }
                action();
            });
        workspace.RefreshPlaylistTreeTables(fixture.Playlist, fixture.Library);
        workspace.SetPlaylistSummaryMode(enabled: true); workspace.RebuildPlaylistSummaryView(runAsync: false);
        PlaylistSummaryRow row = workspace.PlaylistSummaryView.Single();
        int revision = fixture.Table.PlaylistEntriesRevision;
        DateTime update = fixture.Table.last_update;
        int projected = 0;
        row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(PlaylistSummaryRow.Name)) { projected++; } };
        var outputFailures = new List<BmtTableExportService.FileOperationFailure>();
        fixture.Playlist.BmtOutput.FailureReporter = failures => outputFailures.AddRange(failures);
        FileStream? blocker = failOutput ? new FileStream(fixture.BmtManifestPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        Task? operation = null;
        try
        {
            hold = true;
            operation = route switch
            {
                "types" => workspace.ApplyPlaylistSummaryCustomFolderOutputTypes([row], new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch { AllSongsFolder = false }),
                "sync" => workspace.ApplyPlaylistSummaryExternalSync([row], true),
                "root" => workspace.ApplyPlaylistSummaryRootFolder([row], true),
                _ => workspace.ApplyPlaylistSummaryOutputBase([row], string.Empty)
            };
            await TestUiDispatcherHost.AwaitNotificationAsync(Task.WhenAll(entered.Task, catalogEntered.Task), operation, "bulk catalog failure and header gate");
            Assert.IsFalse(operation.IsCompleted); Assert.AreEqual(0, projected);
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Playlist.AcquirePlaylistMutationLease("bulk-header-gate-competing"));
            if (failOutput) { Assert.IsTrue(outputFailures.Count > 0); }
            else
            {
                string output = Path.GetDirectoryName(fixture.BmtManifestPath) ?? throw new AssertFailedException();
                Assert.AreEqual(1, Directory.GetFiles(output, "*.bmt").Length, "catalog予約が失敗しても必要なBMTを出力します。");
            }
            release.TrySetResult();
            if (failOutput)
            {
                PlaylistMutationPostCommitException failure = await Assert.ThrowsExactlyAsync<PlaylistMutationPostCommitException>(() => operation);
                Assert.IsNotNull(failure.InnerException);
                Assert.IsInstanceOfType<IOException>(failure.InnerException);
                StringAssert.Contains(failure.InnerException.Message, BmtTableExportService.ManifestFileName);
            }
            else { Assert.AreSame(catalogFailure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operation)); }
            Assert.AreEqual(1, projected); Assert.AreSame(row, workspace.PlaylistSummaryView.Single());
            Assert.AreEqual(revision, fixture.Table.PlaylistEntriesRevision); Assert.AreEqual(update, row.LastUpdate);
            Assert.AreEqual(fixture.Table.is_external_sync, row.IsExternalSync); Assert.AreEqual(fixture.Table.is_root_folder, row.IsRootFolder);
            Assert.AreEqual(fixture.Table.custom_folder_output_base_name ?? string.Empty, row.OutputBaseName);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
            BMSTable saved = verify.Table<BMSTable>().Single();
            Assert.AreEqual(fixture.Table.ignore_folder_output, saved.ignore_folder_output);
            Assert.AreEqual(fixture.Table.is_external_sync, saved.is_external_sync); Assert.AreEqual(fixture.Table.is_root_folder, saved.is_root_folder);
            Assert.AreEqual(fixture.Table.custom_folder_output_base_name, saved.custom_folder_output_base_name);
            using LibraryFileMutationLease next = fixture.Playlist.AcquirePlaylistMutationLease("bulk-header-gate-next");
        }
        finally
        {
            release.TrySetResult();
            if (operation != null) { try { await operation; } catch (Exception error) when (error is InvalidOperationException or PlaylistMutationPostCommitException) { } }
            blocker?.Dispose();
            await fixture.Playlist.WaitForPlaylistMutationIdleAsync();
            workspace.StopPlaylistSummaryDataBuild(); await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        }
    }

    /// <summary>実BMT台帳の読取り故障をDB確定後に発生させ、概要コピー/候補または採用済み詳細を確定値へ進めます。二次通知故障を重ねても出力原因を保ち、Pと進捗・receiptの終端を実Taskで確認します。DB前の旧状態は既存DurableFailureとprefix境界ケースが担当します。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task BulkExternalProperties_BmtFailureRefreshesCommittedPresentationAndPreservesPrimary(
        bool summaryActive, bool failSecondaryNotifications)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(enableBmtOutput: true, modelUiScheduler: new TestUiScheduler(() => null));
        var receiptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?>? operation = null;
        try
        {
            await fixture.Refresh();
            if (summaryActive)
            {
                fixture.Workspace.SetPlaylistSummaryMode(enabled: true);
                fixture.Workspace.RebuildPlaylistSummaryView(runAsync: false);
                Assert.AreEqual("Prefix", fixture.Workspace.PlaylistSummaryView.Single().Name);
            }
            BMSTableEntry entry = fixture.Table.entries[0];
            int version = fixture.Table.PlaylistEntriesRevision;
            DateTime date = fixture.Table.last_update;
            Assert.AreEqual("PF", fixture.Library.GetPlaylistReferenceDisplay(entry.md5, null).Symbols);
            File.WriteAllText(fixture.HeaderPath, "{\"name\":\"Changed Prefix\",\"symbol\":\"NEW\",\"compat_prefix\":\"Q\",\"data_url\":\"data.json\"}");
            var secondary = new InvalidOperationException("bulk presentation notification failed");
            int summaryRequests = 0, catalogNotifications = 0, keywordNotifications = 0, entryNotifications = 0;
            bool headerGuardsReleased = false, collectionGuardReleased = false;
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            var receipts = new ConcurrentQueue<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            var keywordCandidates = new ConcurrentQueue<string[]>();
            var bmtFailures = new List<BmtTableExportService.FileOperationFailure>();
            fixture.Playlist.BmtOutput.FailureReporter = failures =>
            {
                bmtFailures.AddRange(failures);
                fixture.Workspace.ReportBmtOutputFailures(failures);
            };
            fixture.Playlist.PlaylistEntryCommitsAvailable += (_, _) => entryNotifications++;
            fixture.Workspace.PlaylistCatalogChanged += (_, _) =>
            {
                catalogNotifications++;
                headerGuardsReleased = !fixture.Table.ReaderWriterLock.IsReadLockHeld && !fixture.Table.ReaderWriterLock.IsWriteLockHeld;
                // 同threadのcollection readerが残っていればwriter昇格は失敗する。
                fixture.Playlist.AcquireWriterLockBMSTables();
                try { collectionGuardReleased = true; }
                finally { fixture.Playlist.FreeWriterLockBMSTables(); }
                if (failSecondaryNotifications) { throw secondary; }
            };
            fixture.Workspace.PlaylistPresentationRefreshRequested += (_, request) =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.SummaryData
                    && request.Reason == "playlist_summary_external_property_initialization")
                {
                    summaryRequests++;
                    if (failSecondaryNotifications) { throw secondary; }
                }
            };
            fixture.Workspace.PlaylistKeywordValueCandidatesChanged += (_, _) =>
            {
                keywordNotifications++;
                keywordCandidates.Enqueue(fixture.Workspace.GetPlaylistKeywordValueCandidates().ToArray());
                if (failSecondaryNotifications) { throw secondary; }
            };
            fixture.Workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            fixture.Workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
            {
                receipts.Enqueue(request);
                if (request.RouteName != "playlist summary external property initialization notification") { return; }
                receiptEntered.TrySetResult();
                releaseReceipt.Task.GetAwaiter().GetResult();
                if (failSecondaryNotifications) { throw secondary; }
            };
            using var blocker = new FileStream(fixture.BmtManifestPath, FileMode.Open, FileAccess.Read, FileShare.None);
            operation = Task.Run(async () =>
            {
                try
                {
                    await fixture.Workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                        [new PlaylistSummaryRow { TableRef = fixture.Table }],
                        new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                        { Name = true, Symbol = true, CompatPrefix = true });
                    return (Exception?)null;
                }
                catch (Exception failure) { return failure; }
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(receiptEntered.Task, operation, "bulk external properties terminal receipt");
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsFalse(fixture.Playlist.WaitForPlaylistMutationIdleAsync().IsCompleted);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                using LibraryFileMutationLease conflicting = fixture.Playlist.AcquirePlaylistMutationLease("bulk-notification-busy");
            });
            releaseReceipt.TrySetResult();
            Exception? failure = await operation;
            Assert.IsNotNull(failure);
            Assert.IsInstanceOfType<PlaylistMutationPostCommitException>(failure);
            Assert.IsNotNull(failure.InnerException);
            Assert.IsInstanceOfType<IOException>(failure.InnerException);
            StringAssert.Contains(failure.InnerException.Message, BmtTableExportService.ManifestFileName);
            Assert.IsTrue(bmtFailures.Count > 0);
            Assert.AreNotSame(secondary, failure);
            Assert.AreNotSame(secondary, failure.InnerException);
            await fixture.Workspace.WaitForDetailBuildIdleAsync();
            await fixture.Workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            Assert.AreSame(entry, fixture.Table.entries[0]);
            Assert.AreEqual(version + 1, fixture.Table.PlaylistEntriesRevision);
            Assert.AreEqual(date, fixture.Table.last_update);
            Assert.AreEqual("Changed Prefix", fixture.Table.name);
            Assert.AreEqual("NEW", fixture.Table.symbol);
            Assert.AreEqual("Q", fixture.Table.compat_prefix);
            Assert.AreEqual("Q1", entry.folder);
            Assert.AreEqual("NEW", fixture.Library.GetPlaylistReferenceDisplay(entry.md5, null).Symbols);
            Assert.AreEqual(1, entryNotifications);
            Assert.AreEqual(1, catalogNotifications);
            Assert.IsTrue(headerGuardsReleased);
            Assert.IsTrue(collectionGuardReleased);
            Assert.AreEqual(0, summaryRequests, "header投影は全summary更新の要求を退役します。現在行の値は下で確認します。");
            Assert.AreEqual(1, keywordNotifications);
            CollectionAssert.AreEqual(new[] { "Changed Prefix" }, keywordCandidates.Single());
            if (summaryActive)
            {
                PlaylistSummaryRow row = fixture.Workspace.PlaylistSummaryView.Single();
                Assert.AreEqual("Changed Prefix", row.Name);
                Assert.AreEqual("NEW", row.Symbol);
                Assert.AreEqual("Q", row.CompatPrefix);
            }
            else
            {
                Assert.AreEqual(fixture.Table.PlaylistEntriesRevision, fixture.Workspace.DetailViewState.Source.LastBuiltPlaylistRevision);
                CollectionAssert.AreEquivalent(new[] { "Q1", "Q2", "QX", "QQ3" },
                    fixture.Workspace.DetailViewState.Source.Rows.Select(row => row.Folder).ToArray());
            }
            PlaylistSyncProgressSnapshot[] bulkProgress = progress.Where(snapshot => snapshot.Source == "playlist_summary_bulk").ToArray();
            Assert.IsTrue(bulkProgress.Length > 0);
            Assert.IsFalse(bulkProgress[^1].IsActive);
            // 外部属性の初期化は種別の事実で通知し、翻訳済み文言を持たせない。進捗に載る表名は生の名前のままである。
            PlaylistSyncProgressSnapshot[] initializationProgress = bulkProgress.Where(snapshot => snapshot.Kind == PlaylistSyncProgressKind.ExternalPropertyInitialization).ToArray();
            Assert.IsTrue(initializationProgress.Length > 0);
            Assert.IsTrue(initializationProgress.All(snapshot => snapshot.CustomFolderStage == CustomFolderProgressStage.TableName && snapshot.ImportPhase == PlaylistImportProgressPhase.None));
            // custom folder出力の段階通知は段階の事実で届き、段階を表す通知は表名を持たない。
            Assert.IsTrue(bulkProgress.Where(snapshot => snapshot.CustomFolderStage != CustomFolderProgressStage.TableName)
                .All(snapshot => string.IsNullOrEmpty(snapshot.CurrentTableName) && snapshot.Kind == PlaylistSyncProgressKind.CustomFolderOutput));
            Assert.AreEqual(1, receipts.Count(request => request.RouteName == "playlist summary external property initialization notification"));
            await fixture.Playlist.WaitForPlaylistMutationIdleAsync();
            using (fixture.Playlist.AcquirePlaylistMutationLease("bulk-failure-next-request")) { }
            using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
            Assert.AreEqual("Changed Prefix", db.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id=7492;"));
            Assert.AreEqual("NEW", db.ExecuteScalar<string>("SELECT symbol FROM playlist WHERE playlist_id=7492;"));
            Assert.AreEqual("Q", db.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id=7492;"));
            Assert.AreEqual("Q1", db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=7492 AND title='entry 0';"));
        }
        finally
        {
            releaseReceipt.TrySetResult();
            if (operation != null) { await operation; }
        }
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>対象二表が同じ出力種別を有効にしている場合、別種別の除外差を無視して一括編集の有効状態を返すことを確認します。</summary>
    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsValueWhenAllTablesMatch()
    {
        BMSTable first = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        BMSTable second = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder);

        bool? state = PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [first, second],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.AreEqual(true, state);
    }

    /// <summary>同じ出力種別が有効・無効の二表を選ぶと不定状態を返し、混在設定を一律の値として表示する誤りを検出します。</summary>
    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsNullWhenTablesAreMixed()
    {
        BMSTable enabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        BMSTable disabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        bool? state = PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [enabled, disabled],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.IsNull(state);
    }

    /// <summary>UserFolderを未変更、ClearFolderを有効とするpatchで、未変更の既存無効状態を保ちながら指定種別だけを変更します。</summary>
    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_KeepsIndeterminateValues()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        var patch = new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            UserFolder = null,
            ClearFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.UserFolder));
        Assert.IsTrue(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.ClearFolder));
    }

    /// <summary>フォルダ単位の表へレベル別出力を有効指定しても禁止状態を維持し、一括編集から利用不可の出力を有効にする誤りを検出します。</summary>
    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_DisablesLevelFolderForFolderEntryUnit()
    {
        BMSTable table = CreateTable(
            LR2SongDBExtended.playlist.CustomFolderType.None,
            LR2SongDBExtended.playlist.EntryUnitType.Folder);
        var patch = new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            LevelFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.LevelFolder));
    }

    /// <summary>旧保存mask 0x7Fを評価し、後発の最終プレイ・BPM・全曲出力も有効として解釈する保存互換を維持します。</summary>
    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_TreatsOldAllFoldersMaskAsNewTypesEnabled()
    {
        BMSTable table = CreateTable((LR2SongDBExtended.playlist.CustomFolderType)0x7F);

        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder));
        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder));
        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder));
    }

    /// <summary>実workspaceの現正本から一括編集draftを作り、最終プレイ出力を選ぶと反映操作を許可することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistSummaryBulkEditDialogViewModel_AllowsLastPlaySortFolderRegardlessOfSchemaStatus()
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture(modelUiScheduler: new TestUiScheduler(() => null));
        var dialog = new PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel(
            fixture.Workspace,
            [new PlaylistSummaryRow { TableRef = fixture.Table }]);

        dialog.OutputLastPlaySortFolder = true;

        Assert.IsTrue(dialog.CanApplyCustomFolderOutput);
    }

    /// <summary>外部JSON取得後の実DB保存を失敗させ、取得元を含む一括進捗を終端させながらsummary・catalog・検索候補の成功通知を公開しないことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistSummaryBulkExternalPropertyInitialization_DurableFailureSuppressesLatePresentation()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        testSettings.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(
            migrationWorkspace.RootDirectory,
            nameof(PlaylistSummaryBulkEditTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string scorePath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerPath,
                CreateUtf8BomBytes("{\"name\":\"External Name\",\"symbol\":\"E\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(
                scorePath,
                CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(databaseScope, tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope,
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: testSettings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));
            table.playlist_id = 8101;
            table.name = "Local Name";
            table.symbol = "L";
            table.Header_url = new Uri(headerPath);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);

            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(
                playlist,
                library: null!,
                settingsProvider: () => new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                });
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            int summaryRefreshCount = 0;
            int catalogChangedCount = 0;
            int keywordValueCandidatesChangedCount = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => summaryRefreshCount++;
            workspace.PlaylistCatalogChanged += (_, _) => catalogChangedCount++;
            workspace.PlaylistKeywordValueCandidatesChanged += (_, _) => keywordValueCandidatesChangedCount++;

            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            Exception? failure = null;
            try
            {
                await workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                    [new PlaylistSummaryRow { TableRef = table }],
                    new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                    {
                        Name = true,
                        Symbol = true
                    });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            Assert.IsNotNull(failure);
            PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
            Assert.IsTrue(notifications.Length > 0);
            Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "playlist_summary_bulk"));
            Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CurrentUri == new Uri(headerPath)),
                "取得元の通知も一括編集が所有する進捗として届く。");
            Assert.IsFalse(notifications[^1].IsActive);
            Assert.IsTrue(notifications.Where(snapshot => snapshot.IsActive).All(snapshot => snapshot.Kind is PlaylistSyncProgressKind.ExternalPropertyInitialization or PlaylistSyncProgressKind.CustomFolderOutput),
                "外部属性初期化の通知は種別の事実で届き、翻訳済み文言を持たない（出力を伴う場合は概要から行うcustom folder出力の種別を含む）。");

            Assert.AreEqual(0, summaryRefreshCount);
            Assert.AreEqual(0, catalogChangedCount);
            Assert.AreEqual(0, keywordValueCandidatesChangedCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>未登録名から通常・追加先への一覧再選択で実DBと必要出力を確定し、通常先の無関係なファイル・管理行を保ちます。外部名初期化では未登録名を維持して出力だけを停止します。</summary>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("additional")]
    [DataRow("external-name")]
    public async Task BulkOutputChange_UnregisteredOldBasePreservesUnrelatedNormalOutput(string operation)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistSummaryBulkEditTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        try
        {
            string songDb = CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDb);
            string normal = Path.Combine(directory, "Normal"), additional = Path.Combine(directory, "Additional");
            Settings settings = TestSettingsFactory.CreateIsolatedSettings();
            settings.OperationModeLR2DB = true;
            settings.EnableBeatorajaBmtOutput = false;
            settings.EnablePlaylistUrlCompletion = false;
            settings.LR2RootPath = directory;
            settings.LR2CustomFolderOutputBaseDir = normal;
            settings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "Root");
            settings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additional]);
            playlist = new TestBmsPlaylist(databaseScope, songDb, CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songDb, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null), settings: settings);
            var table = new BMSTable
            {
                playlist_id = 8103,
                name = "Unknown",
                symbol = "T",
                custom_folder_output_base_name = "Missing",
                entries = [CreateEntry(new string('a', 32), "Folder")],
                Folder_order = ["Folder"],
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
            };
            string header = Path.Combine(directory, "header.json");
            File.WriteAllBytes(header, CreateUtf8BomBytes("{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"data.json\"}"));
            File.WriteAllBytes(Path.Combine(directory, "data.json"), CreateUtf8BomBytes("[]"));
            table.Header_url = new Uri(header);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTablesWithEntriesToDB([table]);
            string unrelatedDirectory = Path.Combine(normal, "Unknown");
            Directory.CreateDirectory(unrelatedDirectory);
            string sentinel = Path.Combine(unrelatedDirectory, "keep.txt");
            File.WriteAllText(sentinel, "unrelated normal output");
            // 通常先を選ぶ場合、その表ディレクトリは新しい管理領域です。管理行の保全は兄弟領域で確認します。
            // 追加先を選ぶ場合は、旧先と誤認されていた通常先の同名ディレクトリ内の行を保全します。
            string preservedRowPath = operation == "normal" ? Path.Combine(normal, "Sibling", "keep.lr2folder") : sentinel;
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDb))
            {
                db.InsertOrReplace(new LR2SongDB.folder { path = preservedRowPath, title = "Keep", type = 2 }, typeof(LR2SongDB.folder));
            }
            using PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, null!,
                settingsProvider: () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
            PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel draft = workspace.OpenSummaryBulkEditDialog([new PlaylistSummaryRow { TableRef = table }])
                ?? throw new AssertFailedException("一括編集を準備できません。");
            string? expectedBase = operation == "normal" ? null : operation == "additional" ? "Additional" : "Missing";
            if (operation == "external-name")
            {
                draft.InitializeNameFromExternal = true;
                await draft.ApplyExternalPropertyInitializationAsync();
            }
            else
            {
                draft.OutputBaseOption = draft.OutputBaseOptions.Single(option => !option.IsNoChange && option.BaseName == expectedBase);
                await draft.ApplyOutputBaseAsync();
            }
            Assert.AreEqual(expectedBase, table.custom_folder_output_base_name);
            Assert.AreEqual(operation == "external-name" ? "External" : "Unknown", table.name);
            Assert.AreEqual("unrelated normal output", File.ReadAllText(sentinel));
            using LR2SongDBExtended read = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly();
            BMSTable saved = read.Table<BMSTable>().Single(item => item.playlist_id == table.playlist_id);
            Assert.AreEqual(expectedBase, saved.custom_folder_output_base_name);
            Assert.AreEqual(table.name, saved.name);
            Assert.AreEqual("Keep", read.Table<LR2SongDB.folder>().Single(row => row.path == preservedRowPath).title);
            if (operation == "external-name")
            {
                Assert.IsFalse(Directory.GetFiles(normal, "*.lr2folder", SearchOption.AllDirectories).Any());
                Assert.IsFalse(Directory.Exists(additional));
            }
            else
            {
                string output = Path.Combine(operation == "normal" ? normal : additional, "Unknown", "0000.lr2folder");
                Assert.IsTrue(File.Exists(output), "未解決の旧先を同一先として扱わず、選択した新先へ生成します。");
                Assert.AreEqual("Folder", read.Table<LR2SongDB.folder>().Single(row => row.path == output).title);
            }
        }
        finally
        {
            playlist?.RequestShutdown("unknown-bulk-output-fixture");
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>BMT・外部同期・root・出力mask・基点の保存を実DB triggerで失敗させ、同じ表と保存値を戻し、同じdraftの次の明示反映を保存します。</summary>
    [TestMethod]
    // 各条件は別の保存列を変更し、同じdraftで失敗後の再保存を観測します。
    [DataRow("bmt")]
    [DataRow("sync")]
    [DataRow("root")]
    [DataRow("mask")]
    [DataRow("base")]
    public async Task BulkHeaderSaveFailure_RestoresCurrentTableAndSameDraftCanSave(string operation)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(PlaylistSummaryBulkEditTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string songDb = CreateTempSongDbPath(databaseScope, root);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDb);
            Settings settings = TestSettingsFactory.CreateIsolatedSettings();
            settings.OperationModeLR2DB = false;
            settings.EnableBeatorajaBmtOutput = false;
            settings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(root, "Secondary")]);
            var playlist = new TestBmsPlaylist(databaseScope, songDb, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDb), uiScheduler: new TestUiScheduler(() => null), settings: settings);
            var table = new BMSTable
            {
                playlist_id = 8102,
                name = "Bulk save",
                is_bmt_output = false,
                is_external_sync = false,
                is_root_folder = false,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None,
                Header_url = new Uri("https://example.invalid/header"),
                Data_url = new Uri("https://example.invalid/data")
            };
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableHeadersToDB([table]);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, null!,
                settingsProvider: () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
            var draft = new PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel(workspace, [new PlaylistSummaryRow { TableRef = table }]);
            draft.BmtOutputOption = draft.OnOption;
            draft.ExternalSyncOption = draft.OnOption;
            draft.RootFolderOption = draft.OnOption;
            draft.OutputUserFolder = false;
            draft.OutputBaseOption = draft.OutputBaseOptions.Single(option => option.BaseName == "Secondary");
            Func<Task> apply = operation switch
            {
                "bmt" => draft.ApplyBmtOutputAsync,
                "sync" => draft.ApplyExternalSyncAsync,
                "root" => draft.ApplyRootFolderAsync,
                "mask" => draft.ApplyCustomFolderOutputTypesAsync,
                "base" => draft.ApplyOutputBaseAsync,
                _ => throw new AssertFailedException(operation)
            };
            string column = operation switch
            {
                "bmt" => "is_bmt_output",
                "sync" => "is_external_sync",
                "root" => "is_root_folder",
                "mask" => "ignore_folder_output",
                "base" => "custom_folder_output_base_name",
                _ => throw new AssertFailedException(operation)
            };
            string readSql = "SELECT CAST(COALESCE(" + column + ", '') AS TEXT) FROM playlist WHERE playlist_id = ?;";
            string before;
            using (LR2SongDBExtended database = databaseScope.OpenSongWrite(songDb))
            {
                before = database.ExecuteScalar<string>(readSql, table.playlist_id);
                database.Execute("CREATE TRIGGER fail_bulk_save BEFORE INSERT ON playlist BEGIN SELECT RAISE(FAIL, 'forced bulk DB failure'); END;");
            }
            Exception? failure = null;
            try { await apply(); }
            catch (Exception exception) { failure = exception; }
            Assert.IsNotNull(failure);
            Assert.IsFalse(failure is PlaylistMutationPostCommitException);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.IsFalse(table.is_bmt_output);
            Assert.IsFalse(table.is_external_sync);
            Assert.IsFalse(table.is_root_folder);
            Assert.AreEqual(LR2SongDBExtended.playlist.CustomFolderType.None, table.ignore_folder_output);
            Assert.IsNull(table.custom_folder_output_base_name);
            using (LR2SongDBExtended database = databaseScope.OpenSongWrite(songDb))
            {
                Assert.AreEqual(before, database.ExecuteScalar<string>(readSql, table.playlist_id));
                database.Execute("DROP TRIGGER fail_bulk_save;");
            }
            await apply();
            using (LR2SongDBExtended database = databaseScope.OpenSongWrite(songDb))
            {
                Assert.AreNotEqual(before, database.ExecuteScalar<string>(readSql, table.playlist_id), "同じdraftの再反映も、正本判定を通り実保存します。");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>取得URLのない表へ外部同期オンを要求しても変更なし・オフを維持し、取得不能な表を同期対象へ混入させないことを確認します。</summary>
    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_KeepsInvalidUrlTableOffWhenEnabling()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);

        bool changed = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsFalse(changed);
        Assert.IsFalse(table.is_external_sync);
    }

    /// <summary>絶対headerと相対dataの表へ外部同期オンを要求し、受理して変更ありを返すことを確認して相対dataの誤拒否を検出します。</summary>
    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_EnablesValidUrlTable()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        table.Header_url = new Uri("https://example.invalid/header.json", UriKind.Absolute);
        table.Data_url = new Uri("data.json", UriKind.Relative);

        bool changed = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsTrue(changed);
        Assert.IsTrue(table.is_external_sync);
    }

    private static BMSTable CreateTable(
        LR2SongDBExtended.playlist.CustomFolderType ignoreFolderOutput,
        LR2SongDBExtended.playlist.EntryUnitType entryUnitType = LR2SongDBExtended.playlist.EntryUnitType.File)
    {
        return new BMSTable
        {
            ignore_folder_output = ignoreFolderOutput,
            entry_type = entryUnitType
        };
    }

}
