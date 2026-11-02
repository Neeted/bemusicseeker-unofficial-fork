using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistActionTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>workspaceから保存・削除・browser・lamp遷移・reload cleanupへの実要求と失敗経路を検証します。</summary>
/// <remarks>SQLiteの確定とlockは実資源を使い、lampのVM受信だけ共有dispatcherでbuild終端を観測します。実画面のBinding・modal・resizeは上位WPFケースが分担します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class PlaylistWorkspaceActionWorkflowTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実VMのlamp要求購読から空名folderの詳細build終端まで待ち、keyword・選択・確定行が同じ条件へ揃うことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistLampNavigation_EmptyFolderRunsMainWindowConsumerAndCommitsFinalRows()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        TestUiDispatcherHost.Invoke(() =>
        {
            string tempDirectory = Path.Combine(
                migrationWorkspace.RootDirectory,
                nameof(PlaylistWorkspaceActionWorkflowTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            MainWindowViewModel? viewModel = null;
            try
            {
                string songDbPath = Path.Combine(tempDirectory, "song.db");
                Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
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
                ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
                StartupLibraryConstructionTestSupport.CreateSongDatabase(databaseScope, songDbPath);
                PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);

                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, settings, viewModel);
                library.BmsCharts = [];
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(databaseScope, songDbPath, settings, library: library);
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
                    lr2ConfigOwner: null,
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

    /// <summary>実VMの全体lamp要求から実名[NO SONG]を含む三つの通常folderのNP行を確定し、同名folderへの移動後も通常選択を維持する。本物の特殊ノードへの要求は通知・選択・filter・詳細要求版を変えず拒否する。</summary>
    /// <remarks>実MainWindow consumerと詳細buildの終端まで動かし、要求の発行だけでなく最終確定行と通常filterへの接続を確認する。特殊ノードは同じ表示名でもIsSpecialで区別し、カテゴリ条件の網羅は集計・一覧パーサー側へ分担する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistLampNavigation_OverallAndRealNoSongFolderCommitNormalRowsAndRejectSpecialNode()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        TestUiDispatcherHost.Invoke(() =>
        {
            string tempDirectory = Path.Combine(
                migrationWorkspace.RootDirectory,
                nameof(PlaylistWorkspaceActionWorkflowTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            MainWindowViewModel? viewModel = null;
            try
            {
                string songDbPath = Path.Combine(tempDirectory, "song.db");
                Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
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
                ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
                StartupLibraryConstructionTestSupport.CreateSongDatabase(databaseScope, songDbPath);
                PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);

                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, settings, viewModel);
                library.BmsCharts = [];
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(databaseScope, songDbPath, settings, library: library);
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
                var namedFolderNoSong = new TestablePlaylistEntry(
                    "44444444444444444444444444444444",
                    "real folder named no song")
                {
                    folder = "[NO SONG]"
                };
                namedFolderNoSong.SetSha256(new string('4', 64));
                const int playlistId = 7404;
                var table = new BMSTable
                {
                    playlist_id = playlistId,
                    name = "Overall lamp navigation",
                    entries = [firstFolderNoPlay, secondFolderNoSong, normalNonMatch, namedFolderNoSong],
                    Folder_order = ["First", "Second", "[NO SONG]"]
                };
                playlist.BMSTables = new ObservableCollection<BMSTable> { table };
                PlaylistFolderNode namedFolder = table.FolderNodes.Single(node => !node.IsSpecial && node.FolderName == "[NO SONG]");
                PlaylistFolderNode specialNode = table.FolderNodes.Single(node => node.IsSpecial);
                Assert.AreEqual(namedFolder.DisplayName, specialNode.DisplayName);
                Assert.AreEqual(string.Empty, specialNode.FolderName);
                Assert.IsFalse(table.FolderNodes.Any(node => !node.IsSpecial && node.FolderName == specialNode.FolderName));

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
                            [namedFolderNoSong.sha256] = CreateScore(
                                namedFolderNoSong.md5,
                                ClearType.NO_SONG,
                                RankType.INVALID)
                        }
                    }
                };

                var profile = new LibraryProfile(
                    operationModeLR2DB: false,
                    songDbPath,
                    [tempDirectory],
                    lr2ConfigOwner: null,
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
                    new[] { firstFolderNoPlay, secondFolderNoSong, namedFolderNoSong },
                    finalRows.Select(row => row.Entry).ToArray());
                Assert.IsFalse(finalRows.Any(row => ReferenceEquals(row.Entry, normalNonMatch)));
                Assert.AreEqual(
                    MainViewUpdateMode.PlaylistFilterSelected,
                    viewModel.MainChartList.LastCompletion.Mode);

                Assert.IsTrue(viewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(
                    PlaylistLampSegmentInvocationRequest.ForClear(
                        playlistId.ToString(CultureInfo.InvariantCulture),
                        namedFolder.FolderName,
                        PlaylistLampClearCategory.NP)));
                requestVersion = viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.PlaylistWorkspace.WaitForDetailRequestCompletionAsync(requestVersion),
                    "playlist-lamp.real-no-song.consumer-detail-build");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(2, publications.Count);
                Assert.AreEqual(PlaylistLampViewerNavigationScope.Folder, publications[1].Request.Scope);
                selection = viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection();
                Assert.IsNotNull(selection);
                Assert.AreSame(table, selection.Table);
                Assert.AreEqual(namedFolder.FolderName, selection.FolderName);
                Assert.AreEqual(PlaylistDetailSelectionScope.Folder, selection.Scope);
                Assert.AreEqual(PlaylistDetailFilter.PlaylistFilter, selection.Filter);
                CollectionAssert.AreEqual(
                    new[] { namedFolderNoSong },
                    viewModel.MainChartList.Rows.Cast<PlaylistDetailRow>().Select(row => row.Entry).ToArray());
                Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, viewModel.MainChartList.LastCompletion.Mode);
                ChartListFilterSnapshot appliedFilter = viewModel.PlaylistWorkspace.CapturePlaylistDetailFilterSnapshot();
                Assert.AreEqual(appliedFilter.KeywordFilter, viewModel.ChartFilters.KeywordFilter);

                Assert.IsFalse(viewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(
                    PlaylistLampSegmentInvocationRequest.ForClear(
                        playlistId.ToString(CultureInfo.InvariantCulture),
                        specialNode.FolderName,
                        PlaylistLampClearCategory.NP)));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(2, publications.Count);
                Assert.AreEqual(requestVersion, viewModel.PlaylistWorkspace.DetailBuildState.RequestVersion);
                Assert.IsTrue(selection.Matches(viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection()));
                ChartListFilterSnapshot afterRejected = viewModel.PlaylistWorkspace.CapturePlaylistDetailFilterSnapshot();
                Assert.AreEqual(appliedFilter.KeywordFilter, afterRejected.KeywordFilter);
                Assert.AreEqual(appliedFilter.ModeFilter, afterRejected.ModeFilter);
                Assert.AreEqual(appliedFilter.KeywordFilter, viewModel.ChartFilters.KeywordFilter);
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

}
