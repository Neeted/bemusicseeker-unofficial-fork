using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using static BeMusicSeeker.Tests.PlaylistWorkspaceTestDataSupport;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

/// <summary>workspaceから保存・削除・browser・lamp遷移・reload cleanupへの実要求と失敗経路を検証します。</summary>
/// <remarks>SQLiteの確定とlockは実資源を使い、lampのVM受信だけ共有dispatcherでbuild終端を観測します。実画面のBinding・modal・resizeは上位WPFケースが分担します。</remarks>
[TestClass]
public sealed class PlaylistWorkspaceActionWorkflowTests
{
    /// <summary>実property prefix保存を全体/選択folder詳細へ配送し、非表示入力・次sort/filter・memo編集の実終端を確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaylistPropertySave_PrefixReceiptUpdatesExistingDetailAndLaterRequests(bool selectedFolder)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        BMSTable table = fixture.Table;
        if (selectedFolder) { fixture.Workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("P1")); }
        else { fixture.Workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("P1", ChartModeFilter.All)); }
        await fixture.Refresh();
        PlaylistDetailSourceRow old = fixture.Workspace.DetailViewState.Source.Rows.Single(row => row.Entry.title == "entry 0");
        BMSTableEntry target = table.entries[0];
        DateTime date = table.last_update;
        Task selectionRefresh = Task.CompletedTask;
        fixture.Workspace.PlaylistDetailReloadRefreshRequested += (_, _) => { selectionRefresh = fixture.Refresh(); };
        using PlaylistPropertyDialogViewModel dialog = await fixture.Workspace.OpenPropertyDialogAsync(table)
            ?? throw new AssertFailedException("Property dialog was not created.");
        dialog.compat_prefix = "Q";
        Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
        await selectionRefresh;
        Assert.AreEqual("P1", old.Folder, "旧捕捉値は書き換えません。");
        Assert.AreSame(target, table.entries[0]); Assert.AreEqual(date, table.last_update);
        Assert.AreEqual(selectedFolder ? "Q1" : null, fixture.Workspace.CapturePlaylistDetailSelection().FolderName);
        Assert.AreEqual(table.PlaylistEntriesRevision, fixture.Workspace.DetailViewState.Source.LastBuiltPlaylistRevision);
        string[] folders = [.. fixture.Workspace.DetailViewState.Source.Rows.Select(row => row.Folder).OrderBy(value => value, StringComparer.Ordinal)];
        CollectionAssert.AreEqual(selectedFolder ? new[] { "Q1" } : new[] { "Q1", "Q2", "QQ3", "QX" }, folders);
        if (!selectedFolder) { Assert.AreEqual(0, fixture.Workspace.DetailViewState.View.Rows.Count, "旧prefix検索から対象が外れます。"); }
        fixture.Workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Folder), System.ComponentModel.ListSortDirection.Descending);
        await fixture.Refresh();
        Assert.AreEqual(selectedFolder ? 1 : 4, fixture.Workspace.DetailViewState.View.Rows.Count);
        PlaylistDetailRow row = fixture.Workspace.DetailViewState.View.Rows.Cast<PlaylistDetailRow>().Single(value => ReferenceEquals(value.Entry, target));
        var context = new MainChartListCellEditContext(row, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistMissing, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(context);
        row.memo = "memo after prefix";
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "memo after prefix", commit: true));
        Assert.AreEqual("memo after prefix", target.memo);
        Assert.AreEqual("Q1", row.Folder);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual("memo after prefix", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=7492 AND title='entry 0';"));
    }

    /// <summary>
    /// 外部参照置換と接頭辞変更を実保存し、参照依存の通知時に既に新参照を採用し、選択・ツリー・実詳細行・DBがNEW 1へ揃うことを確認します。
    /// 受信側単体の非現行参照拒否と、画面のBinding・所有者は既存詳細選択テストとWPF代表へ分担します。
    /// </summary>
    [TestMethod]
    public async Task PlaylistPropertySave_ExternalReplacementRemapsSelectedFolderAndRefreshesDetail()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceActionWorkflowTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistWorkspaceViewModel? workspace = null;
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"Prefix replacement\",\"symbol\":\"P\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.EnablePlaylistUrlCompletion = false);
            var playlist = new TestBmsPlaylist(songPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot(), settings: settings);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            table.playlist_id = 7481;
            table.Page_url = new Uri(Path.Combine(directory, "previous-header.json"));
            // 取得済みの所属も正規prefix候補で初期化し、entry値と索引を同時に揃えます。
            table.ApplyEntryMutation(table.PrepareHeaderMutation(new PlaylistHeaderEdit
            { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = "OLD " }));
            Assert.IsTrue(table.EnableExternalSync());
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Selected\",\"level\":\"1\"}]");
            var library = new TestBmsLibrary(songPath, settings: settings);
            var service = new PlaylistPropertySaveService(() => playlist, () => library, () => null!, () => new CustomFolderOutputSettingsSnapshot());
            workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library, propertySaveService: service);

            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            workspace.PlaylistSyncProgressChanged += (_, _) => { };
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) => { };
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.SetDetailDataSource(new PlaylistDetailDataSource(library, playlist, new MainChartRowProjectionOwner()));
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("OLD 1"));
            PlaylistWorkspaceViewModel currentWorkspace = workspace;
            int folderNotices = 0, entryNotices = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, request) =>
            {
                folderNotices++;
                Assert.AreSame(request.Table, currentWorkspace.CapturePlaylistDetailSelection().Table,
                    "フォルダ通知に先立ち新参照を採用します。");
            };
            service.PlaylistPropertyEntriesChanged += (_, request) =>
            {
                entryNotices++;
                Assert.AreSame(request.Table, currentWorkspace.CapturePlaylistDetailSelection().Table,
                    "項目通知に先立ち新参照を採用します。");
            };

            dialog = await workspace.OpenPropertyDialogAsync(table) ?? throw new AssertFailedException("Property dialog was not created.");
            dialog.Page_url = new Uri(header);
            dialog.compat_prefix = "NEW ";
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            BMSTable confirmed = playlist.BMSTables.Single();
            PlaylistDetailSelection selection = workspace.CapturePlaylistDetailSelection();
            Assert.AreNotSame(table, confirmed);
            Assert.AreSame(confirmed, selection.Table);
            Assert.AreEqual("NEW 1", selection.FolderName);
            Assert.AreEqual(1, folderNotices);
            Assert.AreEqual(1, entryNotices);

            Assert.IsTrue(confirmed.FolderNodes.Any(node => node.FolderName == "NEW 1"));
            string stage = string.Empty;
            PlaylistSourceBuildResult detail = workspace.BuildDetailSourceRows(selection.Table, selection.Scope, selection.FolderName, CancellationToken.None, ref stage);
            Assert.AreEqual("NEW 1", detail.SourceRows.Single().Folder);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
            Assert.AreEqual("NEW 1", db.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == table.playlist_id).folder);
        }
        finally
        {
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>二表をsnapshotへ捕捉して順序と同じ表参照を保ち、別workerのwriter取得が完了して読取lockを残していないことを確認します。</summary>
    [TestMethod]
    public void PlaylistTreeSnapshot_ReleasesReaderLockAfterCapture()
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
            BMSTable first = new() { name = "first" };
            BMSTable second = new() { name = "second" };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            workspace.RefreshPlaylistTreeTables(playlist);

            List<BMSTable> snapshot = workspace.CapturePlaylistTreeTablesSnapshot();

            CollectionAssert.AreEqual(new[] { first, second }, snapshot);
            Task<bool> writerProbe = Task.Run(() =>
            {
                playlist.AcquireWriterLockBMSTables();
                try
                {
                    return true;
                }
                finally
                {
                    playlist.FreeWriterLockBMSTables();
                }
            });
            bool writerCompleted = writerProbe.Wait(TimeSpan.FromSeconds(2));
            try
            {
                Assert.IsTrue(writerCompleted);
                Assert.IsTrue(writerProbe.GetAwaiter().GetResult());
            }
            finally
            {
                if (!writerCompleted)
                {
                    writerProbe.Wait();
                }
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



    /// <summary>通常ローカルfolderだけ改名・削除を許可し、特殊folder・外部同期・未選択を拒否して禁止対象へ編集を露出する誤りを検出します。</summary>
    [TestMethod]
    public void PlaylistFolderContextMenuAvailability_PreservesEditabilityPolicy()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        BMSTable localTable = new();

        PlaylistFolderContextMenuAvailability normal =
            workspace.CapturePlaylistFolderContextMenuAvailability(
                localTable,
                PlaylistFolderNode.CreateFolder("Folder"));
        Assert.IsTrue(normal.CanDelete);
        Assert.IsTrue(normal.CanRename);

        PlaylistFolderContextMenuAvailability special =
            workspace.CapturePlaylistFolderContextMenuAvailability(
                localTable,
                PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
        Assert.IsFalse(special.CanDelete);
        Assert.IsFalse(special.CanRename);

        PlaylistFolderContextMenuAvailability external =
            workspace.CapturePlaylistFolderContextMenuAvailability(
                new BMSTable { is_external_sync = true },
                PlaylistFolderNode.CreateFolder("Folder"));
        Assert.IsFalse(external.CanDelete);
        Assert.IsFalse(external.CanRename);

        PlaylistFolderContextMenuAvailability missing =
            workspace.CapturePlaylistFolderContextMenuAvailability(localTable, null);
        Assert.IsFalse(missing.CanDelete);
        Assert.IsFalse(missing.CanRename);
    }

    /// <summary>store未接続、通常表、外部同期表の操作可否を評価し、属性画面の利用条件と同期中のfolder作成禁止を各操作へ配送します。</summary>
    [TestMethod]
    public void PlaylistTableContextMenuAvailability_PreservesAllActionPolicyFields()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
        PlaylistTableContextMenuAvailability unavailable =
            unavailableWorkspace.CapturePlaylistTableContextMenuAvailability(
                new BMSTable { Page_url = new Uri("https://example.test/table") });
        Assert.IsFalse(unavailable.CanOpenProperty);

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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);

            BMSTable normalTable = new()
            {
                Page_url = new Uri("https://example.test/table")
            };
            PlaylistTableContextMenuAvailability normal =
                workspace.CapturePlaylistTableContextMenuAvailability(normalTable);
            Assert.IsTrue(normal.CanReload);
            Assert.IsTrue(normal.CanOpenPage);
            Assert.IsTrue(normal.CanCreateFolder);
            Assert.IsTrue(normal.CanOverwriteLevel);
            Assert.IsTrue(normal.CanRemoveTable);
            Assert.IsTrue(normal.CanOpenProperty);

            BMSTable externalTable = new()
            {
                Page_url = new Uri("https://example.test/external"),
                is_external_sync = true
            };
            PlaylistTableContextMenuAvailability external =
                workspace.CapturePlaylistTableContextMenuAvailability(externalTable);
            Assert.IsTrue(external.CanReload);
            Assert.IsTrue(external.CanOpenPage);
            Assert.IsFalse(external.CanCreateFolder);
            Assert.IsTrue(external.CanOverwriteLevel);
            Assert.IsTrue(external.CanRemoveTable);
            Assert.IsTrue(external.CanOpenProperty);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>page優先とheader fallbackの外部URIを解決し、内蔵recommended・estimation・未知の専用URIをbrowserへ渡さないことを確認します。</summary>
    [TestMethod]
    public void PlaylistTableExternalLinks_PreserveNormalHeaderAndSpecialRoutes()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        BMSTable pageTable = new() { Page_url = new Uri("https://example.test/table") };
        PlaylistTableContextMenuAvailability pageAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(pageTable);
        Assert.IsTrue(pageAvailability.CanReload);
        Assert.IsTrue(pageAvailability.CanOpenPage);
        Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(pageTable, out Uri pageUri));
        Assert.AreEqual("https://example.test/table", pageUri.ToString());

        BMSTable headerTable = new() { Header_url = new Uri("https://example.test/header.json") };
        PlaylistTableContextMenuAvailability headerAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(headerTable);
        Assert.IsTrue(headerAvailability.CanReload);
        Assert.IsTrue(headerAvailability.CanOpenPage);
        Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(headerTable, out Uri headerUri));
        Assert.AreEqual("https://example.test/header.json", headerUri.ToString());

        BMSTable estimationTable = new() { Page_url = new Uri("bmseeker:table.estimation") };
        Assert.IsFalse(workspace.CapturePlaylistTableContextMenuAvailability(estimationTable).CanOpenPage);
        Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(estimationTable, out _));

        BMSTable recommendedTable = new() { Page_url = new Uri("bmseeker:table.recommended") };
        Assert.IsFalse(workspace.CapturePlaylistTableContextMenuAvailability(recommendedTable).CanOpenPage);
        Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(recommendedTable, out _));

        BMSTable unsupportedTable = new() { Page_url = new Uri("bmseeker:table.other") };
        Assert.IsFalse(workspace.CapturePlaylistTableContextMenuAvailability(unsupportedTable).CanOpenPage);
        Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(unsupportedTable, out _));
    }

    /// <summary>実libraryにLR2利用者IDがあっても内蔵recommendedを外部ページへ解決せず、利用者IDの存在で専用URIを誤許可する分岐を検出します。</summary>
    [TestMethod]
    public void PlaylistTableExternalLinks_DisableBuiltInPageEvenWhenPlayerIdIsConfigured()
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
            BMSLibrary library = CreateLibraryWithLr2Id(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistLibraryProvider: () => library);

            BMSTable recommendedTable = new() { Page_url = new Uri("bmseeker:table.recommended") };
            Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(recommendedTable, out _));

        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>summaryと表の外部URIだけをbrowser境界へ渡し、summary側の起動失敗処理と表側の元例外伝達をそれぞれ確認します。</summary>
    [TestMethod]
    public async Task PlaylistExternalLinkActions_UseWorkspaceBrowserBoundaryAndPreserveFailureContracts()
    {
        var openedUris = new List<Uri>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            browserOpenSink: openedUris.Add);
        Uri summaryUri = new("https://example.test/summary");
        BMSTable table = new() { Page_url = new Uri("https://example.test/table") };

        await workspace.OpenPlaylistSummaryUriAsync(summaryUri);
        Assert.IsTrue(workspace.OpenPlaylistTablePage(table));
        await workspace.OpenPlaylistSummaryUriAsync(new Uri("bmseeker:table.recommended"));
        Assert.IsFalse(workspace.OpenPlaylistTablePage(new BMSTable { Page_url = new Uri("bmseeker:table.estimation?type=easy") }));

        CollectionAssert.AreEqual(
            new[] { summaryUri, table.Page_url },
            openedUris);

        PlaylistWorkspaceViewModel failingWorkspace = CreateDetailWorkspace(
            out _,
            browserOpenSink: _ => throw new InvalidOperationException("launch failed"));
        await failingWorkspace.OpenPlaylistSummaryUriAsync(summaryUri);
        Assert.ThrowsException<InvalidOperationException>(
            () => failingWorkspace.OpenPlaylistTablePage(table));
    }

    /// <summary>同IDの古い表・summary参照を現表へ解決し、削除済IDやIDなしを名前・URIだけで現表へ誤接続しないことを確認します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceResolvesCurrentTableIdentityOutsideTheView()
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
            BMSTable active = new()
            {
                playlist_id = 42,
                name = "Current",
                Page_url = new Uri("https://example.test/table")
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([active])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            BMSTable stale = new()
            {
                playlist_id = 42,
                name = "Old",
                Page_url = active.Page_url
            };

            Assert.AreSame(active, workspace.ResolveActivePlaylistTable(stale));
            Assert.AreSame(
                active,
                workspace.ResolveActivePlaylistSummaryTable(new PlaylistSummaryRow
                {
                    TableRef = stale,
                    Name = active.name
                }));

            BMSTable deletedPersistentTable = new()
            {
                playlist_id = 99,
                name = active.name,
                Page_url = active.Page_url
            };
            Assert.IsNull(workspace.ResolveActivePlaylistTable(deletedPersistentTable));

            BMSTable transientTable = new()
            {
                name = active.name,
                Page_url = active.Page_url
            };
            Assert.IsNull(workspace.ResolveActivePlaylistTable(transientTable));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>clear・rankの全カテゴリから検索aliasを生成し、MAXとPERFECT、ASSISTの別名、未プレイと未所持を含む独立入力の期待行へ絞り込みます。</summary>
    [TestMethod]
    public void PlaylistLampNavigation_UsesChartSearchAliasesForEveryClearAndRankCategory()
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

            const int playlistId = 7405;
            const string folderName = "Normal";
            var entries = new List<BMSTableEntry>();
            var sourceRows = new List<PlaylistDetailSourceRow>();
            int fixtureIndex = 0;

            PlaylistDetailSourceRow AddRow(
                ClearType clear,
                RankType rank,
                string title)
            {
                fixtureIndex++;
                var entry = new TestablePlaylistEntry(
                    fixtureIndex.ToString("x32", CultureInfo.InvariantCulture),
                    title)
                {
                    folder = folderName
                };
                entries.Add(entry);
                PlaylistDetailSourceRow row = new(
                    entry,
                    resolvedChart: null,
                    scoreSnapshot: CreateScore(entry.md5, clear, rank));
                sourceRows.Add(row);
                return row;
            }

            PlaylistDetailSourceRow max = AddRow(ClearType.MAX, RankType.MAX, "max");
            PlaylistDetailSourceRow perfect = AddRow(ClearType.PA, RankType.AA, "perfect");
            PlaylistDetailSourceRow fc = AddRow(ClearType.FC, RankType.A, "fc");
            PlaylistDetailSourceRow exhard = AddRow(ClearType.EX_HARD, RankType.B, "exhard");
            PlaylistDetailSourceRow hard = AddRow(ClearType.HARD, RankType.C, "hard");
            PlaylistDetailSourceRow normal = AddRow(ClearType.CLEAR, RankType.D, "normal");
            PlaylistDetailSourceRow easy = AddRow(ClearType.EASY, RankType.E, "easy");
            PlaylistDetailSourceRow assist = AddRow(ClearType.INVALID, RankType.INVALID, "assist");
            PlaylistDetailSourceRow lassist = AddRow(ClearType.L_ASSIST, RankType.INVALID, "lassist");
            PlaylistDetailSourceRow failed = AddRow(ClearType.FAILED, RankType.F, "failed");
            PlaylistDetailSourceRow noPlay = AddRow(ClearType.NO_PLAY, RankType.INVALID, "no-play");
            PlaylistDetailSourceRow noSong = AddRow(ClearType.NO_SONG, RankType.INVALID, "no-song");
            PlaylistDetailSourceRow rankAaa = AddRow(ClearType.HARD, RankType.AAA, "rank-aaa");
            PlaylistDetailSourceRow rankMax = AddRow(ClearType.HARD, RankType.MAX, "rank-max");
            PlaylistDetailSourceRow rankAa = AddRow(ClearType.HARD, RankType.AA, "rank-aa");
            PlaylistDetailSourceRow rankA = AddRow(ClearType.HARD, RankType.A, "rank-a");
            PlaylistDetailSourceRow rankB = AddRow(ClearType.HARD, RankType.B, "rank-b");
            PlaylistDetailSourceRow rankC = AddRow(ClearType.HARD, RankType.C, "rank-c");
            PlaylistDetailSourceRow rankD = AddRow(ClearType.HARD, RankType.D, "rank-d");
            PlaylistDetailSourceRow rankE = AddRow(ClearType.HARD, RankType.E, "rank-e");
            PlaylistDetailSourceRow rankF = AddRow(ClearType.HARD, RankType.F, "rank-f");
            PlaylistDetailSourceRow playedAssistWithInvalidRank = AddRow(
                ClearType.INVALID,
                RankType.INVALID,
                "played-assist-invalid-rank");

            var table = new BMSTable
            {
                playlist_id = playlistId,
                name = "Lamp category aliases",
                entries = entries,
                Folder_order = [folderName]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            workspace.InitializePlaylistDetailFilter(
                new ChartListFilterSnapshot("previous keyword", ChartModeFilter._7KEYS));
            var publications = new List<PlaylistLampNavigationRequestedEventArgs>();
            workspace.PlaylistLampNavigationRequested += (_, args) => publications.Add(args);

            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.MAX),
                publications, max);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.PERFECT),
                publications, perfect);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.FC),
                publications, fc);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.EXHARD),
                publications, exhard);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.HARD),
                publications, hard, rankAaa, rankMax, rankAa, rankA, rankB, rankC, rankD, rankE, rankF);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.NORMAL),
                publications, normal);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.EASY),
                publications, easy);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.ASSIST),
                publications, assist, lassist, playedAssistWithInvalidRank);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.FAILED),
                publications, failed);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampClearCategory.NP),
                publications, noPlay, noSong);

            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.AAA),
                publications, max, rankAaa, rankMax);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.AA),
                publications, perfect, rankAa);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.A),
                publications, fc, rankA);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.B),
                publications, exhard, rankB);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.C),
                publications, hard, rankC);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.D),
                publications, normal, rankD);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.E),
                publications, easy, rankE);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.F),
                publications, failed, assist, lassist, rankF, playedAssistWithInvalidRank);
            AssertLampNavigationRows(
                workspace, table, sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(CultureInfo.InvariantCulture), folderName, PlaylistLampRankCategory.NP),
                publications, noPlay, noSong);
            Assert.AreEqual(19, publications.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>未所持でも存在するscore、NO_PLAY、NO_SONGを明示入力し、前のkeywordを置換してAAA・clear NP・rank NPごとの期待行を公開します。</summary>
    [TestMethod]
    public void PlaylistLampNavigation_ReplacesKeywordAndPublishesFinalRowsForUnownedScoreAndNoPlay()
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

            const int playlistId = 7401;
            const string folderName = "Normal";
            var maxEntry = new TestablePlaylistEntry(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "unowned max")
            {
                folder = folderName
            };
            var noPlayEntry = new TestablePlaylistEntry(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "no play")
            {
                folder = folderName
            };
            var noSongEntry = new TestablePlaylistEntry(
                "cccccccccccccccccccccccccccccccc",
                "no song")
            {
                folder = folderName
            };
            var table = new BMSTable
            {
                playlist_id = playlistId,
                name = "Lamp navigation",
                entries = [maxEntry, noPlayEntry, noSongEntry],
                Folder_order = [folderName]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            workspace.InitializePlaylistDetailFilter(
                new ChartListFilterSnapshot("previous keyword", ChartModeFilter._7KEYS));

            BMSScore maxScore = CreateScore(maxEntry.md5, ClearType.CLEAR, RankType.MAX, rate: 100);
            BMSScore noPlayScore = CreateScore(noPlayEntry.md5, ClearType.NO_PLAY, RankType.INVALID);
            BMSScore noSongScore = CreateScore(noSongEntry.md5, ClearType.NO_SONG, RankType.INVALID);
            PlaylistDetailSourceRow maxRow = new(maxEntry, resolvedChart: null, scoreSnapshot: maxScore);
            PlaylistDetailSourceRow noPlayRow = new(noPlayEntry, resolvedChart: null, scoreSnapshot: noPlayScore);
            PlaylistDetailSourceRow noSongRow = new(noSongEntry, resolvedChart: null, scoreSnapshot: noSongScore);
            IReadOnlyList<PlaylistDetailSourceRow> sourceRows = [maxRow, noPlayRow, noSongRow];
            var publications = new List<PlaylistLampNavigationRequestedEventArgs>();
            workspace.PlaylistLampNavigationRequested += (_, args) => publications.Add(args);

            AssertLampNavigationRows(
                workspace,
                table,
                sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(),
                    folderName,
                    PlaylistLampRankCategory.AAA),
                publications,
                maxRow);
            AssertLampNavigationRows(
                workspace,
                table,
                sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(),
                    folderName,
                    PlaylistLampClearCategory.NP),
                publications,
                noPlayRow,
                noSongRow);
            AssertLampNavigationRows(
                workspace,
                table,
                sourceRows,
                PlaylistLampSegmentInvocationRequest.ForRank(
                    playlistId.ToString(),
                    folderName,
                    PlaylistLampRankCategory.NP),
                publications,
                noPlayRow,
                noSongRow);
            Assert.AreEqual(3, publications.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>空文字名の通常folderをlamp遷移先として受理し、全体scopeのnullと混同せず空folderに一致する行を公開します。</summary>
    [TestMethod]
    public void PlaylistLampNavigation_AcceptsEmptyNormalFolderAndPublishesFinalRows()
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

            const int playlistId = 7402;
            const string folderName = "";
            var entry = new TestablePlaylistEntry(
                "dddddddddddddddddddddddddddddddd",
                "empty folder chart")
            {
                folder = folderName
            };
            var table = new BMSTable
            {
                playlist_id = playlistId,
                name = "Empty folder navigation",
                entries = [entry],
                Folder_order = [folderName]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            workspace.InitializePlaylistDetailFilter(
                new ChartListFilterSnapshot("previous keyword", ChartModeFilter._7KEYS));

            BMSScore noPlayScore = CreateScore(entry.md5, ClearType.NO_PLAY, RankType.INVALID);
            PlaylistDetailSourceRow noPlayRow = new(entry, resolvedChart: null, scoreSnapshot: noPlayScore);
            IReadOnlyList<PlaylistDetailSourceRow> sourceRows = [noPlayRow];
            var publications = new List<PlaylistLampNavigationRequestedEventArgs>();
            workspace.PlaylistLampNavigationRequested += (_, args) => publications.Add(args);

            AssertLampNavigationRows(
                workspace,
                table,
                sourceRows,
                PlaylistLampSegmentInvocationRequest.ForClear(
                    playlistId.ToString(),
                    folderName,
                    PlaylistLampClearCategory.NP),
                publications,
                noPlayRow);
            Assert.AreEqual(string.Empty, publications.Single().Selection.FolderName);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実VMのlamp要求購読から空名folderの詳細build終端まで待ち、keyword・選択・確定行が同じ条件へ揃うことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistLampNavigation_EmptyFolderRunsMainWindowConsumerAndCommitsFinalRows()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                nameof(PlaylistWorkspaceActionWorkflowTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            MainWindowViewModel? viewModel = null;
            try
            {
                string songDbPath = Path.Combine(tempDirectory, "song.db");
                StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
                PlaylistPersistenceRepository.EnsureSchema(songDbPath);

                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = tempDirectory;
                values.StandaloneBmsRootPaths = tempDirectory;
                values.BMSInstallDir = tempDirectory;
                values.SkipInitPlaylistLoad = true;
                values.ScanBmsFilesOnStartup = false;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseExternalPanelImage = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
            });
                viewModel = MainWindowViewModelTestFactory.Create(settings);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);

                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, settings, viewModel);
                library.BmsCharts = [];
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, library: library);
                var noPlayEntry = new TestablePlaylistEntry(
                    "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    "empty-folder no-play")
                {
                    folder = string.Empty
                };
                noPlayEntry.SetSha256(new string('e', 64));
                var scoredEntry = new TestablePlaylistEntry(
                    "ffffffffffffffffffffffffffffffff",
                    "empty-folder scored")
                {
                    folder = string.Empty
                };
                scoredEntry.SetSha256(new string('f', 64));
                const int playlistId = 7403;
                var table = new BMSTable
                {
                    playlist_id = playlistId,
                    name = "Empty normal folder consumer",
                    entries = [noPlayEntry, scoredEntry],
                    Folder_order = [string.Empty]
                };
                playlist.BMSTables = new ObservableCollection<BMSTable> { table };

                var scored = new BMSScore
                {
                    hash = scoredEntry.md5,
                    clear = ClearType.HARD,
                    rank = RankType.A,
                    perfect = 80,
                    great = 20,
                    totalnotes = 100,
                    rate = 80
                };
                var detailDataSource = new FakePlaylistDetailDataSource
                {
                    ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.Empty,
                    ScoreSnapshot = new BMSLibrary.ScoreSnapshot
                    {
                        ActiveScoreSource = ActiveScoreSource.Beatoraja,
                        LoadStatus = ScoreTableLoadStatus.Loaded,
                        Version = 1,
                        SourceGeneration = 1,
                        ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
                        {
                            [scoredEntry.sha256] = scored
                        }
                    }
                };

                var profile = new LibraryProfile(
                    operationModeLR2DB: false,
                    songDbPath,
                    [tempDirectory],
                    lr2ConfigProvider: null,
                    lr2ScoreDbPath: null,
                    canWriteLr2Config: false,
                    canOutputLr2Folders: false,
                    canUseLr2Backup: false,
                    canUseLr2IrScore: false);
                var applicationPort =
                    (IStartupLibraryApplicationPort)viewModel!;
                applicationPort.AttachStartupLibrary(library);
                applicationPort.AttachStartupServices(
                    new StartupLibraryServices(profile, library, playlist));
                viewModel.PlaylistWorkspace.SetDetailDataSource(detailDataSource);

                ChartListFilterSnapshot previousFilter =
                    new("previous keyword", ChartModeFilter.All);
                viewModel.ChartFilters.ApplySnapshotSilently(previousFilter);
                viewModel.PlaylistWorkspace.InitializePlaylistDetailFilter(previousFilter);
                int rowsChanged = 0;
                viewModel.MainChartList.PropertyChanged += (_, args) =>
                {
                    if (string.Equals(args.PropertyName, nameof(viewModel.MainChartList.Rows), StringComparison.Ordinal))
                    {
                        rowsChanged++;
                    }
                };

                bool accepted = viewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(
                    PlaylistLampSegmentInvocationRequest.ForClear(
                        playlistId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        string.Empty,
                        PlaylistLampClearCategory.NP));
                Assert.IsTrue(accepted);
                int requestVersion = viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion;
                Task completion = viewModel.PlaylistWorkspace.WaitForDetailRequestCompletionAsync(requestVersion);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    completion,
                    "playlist-lamp.empty-folder.consumer-detail-build");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreNotEqual(previousFilter.KeywordFilter, viewModel.ChartFilters.KeywordFilter);
                ChartListFilterSnapshot appliedFilter =
                    viewModel.PlaylistWorkspace.CapturePlaylistDetailFilterSnapshot();
                Assert.AreNotEqual(previousFilter.KeywordFilter, appliedFilter.KeywordFilter);
                Assert.AreEqual(appliedFilter.KeywordFilter, viewModel.ChartFilters.KeywordFilter);
                PlaylistDetailSelection selection = viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection();
                Assert.IsNotNull(selection);
                Assert.AreSame(table, selection.Table);
                Assert.AreEqual(string.Empty, selection.FolderName);

                var finalRows = viewModel.MainChartList.Rows
                    .Cast<PlaylistDetailRow>()
                    .ToList();
                Assert.AreEqual(1, finalRows.Count);
                Assert.AreSame(noPlayEntry, finalRows[0].Entry);
                Assert.IsTrue(rowsChanged > 0);
                Assert.IsTrue(viewModel.MainChartList.LastCompletion.RequestId > 0L);
                Assert.AreEqual(
                    MainViewUpdateMode.PlaylistFilterSelected,
                    viewModel.MainChartList.LastCompletion.Mode);
                Assert.AreSame(
                    table,
                    viewModel.PlaylistWorkspace.DetailViewState.Source.CurrentTable);
            }
            finally
            {
                if (viewModel != null)
                {
                    viewModel.PlaylistWorkspace.CancelDetailBuilds();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.PlaylistWorkspace.WaitForDetailBuildIdleAsync(),
                        "playlist-lamp.empty-folder.consumer-cleanup");
                    viewModel.PlaylistWorkspace.Dispose();
                    viewModel.SettingDialog.Dispose();
                }
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        });
    }

    /// <summary>実VMの全体lamp要求から詳細buildを完了し、二つの通常folderの該当行だけを採用して特殊folderと不一致scoreを除外します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistLampNavigation_OverallRunsMainWindowConsumerAcrossNormalFoldersAndExcludesSpecialFolder()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                nameof(PlaylistWorkspaceActionWorkflowTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            MainWindowViewModel? viewModel = null;
            try
            {
                string songDbPath = Path.Combine(tempDirectory, "song.db");
                StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
                PlaylistPersistenceRepository.EnsureSchema(songDbPath);

                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = tempDirectory;
                values.StandaloneBmsRootPaths = tempDirectory;
                values.BMSInstallDir = tempDirectory;
                values.SkipInitPlaylistLoad = true;
                values.ScanBmsFilesOnStartup = false;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseExternalPanelImage = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
            });
                viewModel = MainWindowViewModelTestFactory.Create(settings);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);

                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, settings, viewModel);
                library.BmsCharts = [];
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings, library: library);
                var firstFolderNoPlay = new TestablePlaylistEntry(
                    "11111111111111111111111111111111",
                    "first folder no play")
                {
                    folder = "First"
                };
                firstFolderNoPlay.SetSha256(new string('1', 64));
                var secondFolderNoSong = new TestablePlaylistEntry(
                    "22222222222222222222222222222222",
                    "second folder no song")
                {
                    folder = "Second"
                };
                secondFolderNoSong.SetSha256(new string('2', 64));
                var normalNonMatch = new TestablePlaylistEntry(
                    "33333333333333333333333333333333",
                    "normal folder hard")
                {
                    folder = "First"
                };
                normalNonMatch.SetSha256(new string('3', 64));
                var specialFolderNoSong = new TestablePlaylistEntry(
                    "44444444444444444444444444444444",
                    "special no song control")
                {
                    folder = "[NO SONG]"
                };
                specialFolderNoSong.SetSha256(new string('4', 64));
                const int playlistId = 7404;
                var table = new BMSTable
                {
                    playlist_id = playlistId,
                    name = "Overall lamp navigation",
                    entries = [firstFolderNoPlay, secondFolderNoSong, normalNonMatch, specialFolderNoSong],
                    Folder_order = ["First", "Second"]
                };
                playlist.BMSTables = new ObservableCollection<BMSTable> { table };

                var detailDataSource = new FakePlaylistDetailDataSource
                {
                    ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.Empty,
                    ScoreSnapshot = new BMSLibrary.ScoreSnapshot
                    {
                        ActiveScoreSource = ActiveScoreSource.Beatoraja,
                        LoadStatus = ScoreTableLoadStatus.Loaded,
                        Version = 1,
                        SourceGeneration = 1,
                        ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
                        {
                            [firstFolderNoPlay.sha256] = CreateScore(
                                firstFolderNoPlay.md5,
                                ClearType.NO_PLAY,
                                RankType.INVALID),
                            [secondFolderNoSong.sha256] = CreateScore(
                                secondFolderNoSong.md5,
                                ClearType.NO_SONG,
                                RankType.INVALID),
                            [normalNonMatch.sha256] = CreateScore(
                                normalNonMatch.md5,
                                ClearType.HARD,
                                RankType.A),
                            [specialFolderNoSong.sha256] = CreateScore(
                                specialFolderNoSong.md5,
                                ClearType.NO_SONG,
                                RankType.INVALID)
                        }
                    }
                };

                var profile = new LibraryProfile(
                    operationModeLR2DB: false,
                    songDbPath,
                    [tempDirectory],
                    lr2ConfigProvider: null,
                    lr2ScoreDbPath: null,
                    canWriteLr2Config: false,
                    canOutputLr2Folders: false,
                    canUseLr2Backup: false,
                    canUseLr2IrScore: false);
                var applicationPort =
                    (IStartupLibraryApplicationPort)viewModel!;
                applicationPort.AttachStartupLibrary(library);
                applicationPort.AttachStartupServices(
                    new StartupLibraryServices(profile, library, playlist));
                viewModel.PlaylistWorkspace.SetDetailDataSource(detailDataSource);

                ChartListFilterSnapshot previousFilter =
                    new("stale keyword", ChartModeFilter.All);
                viewModel.ChartFilters.ApplySnapshotSilently(previousFilter);
                viewModel.PlaylistWorkspace.InitializePlaylistDetailFilter(previousFilter);

                List<PlaylistLampNavigationRequestedEventArgs> publications = [];
                viewModel.PlaylistWorkspace.PlaylistLampNavigationRequested +=
                    (_, args) => publications.Add(args);
                var request =
                    PlaylistLampViewerNavigationRequest.ForOverall(
                        playlistId.ToString(CultureInfo.InvariantCulture),
                        PlaylistLampSegmentKind.Clear,
                        PlaylistLampClearCategory.NP,
                        rankCategory: null);

                Assert.IsTrue(viewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(request));
                Assert.AreEqual(1, publications.Count);
                Assert.AreSame(table, publications[0].Selection.Table);
                Assert.IsNull(publications[0].Selection.FolderName);
                Assert.AreEqual(PlaylistLampViewerNavigationScope.Overall, publications[0].Request.Scope);

                int requestVersion = viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.PlaylistWorkspace.WaitForDetailRequestCompletionAsync(requestVersion),
                    "playlist-lamp.overall.consumer-detail-build");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreNotEqual(previousFilter.KeywordFilter, viewModel.ChartFilters.KeywordFilter);
                Assert.AreEqual(
                    viewModel.ChartFilters.KeywordFilter,
                    viewModel.PlaylistWorkspace.CapturePlaylistDetailFilterSnapshot().KeywordFilter);
                PlaylistDetailSelection selection =
                    viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection();
                Assert.IsNotNull(selection);
                Assert.AreSame(table, selection.Table);
                Assert.IsNull(selection.FolderName);

                var finalRows = viewModel.MainChartList.Rows
                    .Cast<PlaylistDetailRow>()
                    .ToList();
                CollectionAssert.AreEquivalent(
                    new[] { firstFolderNoPlay, secondFolderNoSong },
                    finalRows.Select(row => row.Entry).ToArray());
                Assert.IsFalse(finalRows.Any(row => ReferenceEquals(row.Entry, normalNonMatch)));
                Assert.IsFalse(finalRows.Any(row => ReferenceEquals(row.Entry, specialFolderNoSong)));
                Assert.AreEqual(
                    MainViewUpdateMode.PlaylistFilterSelected,
                    viewModel.MainChartList.LastCompletion.Mode);
            }
            finally
            {
                if (viewModel != null)
                {
                    viewModel.PlaylistWorkspace.CancelDetailBuilds();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.PlaylistWorkspace.WaitForDetailBuildIdleAsync(),
                        "playlist-lamp.overall.consumer-cleanup");
                    viewModel.PlaylistWorkspace.Dispose();
                    viewModel.SettingDialog.Dispose();
                }
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        });
    }

    private static void AssertLampNavigationRows(
        PlaylistWorkspaceViewModel workspace,
        BMSTable table,
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        PlaylistLampSegmentInvocationRequest request,
        List<PlaylistLampNavigationRequestedEventArgs> publications,
        params PlaylistDetailSourceRow[] expectedRows)
    {
        int publicationCountBefore = publications.Count;
        Assert.IsTrue(workspace.TryRequestPlaylistLampNavigation(request));
        Assert.AreEqual(publicationCountBefore + 1, publications.Count);
        PlaylistLampNavigationRequestedEventArgs publication = publications[^1];
        Assert.AreSame(table, publication.Selection.Table);
        Assert.AreEqual(request.FolderName, publication.Selection.FolderName);
        Assert.AreEqual(ChartModeFilter._7KEYS, publication.FilterSnapshot.ModeFilter);
        Assert.AreNotEqual("previous keyword", publication.FilterSnapshot.KeywordFilter);

        List<PlaylistDetailSourceRow> filteredRows = PlaylistDetailPresentationService.ApplySourceRows(
            sourceRows,
            publication.FilterSnapshot.KeywordFilter,
            publication.FilterSnapshot.ModeFilter,
            sortParameters: null,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);
        CollectionAssert.AreEquivalent(expectedRows, filteredRows);
    }

    private static BMSScore CreateScore(
        string hash,
        ClearType clear,
        RankType rank,
        int rate = 80)
    {
        return new BMSScore
        {
            hash = hash,
            clear = clear,
            rank = rank,
            perfect = 80,
            great = 20,
            totalnotes = 100,
            rate = rate
        };
    }

    /// <summary>実表writerをworkerで保有したまま保存を開始し、呼出しが未完了Taskを返してwriter解放後に完了することを確認します。</summary>
    [TestMethod]
    public async Task PlaylistPropertySave_WaitsOffUiThreadBehindAWriter()
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
            BMSTable table = new() { name = "Edit target" };
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(
                songDbPath, MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            }));
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot());
            PlaylistPropertyEditSession session =
                await service.CreateEditSessionAsync(table);
            Assert.IsNotNull(session);
            var writerAcquired = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseWriter = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                playlist.AcquireWriterLockBMSTables();
                try
                {
                    writerAcquired.SetResult(true);
                    releaseWriter.Wait();
                }
                finally
                {
                    playlist.FreeWriterLockBMSTables();
                }
            });
            Task<PlaylistPropertySaveCommit>? saveTask = null;
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(writerAcquired.Task, writer, "playlist-property.writer");
                Assert.IsTrue(playlist.IsWriteLockHeldAnyBMSTable);
                saveTask = service.TrySaveAsync(session, session.Values);
                Assert.IsFalse(saveTask.IsCompleted);
                releaseWriter.Set();
                Assert.IsNotNull(await saveTask);
            }
            finally
            {
                releaseWriter.Set();
                try
                {
                    await writer;
                    if (saveTask != null) { await saveTask; }
                }
                finally { session.Dispose(); }
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

    /// <summary>他者属性更新、同ID正本置換、属性不変の本番項目追加を保存前に検出し、古い画面値で上書きしません。</summary>
    [DataTestMethod]
    // propertiesは属性競合、referenceは同ID参照置換、entriesは属性不変の行変更です。
    [DataRow("properties")]
    [DataRow("reference")]
    [DataRow("entries")]
    public async Task PlaylistPropertySave_RejectsAStaleDetachedSessionWithoutOverwritingConcurrentChanges(string change)
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
                playlist_id = 7601,
                name = "Initial name",
                symbol = "OLD",
                entries = [new BMSTableEntry { md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", folder = "A" }]
            };
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(
                songDbPath, MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            }));
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot());
            using PlaylistPropertyEditSession session =
                await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            session.Values.Name = "Dialog edit";
            if (change == "properties") { table.symbol = "CONCURRENT"; }
            else if (change == "reference")
            {
                playlist.BMSTables[0] = new BMSTable { playlist_id = table.playlist_id, name = table.name, symbol = table.symbol, entries = table.entries };
            }
            else
            {
                using (table.ReaderWriterLock.GetWriterGuard())
                {
                    table.AddBMSTableEntriesToFolder([new BMSTableEntry { md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", title = "Added" }], "A");
                }
                Assert.AreEqual(session.OriginalValues.Name, table.name);
                Assert.AreEqual(session.OriginalValues.Symbol, table.symbol);
                Assert.AreNotEqual(session.OriginalValues.EntriesRevision, table.PlaylistEntriesRevision);
            }

            InvalidOperationException conflict = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await service.TrySaveAsync(session, session.Values));

            StringAssert.Contains(conflict.Message, "changed after editing began");
            Assert.AreEqual("Initial name", table.name);
            Assert.AreEqual(change == "properties" ? "CONCURRENT" : "OLD", table.symbol);
            if (change == "entries") { Assert.AreEqual(2, table.entries.Count); }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>property保存をDB前の実拒否とDB後の通知故障で分け、前者は全旧状態、後者は同entry/header/版/詳細を確定値で保ちます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaylistPropertySave_FailuresRespectDatabaseCommitBoundary(bool afterCommit)
    {
        await using var fixture = new PlaylistPrefixCommitTestFixture();
        await fixture.Refresh();
        BMSTable table = fixture.Table;
        BMSTableEntry target = table.entries[0];
        int version = table.PlaylistEntriesRevision;
        DateTime date = table.last_update;
        int nameNotices = 0;
        table.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != "name") { return; }
            nameNotices++;
            using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
            Assert.AreEqual("Changed header", db.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id=7492;"));
            Assert.AreEqual("Q1", db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=7492 AND title='entry 0';"));
            if (afterCommit) { throw new InvalidOperationException("post-commit header notification failed"); }
        };
        if (!afterCommit)
        { fixture.Execute("CREATE TRIGGER fail_property_header BEFORE INSERT ON playlist BEGIN SELECT RAISE(ABORT, 'property header DB failed'); END;"); }
        using PlaylistPropertyEditSession session = await fixture.Service.CreateEditSessionAsync(table)
            ?? throw new AssertFailedException("Property session was not created.");
        session.Values.Name = "Changed header"; session.Values.Symbol = "NEW"; session.Values.CompatPrefix = "Q";
        PlaylistPropertySaveCommit commit = await fixture.Service.TrySaveAsync(session, session.Values)
            ?? throw new AssertFailedException("Valid property save was not accepted.");
        Assert.IsNotNull(commit.Exception);
        Assert.AreEqual(afterCommit, commit.RequestedValuesSaved);
        Assert.AreEqual(afterCommit ? 1 : 0, nameNotices);
        Assert.AreEqual(afterCommit ? "Changed header" : "Prefix", table.name);
        Assert.AreEqual(afterCommit ? "NEW" : "PF", table.symbol);
        Assert.AreEqual(afterCommit ? "Q" : "P", table.compat_prefix);
        Assert.AreEqual(afterCommit ? "Q1" : "P1", target.folder);
        Assert.AreSame(target, table.entries[0]);
        Assert.AreEqual(version + (afterCommit ? 1 : 0), table.PlaylistEntriesRevision);
        Assert.AreEqual(date, table.last_update);
        CollectionAssert.AreEqual(afterCommit ? new[] { "QQ3", "QX", "Q2", "Q1" } : new[] { "Q3", "X", "P2", "P1" }, table.Folder_order);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(afterCommit ? "Q" : "P", verify.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id=7492;"));
        Assert.AreEqual(afterCommit ? "Q1" : "P1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=7492 AND title='entry 0';"));
        if (afterCommit)
        {
            Assert.IsInstanceOfType<InvalidOperationException>(commit.Exception);
            Assert.AreEqual("post-commit header notification failed", commit.Exception.Message);
            Assert.AreEqual(table.PlaylistEntriesRevision, fixture.Workspace.DetailViewState.Source.LastBuiltPlaylistRevision);
        }
    }
    /// <summary>prefix保存後の公開を失敗させても実DBと行名の確定を保持し、同値の次保存で二重prefixや公開再試行を起こさず、無効属性・通知不成立・取消も区別します。</summary>
    [TestMethod]
    public async Task CompleteSummaryPropertyEdit_FreshSameValueSaveDoesNotReplayPrefixFollowUp()
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
            BMSTableEntry entry = new TestablePlaylistEntry(
                "abababababababababababababababab",
                "Alpha")
            {
                playlist_id = 7301,
                folder = "Alpha"
            };
            BMSTableEntry prefixedEntry = new TestablePlaylistEntry(
                "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
                "Prefixed Alpha")
            {
                playlist_id = 7301,
                folder = "★Alpha"
            };
            BMSTable table = new()
            {
                playlist_id = 7301,
                name = "Inline prefix retry",
                symbol = "IPR",
                compat_prefix = string.Empty,
                entries = [entry, prefixedEntry],
                Folder_order = ["Alpha", "★Alpha"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                seed.InsertOrReplace(prefixedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(
                songDbPath, MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            }));
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            var library = new TestBmsLibrary(songDbPath);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                propertySaveService: service,
                playlistWorkspaceDialogService: dialogs);

            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            int prefixPresentationCount = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) =>
            {
                if (Interlocked.Increment(ref prefixPresentationCount) == 1)
                {
                    throw new InvalidOperationException("test inline prefix presentation failure");
                }
            };
            PlaylistSummaryRow row = new() { TableRef = table };

            PlaylistSummaryPropertyEditCompletion failedCompletion =
                await workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    nameof(PlaylistSummaryRow.CompatPrefix),
                    "★",
                    commit: true);

            Assert.IsTrue(failedCompletion.RefreshRequired);
            Assert.IsFalse(failedCompletion.IsApplied);
            StringAssert.Contains(
                dialogs.LastMessageRequest.MessageBoxText,
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected);
            CollectionAssert.AreEqual(
                new[] { "★Alpha", "★★Alpha" },
                table.entries.Select(candidate => candidate.folder).ToArray());
            Assert.IsTrue((await workspace.CompleteSummaryPropertyEditAsync(
                row,
                nameof(PlaylistSummaryRow.CompatPrefix),
                "★",
                commit: true)).IsApplied);
            Assert.AreEqual(1, prefixPresentationCount);
            CollectionAssert.AreEqual(
                new[] { "★Alpha", "★★Alpha" },
                table.entries.Select(candidate => candidate.folder).ToArray());
            PlaylistSummaryPropertyEditCompletion invalidCompletion =
                await workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    "Unsupported",
                    "value",
                    commit: true);
            Assert.IsTrue(invalidCompletion.RefreshRequired);
            Assert.IsFalse(invalidCompletion.IsApplied);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_invalid_setting,
                dialogs.LastMessageRequest.MessageBoxText);

            dialogs.MessageResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    "Unsupported",
                    "value",
                    commit: true));

            dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            PlaylistSummaryPropertyEditCompletion noOpCompletion =
                await workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    nameof(PlaylistSummaryRow.Name),
                    "ignored",
                    commit: false);
            Assert.IsFalse(noOpCompletion.IsApplied);
            Assert.IsFalse(noOpCompletion.RefreshRequired);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            CollectionAssert.AreEqual(
                new[] { "★Alpha", "★★Alpha" },
                new[]
                {
                    verify.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        table.playlist_id,
                        entry.md5),
                    verify.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        table.playlist_id,
                        prefixedEntry.md5)
                });
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実draft作成入口から初回登録を確定し、実BMT失敗後の同じ画面の再保存でもIDと表・項目件数を増やしません。</summary>
    [TestMethod]
    public async Task CreatePlaylistPropertyDialog_OutputFailureResavesRegisteredDraftWithoutDuplicateId()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceActionWorkflowTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlaylistPropertyDialogViewModel? dialog = null;
        FileStream? blocker = null;
        try
        {
            string songPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string bmt = Path.Combine(directory, "bmt");
            Directory.CreateDirectory(bmt);
            string manifest = Path.Combine(bmt, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            blocker = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = bmt },
                () => new CustomFolderOutputSettingsSnapshot(),
                BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty));
            var library = new TestBmsLibrary(songPath, settings: MainWindowViewModelTestFactory.CreateIsolatedSettings());
            PlaylistWorkspaceViewModel workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(playlist, library);
            dialog = await workspace.CreatePlaylistPropertyDialogAsync()
                ?? throw new AssertFailedException("Draft dialog was not created.");
            Assert.AreEqual(0, playlist.BMSTables.Count);
            dialog.name = "Registered draft";
            dialog.output_dir = "RegisteredDraft";
            using (var db = new LR2SongDBExtended(songPath))
            {
                db.Execute("CREATE TRIGGER fail_draft_insert BEFORE INSERT ON playlist BEGIN SELECT RAISE(FAIL, 'forced draft save failure'); END;");
            }
            await Assert.ThrowsExceptionAsync<SQLite.SQLiteException>(async () => await dialog.SaveAndApplyAsync());
            Assert.AreEqual(0, playlist.BMSTables.Count, "値適用だけで登録済みへ進めません。");
            Assert.AreEqual("Registered draft", dialog.name, "DB未確定の失敗でも入力を保持します。");
            using (var db = new LR2SongDBExtended(songPath))
            {
                Assert.AreEqual(0, db.Table<BMSTable>().Count());
                db.Execute("DROP TRIGGER fail_draft_insert;");
            }
            await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(async () => await dialog.SaveAndApplyAsync());
            BMSTable registered = playlist.BMSTables.Single();
            Assert.IsTrue(registered.playlist_id.HasValue);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<BMSTable>().Count());
                Assert.AreEqual(0, db.Table<LR2SongDBExtended.playlist_entry>().Count());
                Assert.AreEqual(registered.playlist_id, db.Table<BMSTable>().Single().playlist_id);
            }
            blocker.Dispose();
            blocker = null;
            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await dialog.SaveAndApplyAsync());
            Assert.AreSame(registered, playlist.BMSTables.Single());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.Table<BMSTable>().Count());
            Assert.AreEqual(0, verify.Table<LR2SongDBExtended.playlist_entry>().Count());
            Assert.AreEqual(registered.playlist_id, verify.Table<BMSTable>().Single().playlist_id);
            Assert.IsTrue(File.Exists(manifest));
        }
        finally
        {
            blocker?.Dispose();
            dialog?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>draft作成の設定捕捉を止め、同時作成を未開始として拒否し、先行作成とresetが実DB・正本集合へ未保存表を登録しないことを確認します。</summary>
    [TestMethod]
    public async Task CreatePlaylistPropertyDialog_RejectsConcurrentOpenWithoutCanonicalDraftRegistration()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        Task<PlaylistPropertyDialogViewModel>? firstOpen = null;
        var firstSettingsCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstCreation = new ManualResetEventSlim();
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var visibleTables = new ObservableCollection<BMSTable>();
            TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(
                songDbPath, MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            }));
            playlist.BMSTables = visibleTables;
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () =>
                {
                    firstSettingsCaptured.TrySetResult();
                    releaseFirstCreation.Wait();
                    return new CustomFolderOutputSettingsSnapshot();
                });
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                propertySaveService: service);
            firstOpen = workspace.CreatePlaylistPropertyDialogAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(firstSettingsCaptured.Task, firstOpen, "playlist-property.creation-settings");
            Assert.AreEqual(0, visibleTables.Count, "新規draftの準備は正本に登録しません。");
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            { Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM playlist")); }
            PlaylistPropertyDialogViewModel secondDialog =
                await workspace.CreatePlaylistPropertyDialogAsync();
            Assert.IsNull(secondDialog);
            releaseFirstCreation.Set();
            dialog = await firstOpen;

            Assert.IsNotNull(dialog);
            Assert.AreEqual(0, visibleTables.Count);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            Assert.AreEqual(0, visibleTables.Count);
        }
        finally
        {
            releaseFirstCreation.Set();
            if (firstOpen != null) { dialog ??= await firstOpen; }
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }


    /// <summary>BMSとBMSONの実所属行を登録し、導入パッケージの参照表示を同じ登録済み索引から解決します。</summary>
    [TestMethod]
    public void InstalledPackageReferenceLookup_ResolvesBmsAndBmsonFromRegisteredTable()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSLibrary library = new TestBmsLibrary(songDbPath);
            const string bmsMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string bmsonSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ChartFile bmsFile = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(bmsMd5, @"C:\Installed\bms-chart.bms")));
            ChartFile bmsonIdentity = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(CreateSongTableRow(null, @"C:\Installed\bmson-chart.bmson")));
            BMSTable table = new() { name = "Installed references", symbol = "P" };
            var bmsTableEntry = new BMSTableEntry(bmsFile);
            var bmsonTableEntry = new BMSTableEntry(bmsonIdentity);
            bmsonTableEntry.MarkAsBmsonPlaylistIdentity(bmsonSha256);
            table.entries = [bmsTableEntry, bmsonTableEntry];
            playlist.BMSTables.Add(table);

            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"C:\Installed\bmson-chart.bmson",
                Sha256 = bmsonSha256,
                RawTitle = "Bmson chart",
                RawArtist = "Artist"
            };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    bmsFile,
                    @"C:\Installed",
                    "Bms chart",
                    "Artist"),
                PackageChartEntry.FromChart((bmsonSong)));
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspace.RefreshPlaylistTreeTables(playlist);
            library.SynchronizeReferenceBMSTables([table]);



            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(package.ChartEntries[0].Chart).Symbols);
            Assert.AreEqual("Installed references", library.GetPlaylistReferenceDisplay(package.ChartEntries[0].Chart).Names);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(package.ChartEntries[1].Chart).Symbols);
            Assert.AreEqual("Installed references", library.GetPlaylistReferenceDisplay(package.ChartEntries[1].Chart).Names);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }



    /// <summary>重複した表の同期オン要求で破壊的変更の確認を行い、取消なら同期状態を変えないことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSummaryCellActionRequiresExternalSyncConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable { is_external_sync = false };

        await workspace.ApplyPlaylistSummaryCellActionAsync(
            [
                new PlaylistSummaryRow { TableRef = table },
                new PlaylistSummaryRow { TableRef = table }
            ],
            "IsExternalSync",
            value: true);

        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges,
            dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.IsFalse(table.is_external_sync);
    }

    /// <summary>外部同期オンの表へオフを要求し、解除用確認の承諾後だけ表の同期状態を変更します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSummaryCellActionAcceptsDisableConfirmationAndMutatesTable()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var table = new BMSTable
            {
                name = "Summary table",
                Output_dir = "Summary table",
                is_external_sync = true,
                Folder_order = []
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistWorkspaceDialogService: dialogs);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            await workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                "IsExternalSync",
                value: false);

            Assert.IsNotNull(dialogs.LastConfirmationRequest);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
                dialogs.LastConfirmationRequest.MessageBoxText);
            Assert.IsFalse(table.is_external_sync);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    /// <summary>同期オンの確認を表示できない場合、明示失敗を返して表を変更せず、確認不成立を承諾へ置換しないことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSummaryCellActionPropagatesDialogFailureWithoutMutation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable)
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => new TestBmsPlaylist(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable { is_external_sync = false };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                "IsExternalSync",
                value: true));

        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.IsFalse(table.is_external_sync);
    }

    /// <summary>重複summary行の削除で表削除の確認を要求し、空選択は削除対象なしとして扱うことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSummaryRemovalRequiresConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable();

        await workspace.PlaylistRemovalWorkflow.RemoveSummaryRowsAsync(
            [
                new PlaylistSummaryRow { TableRef = table },
                new PlaylistSummaryRow { TableRef = table }
            ]);

        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_remove_playlist, dialogs.LastConfirmationRequest.MessageBoxText);
        await workspace.PlaylistRemovalWorkflow.RemoveSummaryRowsAsync(Array.Empty<PlaylistSummaryRow>());
    }

    /// <summary>通常folderの削除確認を要求し、特殊folderと外部同期表では前の確認要求を置換せず新たな削除確認を開始しません。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceFolderRemovalRequiresConfirmationAndRejectsInvalidTargets()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable();
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"));
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_remove_folder, dialogs.LastConfirmationRequest.MessageBoxText);

        UiConfirmationRequest confirmationRequest = dialogs.LastConfirmationRequest;
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(
            table,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
        Assert.AreSame(confirmationRequest, dialogs.LastConfirmationRequest);

        table.is_external_sync = true;
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"));
        Assert.AreSame(confirmationRequest, dialogs.LastConfirmationRequest);
    }

    /// <summary>確認待ち中に外部同期へ切り替わったfolder削除を承諾後に拒否し、開始時の可否だけで禁止対象を変更する誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistRemovalWorkflowRejectsExternalSyncRaceAfterConfirmation()
    {
        var table = new BMSTable();
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
            ConfirmationFactory = _ =>
            {
                table.is_external_sync = true;
                return UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            }
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        var rejectedKinds = new List<PlaylistWorkspaceMutationKind>();
        workspace.MutationRejected += (_, request) => rejectedKinds.Add(request.Kind);

        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(
            table,
            PlaylistFolderNode.CreateFolder("Folder"));

        CollectionAssert.AreEqual(
            new[] { PlaylistWorkspaceMutationKind.RemoveFolder },
            rejectedKinds);
    }

    /// <summary>未選択・確認取消では選択を変更せず、承諾時に選択と実DBの表削除を確定して削除workflowの配送を確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceTableRemovalConfirmationIsOwnedByWorkflowOwner()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        using PlaylistWorkspaceTestPorts.OwnedPlaylistStore owned = PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore();
        var table = new BMSTable { name = "Confirmed removal", playlist_id = 4201 };
        owned.Store.BMSTables.Add(table);
        owned.Store.CommitBMSTableHeaderToDB(table);
        var library = new TestBmsLibrary(owned.SongDbPath);
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _, playlistStoreProvider: () => owned.Store,
            playlistWorkspaceDialogService: dialogs, playlistLibraryProvider: () => library);
        workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

        bool selectionApplied = false;
        await workspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync(null, () => selectionApplied = true);
        Assert.IsFalse(selectionApplied);
        await workspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync(table, () => selectionApplied = true);
        Assert.IsFalse(selectionApplied);
        Assert.IsNotNull(dialogs.LastConfirmationRequest);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await workspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync(table, () => selectionApplied = true);
        Assert.IsTrue(selectionApplied);
        Assert.AreEqual(0, owned.Store.BMSTables.Count);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(owned.SongDbPath).OpenSongDbReadOnly();
        Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM playlist"));
    }

    /// <summary>内蔵recommendedのlevel上書きを警告で拒否し、通常表は確認承諾後だけlibraryへ進んで欠落による元失敗を返します。</summary>
    [TestMethod]
    public async Task PlaylistTableLevelOverwriteWorkflowOwnsRecommendationAndConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        int libraryProviderCalls = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistLibraryProvider: () =>
            {
                libraryProviderCalls++;
                return null!;
            },
            playlistWorkspaceDialogService: dialogs);
        var recommendedTable = new BMSTable { Page_url = new Uri("bmseeker:table.recommended") };
        var normalTable = new BMSTable { Page_url = new Uri("https://example.test/table") };

        await workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(null);
        await workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(recommendedTable);
        Assert.IsNotNull(dialogs.LastMessageRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended,
            dialogs.LastMessageRequest.MessageBoxText);
        Assert.IsNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(0, libraryProviderCalls);

        await workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(normalTable);
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_override_level_warning,
            dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.AreEqual(0, libraryProviderCalls);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(normalTable));
        Assert.AreEqual(1, libraryProviderCalls);
    }

    /// <summary>列設定初期化の取消では同じ設定と無通知を保ち、承諾後だけ設定を置換して一回通知することを確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSummaryColumnResetRequiresConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        PlaylistSummaryColumnSettings originalSettings = workspace.PlaylistSummaryColumnsSettings;
        int notificationCount = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                notificationCount++;
            }
        };

        await workspace.ResetPlaylistSummaryColumnsToDefaultAsync();
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_init_column_settings,
            dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
        Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);
        Assert.AreSame(originalSettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(0, notificationCount);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await workspace.ResetPlaylistSummaryColumnsToDefaultAsync();
        Assert.AreNotSame(originalSettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, notificationCount);
    }

    /// <summary>列設定の初期化確認を表示できない場合、明示失敗と元の設定を維持して未承諾の初期化を防ぎます。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceSummaryColumnResetPropagatesDialogFailure()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        PlaylistSummaryColumnSettings originalSettings = workspace.PlaylistSummaryColumnsSettings;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.ResetPlaylistSummaryColumnsToDefaultAsync());

        Assert.AreSame(originalSettings, workspace.PlaylistSummaryColumnsSettings);
    }

    /// <summary>三つのrecommended政策URIを実score入力から登録し、利用者IDと確認を必要とせず各URIの異なる表を一度ずつ生成することを確認します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportAcceptsThreePoliciesWithoutConfirmationOrPlayerId()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceActionWorkflowTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        Task<ExternalPlaylistImportQueueSummary>? completion = null;
        try
        {
            string databasePath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(databasePath);
            library = new TestBmsLibrary(databasePath, null, null, null, () => new BmsLibraryOptionsSnapshot());
            playlist = new TestBmsPlaylist(databasePath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(), recommendationScoreReader: _ => Task.FromResult(RecommendedImportScores()));
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistWorkspaceDialogService: dialogs, playlistLibraryProvider: () => library);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) => summaryReady.TrySetResult(request.Summary);
            completion = summaryReady.Task;
            playlist.StartupReadiness.BeginPlaylistInitialization("test");
            playlist.StartupReadiness.MarkRequiredPlaylistReady();
            ExternalPlaylistUriSubmissionResult submission = workspace.SubmitExternalPlaylistUriText(
                "bmseeker:table.recommended\nbmseeker:table.recommended?base=failed\nbmseeker:table.recommended?failed=noplay");
            Assert.AreEqual(3, submission.ValidUriCount);

            Assert.IsNull(dialogs.LastConfirmationRequest);
            Assert.IsNull(dialogs.LastMessageRequest);
            playlist.StartupReadiness.MarkRequiredPlaylistReady();
            ExternalPlaylistImportQueueSummary summary = await completion;
            Assert.AreEqual(3, summary.ImportedCount);
            Assert.AreEqual(0, summary.FailedCount);
            Assert.AreEqual(3, playlist.BMSTables.Select(table => table.name).Distinct().Count());
            CollectionAssert.AreEquivalent(new[] { "bmseeker:table.recommended", "bmseeker:table.recommended?base=failed", "bmseeker:table.recommended?failed=noplay" },
                playlist.BMSTables.Select(table => table.Page_url.AbsoluteUri).ToArray());
        }
        finally
        {
            playlist?.StartupReadiness.MarkRequiredPlaylistReady();
            if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }

            playlist?.RequestShutdown("test_cleanup");
            library?.RequestShutdown("test_cleanup");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>ready前の内蔵importを未開始として拒否してscore読取も表登録も行わず、ready後の次明示要求だけを一回登録します。</summary>
    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportRejectsBeforeReadinessAndAcceptsNextExplicitRequest()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceActionWorkflowTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        Task<ExternalPlaylistImportQueueSummary>? completion = null;
        try
        {
            string databasePath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(databasePath);
            library = new TestBmsLibrary(databasePath, null, null, null, () => new BmsLibraryOptionsSnapshot());
            int reads = 0;
            playlist = new TestBmsPlaylist(databasePath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(), recommendationScoreReader: _ => { reads++; return Task.FromResult(RecommendedImportScores()); });
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistWorkspaceDialogService: dialogs, playlistLibraryProvider: () => library);
            workspace.RefreshPlaylistTreeTables(playlist);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) => summaryReady.TrySetResult(request.Summary);
            completion = summaryReady.Task;
            playlist.StartupReadiness.BeginPlaylistInitialization("test");
            Assert.IsFalse(workspace.TryEnqueueBuiltInExternalPlaylistImport("bmseeker:table.recommended"));
            Assert.IsNull(dialogs.LastConfirmationRequest);
            Assert.AreEqual(0, reads);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            Assert.IsFalse(completion.IsCompleted);
            playlist.StartupReadiness.MarkRequiredPlaylistReady();
            Assert.IsTrue(workspace.TryEnqueueBuiltInExternalPlaylistImport("bmseeker:table.recommended"));
            ExternalPlaylistImportQueueSummary summary = await completion;
            Assert.AreEqual(1, summary.ImportedCount);
            Assert.AreEqual(0, summary.FailedCount);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(1, playlist.BMSTables.Count);
        }
        finally
        {
            playlist?.StartupReadiness.MarkRequiredPlaylistReady();
            if (playlist != null) { await playlist.WaitForPlaylistMutationIdleAsync(); }

            playlist?.RequestShutdown("test_cleanup");
            library?.RequestShutdown("test_cleanup");
            Directory.Delete(directory, true);
        }
    }

    private static WalkureScoreInput RecommendedImportScores()
    {
        var input = (JObject)JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Walkure", "math-cases.json")))
            .Single(row => row.Value<string>("name") == "mixed");
        return new WalkureScoreInput(ScoreTableLoadStatus.Loaded,
            ((JArray)(input["observations"] ?? throw new FormatException())).ToImmutableDictionary(
                row => row.Value<string>("md5") ?? throw new FormatException(),
                row => Enum.Parse<WalkureLamp>(row.Value<string>("clearLamp") ?? throw new FormatException(), true), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>ダミーを除くworkspace捕捉後にモデル行を削除しても、捕捉集合の順序と同じ行参照を保持します。</summary>
    [TestMethod]
    public void PlaylistWorkspaceEntrySnapshotPreservesReferencesAfterTableMutation()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => PlaylistWorkspaceViewModel.SnapshotPlaylistEntriesExceptDummy(null));

        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "entry");
        var dummy = new TestablePlaylistEntry("00000000000000000000000000000000", "dummy");
        var table = new BMSTable { entries = [entry, dummy] };

        IReadOnlyList<BMSTableEntry> snapshot = PlaylistWorkspaceViewModel.SnapshotPlaylistEntriesExceptDummy(table);
        table.RemoveBMSTableEntries(table.entries.ToArray());

        CollectionAssert.AreEqual(new[] { entry }, snapshot.ToArray());
    }



    /// <summary>単表再読込では全体cleanupを予約せずidleを保ち、その理由を診断へ渡すことを確認します。</summary>
    [TestMethod]
    public void PlaylistReloadCleanupSkipsSingleReloadInsideWorkspace()
    {
        var log = new List<string>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            reloadCleanupLog: log.Add);

        Assert.IsFalse(workspace.QueuePlaylistReloadCleanup(isFullReload: false, tableCount: 1));
        Assert.IsTrue(workspace.IsPlaylistReloadCleanupIdle);
        StringAssert.Contains(log[0], "reason=not_full_reload");
    }

    /// <summary>先行cleanupのdispatcher待機中に二つの全再読込を予約し、先行と最新だけを処理して中間予約を実行せずidleまで待つことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistReloadCleanupProcessesLatestPendingFullReload()
    {
        var log = new List<string>();
        var dispatcherEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int garbageCollectionCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            reloadCleanupDispatcherIdleWaiter: async () =>
            {
                dispatcherEntered.TrySetResult(true);
                await dispatcherRelease.Task.ConfigureAwait(false);
            },
            reloadCleanupGarbageCollector: () => Interlocked.Increment(ref garbageCollectionCount),
            reloadCleanupLog: log.Add);

        Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 1));
        Task cleanupIdle = workspace.WaitForPlaylistReloadCleanupIdleAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(dispatcherEntered.Task, cleanupIdle, "playlist.cleanup-dispatcher");
            Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 2));
            Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 3));
            Assert.IsFalse(cleanupIdle.IsCompleted);
            dispatcherRelease.TrySetResult(true);

            await cleanupIdle.ConfigureAwait(false);

            Assert.IsTrue(workspace.IsPlaylistReloadCleanupIdle);
            Assert.AreEqual(2, garbageCollectionCount);
            Assert.IsTrue(log.Exists(message => message.Contains("completed cleanupId=") && message.Contains("tableCount=1")));
            Assert.IsTrue(log.Exists(message => message.Contains("completed cleanupId=") && message.Contains("tableCount=3")));
            Assert.IsFalse(log.Exists(message => message.Contains("completed cleanupId=") && message.Contains("tableCount=2")));
        }
        finally
        {
            dispatcherRelease.TrySetResult(true);
            await cleanupIdle.ConfigureAwait(false);
        }
    }

    /// <summary>tree展開状態を明示変更し、同値代入では通知せず展開・折畳みごとに一回通知して表示への変更配送を確認します。</summary>
    [TestMethod]
    public void PlaylistTreeExpansionState_IsOwnedByWorkspaceAndRaisesOnlyOnChange()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<string> propertyNames = [];
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        workspace.IsPlaylistTreeExpanded = false;
        propertyNames.Clear();
        workspace.IsPlaylistTreeExpanded = false;
        Assert.AreEqual(0, propertyNames.Count);

        workspace.IsPlaylistTreeExpanded = true;
        workspace.IsPlaylistTreeExpanded = false;

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(PlaylistWorkspaceViewModel.IsPlaylistTreeExpanded),
                nameof(PlaylistWorkspaceViewModel.IsPlaylistTreeExpanded)
            },
            propertyNames);
        Assert.IsFalse(workspace.IsPlaylistTreeExpanded);
    }

    /// <summary>未接続では変更を禁止するlock状態を返し、実store接続後は表集合・個別表writerと更新中状態に追従して誤受付を防ぎます。</summary>
    [TestMethod]
    public void PlaylistLockState_IsOwnedByWorkspaceAndPreservesPreInitializationFallbacks()
    {
        PlaylistWorkspaceViewModel uninitializedWorkspace = CreateDetailWorkspace(out _);

        Assert.IsTrue(uninitializedWorkspace.IsWriteLockHeldBMSTables);
        Assert.IsTrue(uninitializedWorkspace.IsWriteLockHeldBMSTablesInitializeMin);
        Assert.IsTrue(uninitializedWorkspace.IsWriteLockHeldAnyBMSTable);
        Assert.IsFalse(uninitializedWorkspace.IsPlaylistUpdating);

        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);

            Assert.AreEqual(playlist.IsWriteLockHeldBMSTables, workspace.IsWriteLockHeldBMSTables);
            Assert.AreEqual(playlist.IsWriteLockHeldBMSTablesInitializeMin, workspace.IsWriteLockHeldBMSTablesInitializeMin);
            Assert.AreEqual(playlist.IsWriteLockHeldAnyBMSTable, workspace.IsWriteLockHeldAnyBMSTable);
            Assert.AreEqual(playlist.IsPlaylistUpdating, workspace.IsPlaylistUpdating);

            playlist.AcquireWriterLockBMSTables();
            try
            {
                Assert.IsTrue(workspace.IsWriteLockHeldBMSTables);
            }
            finally
            {
                playlist.FreeWriterLockBMSTables();
            }

            var table = new BMSTable();
            playlist.BMSTables.Add(table);
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                Assert.IsTrue(workspace.IsWriteLockHeldAnyBMSTable);
            }

            playlist.IsPlaylistUpdating = true;
            Assert.IsTrue(workspace.IsPlaylistUpdating);
            playlist.IsPlaylistUpdating = false;
            Assert.IsFalse(workspace.IsPlaylistUpdating);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }



}
