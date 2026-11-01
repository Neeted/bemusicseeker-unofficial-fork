using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistViewPipelineTestSupport;
using TestablePlaylistEntry = BeMusicSeeker.TestSupport.Components.PlaylistViewPipelineTestSupport.TestablePlaylistEntry;

namespace BeMusicSeeker.Tests;


/// <summary>詳細要求の失効・終端・表示変換と行操作Resolverの判断を確認します。</summary>
/// <remarks>実VMを使う終端ケースはworkspaceとMainChartListの確定・通知接続、再生headerは実PlaybackPanelへの配送が対象です。共有構成資源のケースのみ非並列にします。画面menuの実接続はMainWindow側へ分担します。</remarks>
[TestClass]
public sealed class PlaylistViewPipelineTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>行置換準備中に版が失効した詳細要求は準備を一回取り消し、現行行・列表示・summary設定を維持して旧結果を適用しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistDetailTerminal_StaleRequestCancelsPreparationWithoutApplyingRows()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        var oldRows = new List<object>();
        var candidateRows = new List<object> { new object() };
        viewModel.MainChartList.Rows = oldRows;
        var oldColumns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        viewModel.MainChartList.ColumnsSettings = oldColumns;
        viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist = System.Windows.Visibility.Collapsed;
        PlaylistSummaryColumnSettings oldSummaryColumns = viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings;
        viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion = 1;
        int preparingCount = 0;
        int canceledCount = 0;
        viewModel.MainChartList.RowsReplacing += (_, _) =>
        {
            preparingCount++;
            viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion++;
        };
        viewModel.MainChartList.RowsReplacementCanceled += (_, _) => canceledCount++;

        PlaylistDetailTerminalCommitResult result = viewModel.PlaylistWorkspace.ApplyDetailTerminal(
            CreatePlaylistTerminalRequest(candidateRows, requestVersion: 1));

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, viewModel.MainChartList.Rows);
        Assert.AreSame(oldColumns, viewModel.MainChartList.ColumnsSettings);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(oldSummaryColumns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, preparingCount);
        Assert.AreEqual(1, canceledCount);
    }

    /// <summary>summary移行の退役登録で要求世代を進め、summary適用前にも古い詳細終端が現行行を置換できません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryRetirement_InvalidatesOlderDetailTerminalBeforeSummaryApply()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        var oldRows = new List<object>();
        viewModel.MainChartList.Rows = oldRows;
        CustomTableColumnSettings oldColumns = viewModel.MainChartList.ColumnsSettings;
        PlaylistSummaryColumnSettings oldSummaryColumns = viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings;
        System.Windows.Visibility oldColumnsVisibility = viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist;
        viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion = 1;
        PlaylistSourceRetirementRequest retirement =
            viewModel.PlaylistWorkspace.PrepareDetailSourceRetirementForRegularView();
        Assert.IsTrue(
            viewModel.PlaylistWorkspace.RegisterPlaylistSummaryDetailSourceRetirement(retirement));
        viewModel.PlaylistWorkspace.RequestPlaylistSummaryMode(enabled: true);

        PlaylistDetailTerminalCommitResult result = viewModel.PlaylistWorkspace.ApplyDetailTerminal(
            CreatePlaylistTerminalRequest(
                new List<object> { new object() },
                requestVersion: 1));

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, viewModel.MainChartList.Rows);
        Assert.AreSame(oldColumns, viewModel.MainChartList.ColumnsSettings);
        Assert.AreSame(oldSummaryColumns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(oldColumnsVisibility, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.IsTrue(viewModel.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
        Assert.AreEqual(2, viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion);
    }

    /// <summary>新しい退役要求を先に登録した後の古い要求を拒否し、遅れて届いた世代で退役状態を巻き戻しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryRetirementRegistration_KeepsNewestRequestVersion()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        var older = new PlaylistSourceRetirementRequest(1, buildCancellation: null);
        var newer = new PlaylistSourceRetirementRequest(2, buildCancellation: null);

        Assert.IsTrue(
            viewModel.PlaylistWorkspace.RegisterPlaylistSummaryDetailSourceRetirement(newer));
        Assert.IsFalse(
            viewModel.PlaylistWorkspace.RegisterPlaylistSummaryDetailSourceRetirement(older));
    }

    /// <summary>現行sourceから詳細表示を構築して実MainChartListへ同じ最終行を渡し、source世代を保持し、列通知時にbuild/viewロックを残しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistWorkspace_CurrentSourceEntryBuildsAndCommitsMainTableRows()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlaylistDetailSourceRow sourceRow = CreateSourceRow(
            "12121212121212121212121212121212",
            "Current source",
            7);
        viewModel.PlaylistWorkspace.DetailViewState.Source.Rows = [sourceRow];
        viewModel.PlaylistWorkspace.DetailViewState.Source.GenerationId = 4;
        viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion = 1;
        bool buildLockHeldDuringColumnModeEvent = false;
        bool viewLockHeldDuringColumnModeEvent = false;
        viewModel.MainChartList.AppliedColumnModeCommitted += _ =>
        {
            buildLockHeldDuringColumnModeEvent = Monitor.IsEntered(
                viewModel.PlaylistWorkspace.DetailBuildState.SyncRoot);
            viewLockHeldDuringColumnModeEvent = Monitor.IsEntered(
                viewModel.PlaylistWorkspace.DetailViewState.SyncRoot);
        };
        var buildRequest = new PlaylistBuildRequest
        {
            RequestVersion = 1,
            Mode = MainViewUpdateMode.PlaylistFilterSelected,
            RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
            Identity = CreatePlaylistIdentity("workspace-current", libraryIndexVersion: 0)
        };

        PlaylistDetailTerminalApplyResult result = viewModel.PlaylistWorkspace.ApplyDetailFromCurrentSource(
            new PlaylistDetailPresentationRequest
            {
                BuildRequest = buildRequest,
                Mode = MainViewUpdateMode.PlaylistFilterSelected,
                KeywordFilter = string.Empty,
                ModeFilter = ChartModeFilter.All,
                SortParameters = null,
                ColumnSettingMode = MainViewUpdateMode.PlaylistFilterSelected,
                CurrentTreeMode = MainViewUpdateMode.PlaylistFilterSelected,
                Stopwatch = Stopwatch.StartNew()
            });

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(1, result.ViewApply.ViewCount);
        Assert.AreSame(result.ViewApply.FinalRows, viewModel.MainChartList.Rows);
        Assert.AreEqual(4L, viewModel.PlaylistWorkspace.DetailViewState.Source.GenerationId);
        Assert.AreEqual(1L, viewModel.PlaylistWorkspace.DetailViewState.View.GenerationId);
        Assert.IsFalse(buildLockHeldDuringColumnModeEvent);
        Assert.IsFalse(viewLockHeldDuringColumnModeEvent);
    }

    /// <summary>BMS・bmsonの既存表示行と重複行は最新再生状態を読む一方、未所持は未送信印だけを保ち、編集値・scoreを再生成で失いません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlaylistDetailOwnedRowsReadCurrentPlaybackStatusAndPreserveEdits(bool bmson)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            PlaylistDetailSourceRow source = CreateOwnedBmsSourceRow(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Owned", 7, out ChartFile file);
            if (bmson)
            {
                source = new PlaylistDetailSourceRow(source.Entry,
                    (ChartTestValues.Empty(ChartFileKind.Bmson) with
                    {
                        Path = "Owned.bmson",
                        RawTitle = "Owned",
                        ModeHint = "beat-7k"
                    }), scoreSnapshot: new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true });
            }
            ChartFileStatus currentStatus = ChartFileStatus.NONE;
            source.PlaybackStatusProvider = _ => currentStatus;
            var missingEntry = new TestablePlaylistEntry();
            missingEntry.SetTitle("Missing");
            var missing = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null,
                scoreSnapshot: new BMSScore { IsLr2IrScoreUnsent = true });
            BMSTableEntry duplicateEntry = source.Entry.Duplicate();
            duplicateEntry.folder = "duplicate folder";
            var table = new BMSTable { entries = [source.Entry, duplicateEntry] };
            Assert.AreEqual(2, table.entries.Count, "同譜面でも別folderの独立entryは正規所属で保持されます。");
            var duplicateSource = new PlaylistDetailSourceRow(duplicateEntry, source.OwnedCurrent,
                scoreSnapshot: new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true }, entryOrder: 1);
            duplicateSource.PlaybackStatusProvider = _ => currentStatus;
            var view = new PlaylistDetailVirtualView([source, duplicateSource, missing]);
            try
            {
                var first = (PlaylistDetailRow)view[0];
                var duplicate = (PlaylistDetailRow)view[1];
                var absent = (PlaylistDetailRow)view[2];
                first.comment = "edited comment";
                first.memo = "edited memo";
                first.Level = "9";
                foreach (ChartFileStatus status in new[] { ChartFileStatus.LOADING, ChartFileStatus.PLAY, ChartFileStatus.PAUSE, ChartFileStatus.PLAY, ChartFileStatus.NONE })
                {
                    currentStatus = status;
                    Assert.AreEqual(status | ChartFileStatus.SCORE_UNSENT, first.status);
                    Assert.AreEqual(status | ChartFileStatus.SCORE_UNSENT, duplicate.status);
                    Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, absent.status);
                    Assert.AreSame(first, view[0]);
                    Assert.AreEqual(3, view.RealizedRowCount);
                }
                Assert.AreEqual(212, first.score);
                Assert.AreEqual("edited comment", first.comment);
                Assert.AreEqual("edited memo", first.memo);
                Assert.AreEqual("9", first.Level);
                Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, file.Status);
            }
            finally
            {
                view.DisposeRealizedRows();
            }
        });
    }

    /// <summary>BMS・bmsonの未実体化行を後から生成しても現在のPause状態とscoreを読み、構築時のPlay状態を固定しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlaylistDetailVirtualView_UnrealizedRowUsesLatestPlaybackStatusWhenCreated(bool bmson)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            PlaylistDetailSourceRow source = CreateOwnedBmsSourceRow(
                "cccccccccccccccccccccccccccccccc", "Unrealized", 7, out ChartFile file);
            if (bmson)
            {
                source = new PlaylistDetailSourceRow(source.Entry,
                    (ChartTestValues.Empty(ChartFileKind.Bmson) with
                    {
                        Path = "Unrealized.bmson",
                        RawTitle = "Unrealized",
                        ModeHint = "beat-7k"
                    }), scoreSnapshot: new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true });
            }
            ChartFileStatus currentStatus = ChartFileStatus.PLAY;
            source.PlaybackStatusProvider = _ => currentStatus;
            BMSTableEntry duplicateEntry = source.Entry.Duplicate();
            duplicateEntry.folder = "duplicate folder";
            var table = new BMSTable { entries = [source.Entry, duplicateEntry] };
            Assert.AreEqual(2, table.entries.Count);
            var duplicateSource = new PlaylistDetailSourceRow(duplicateEntry, source.OwnedCurrent,
                scoreSnapshot: new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true }, entryOrder: 1);
            duplicateSource.PlaybackStatusProvider = _ => currentStatus;
            var view = new PlaylistDetailVirtualView([source, duplicateSource]);
            try
            {
                var first = (PlaylistDetailRow)view[0];
                Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.SCORE_UNSENT, first.status);
                currentStatus = ChartFileStatus.PAUSE;
                Assert.AreEqual(1, view.RealizedRowCount);
                var late = (PlaylistDetailRow)view[1];
                Assert.AreEqual(2, view.RealizedRowCount);
                Assert.AreEqual(ChartFileStatus.PAUSE | ChartFileStatus.SCORE_UNSENT, first.status);
                Assert.AreEqual(first.status, late.status);
                Assert.AreEqual(212, late.score);
                currentStatus = ChartFileStatus.NONE;
                Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, late.status);
            }
            finally
            {
                view.DisposeRealizedRows();
            }
        });
    }

    /// <summary>実PlaybackPanelのheader設定はBMSのtitle・subtitle・artistを各表示経路へ渡し、subtitleをtitleへ重ねません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SetBmsPlayerHeader_UsesSplitBmsMetadata()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "ouroVoros" + ".bms", Title = "ouroVoros", RawTitle = "ouroVoros", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Subtitle = "[LAST BOSS]" };
        file = file with { Artist = "Nepentropy Movie:Vogeln obj:sak", RawArtist = "Nepentropy Movie:Vogeln obj:sak" };

        PlaybackPanelViewModel panel = viewModel.PlaybackPanel;
        panel.SetBmsPlayerHeader(file);

        Assert.AreSame(file, panel.DisplayedChart);
        Assert.AreEqual("ouroVoros", panel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[LAST BOSS]", panel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("Nepentropy Movie:Vogeln obj:sak", panel.BmsPlayerHeaderArtist);
        Assert.AreEqual("ouroVoros", panel.PlayerHeaderTitle);
        Assert.AreEqual("[LAST BOSS]", panel.PlayerHeaderSubtitle);
        Assert.AreEqual("Nepentropy Movie:Vogeln obj:sak", panel.PlayerHeaderArtist);
    }

    /// <summary>再生開始の入口も表示中譜面と両headerへtitle・subtitle・artistを同期し、前譜面のheaderを残しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void BeginPlayback_SynchronizesPlayerHeader()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "abababababababababababababababab", Path = "NextTitle" + ".bms", Title = "NextTitle", RawTitle = "NextTitle", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        file = file with { Subtitle = "[NextSubtitle]" };
        file = file with { Artist = "NextArtist", RawArtist = "NextArtist" };

        PlaybackPanelViewModel panel = viewModel.PlaybackPanel;
        panel.BeginPlayback(file, 0);

        Assert.AreSame(file, panel.DisplayedChart);
        Assert.AreEqual("NextTitle", panel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[NextSubtitle]", panel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("NextArtist", panel.BmsPlayerHeaderArtist);
        Assert.AreEqual("NextTitle", panel.PlayerHeaderTitle);
        Assert.AreEqual("[NextSubtitle]", panel.PlayerHeaderSubtitle);
        Assert.AreEqual("NextArtist", panel.PlayerHeaderArtist);
    }

    /// <summary>実VMの外部同期表memo保存では解決bmsonのMD5を元entryへ補わず、DBもSHA256だけを保って外部由来identityを変えません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task CommitPlaylistRow_ExternalSyncEntryDoesNotBackfillHashesFromResolvedChart()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "PlaylistViewPipelineTests", Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 9101,
                name = "External",
                is_external_sync = true
            };
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
            }
            var entry = new TestablePlaylistEntry
            {
                parent = table,
                folder = string.Empty,
                memo = "memo"
            };
            entry.SetSha256(new string('9', 64));
            entry.SetTitle("External Bmson");
            ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = "C:\\Songs\\Bmson\\external.bmson",
                RawTitle = "External Bmson",
                Md5 = "99999999999999999999999999999999",
                Sha256 = entry.sha256
            };
            PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, (bmson)).CreateViewRow();
            using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
            BMSPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(databaseScope, songDbPath, TestSettingsFactory.CreateIsolatedSettings());
            table.entries = [entry];
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            typeof(MainWindowViewModel).GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, playlist);

            var context = new MainChartListCellEditContext(
                row,
                nameof(PlaylistDetailRow.memo),
                ChartOperationSourceScope.PlaylistOwned,
                MainViewOperationSection.Playlist);
            Task commitTask = viewModel.PlaylistWorkspace.CompleteDetailEdit(
                new MainChartListCellEditEndedEventArgs(context, "updated memo", commit: true));
            await commitTask;

            using LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            BMSTableEntry stored = verifyDb.Table<BMSTableEntry>().Single(dbRow => dbRow.playlist_id == table.playlist_id);
            Assert.IsNull(entry.md5);
            Assert.IsNull(stored.md5);
            Assert.AreEqual(entry.sha256, stored.sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static PlaylistDetailSourceRow CreateOwnedBmsSourceRow(
        string hash,
        string title,
        int? mode,
        out ChartFile file)
    {
        file = ChartTestValues.Empty();
        file = file with { Md5 = hash, Path = title + ".bms", Title = title, RawTitle = title, Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = mode };
        file = ChartFileProjection.WithScore(file, ChartScoreSnapshot.FromBmsScore(new BMSScore
        {
            hash = hash,
            perfect = 100,
            great = 12,
            totalnotes = 200,
            IsLr2IrScoreUnsent = true
        }, file.Path));
        return new PlaylistDetailSourceRow(
            new TestablePlaylistEntry(file),
            (file));
    }

}
