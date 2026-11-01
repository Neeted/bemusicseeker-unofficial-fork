using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ApplicationCompositionTestSupport;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed partial class ApplicationCompositionTests
{

    /// <summary>重複選択とnull・false・trueの出力設定から実変更だけを保存し、必要出力の終端後に完了して再要求の無変更では出力を繰り返しません。</summary>
    [TestMethod]
    public void PlaylistWorkspaceBmtOutputTogglePersistsDistinctChangedHeadersAndRequestsSummaryRefresh()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var databaseScope = new ApplicationDatabaseScope();
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, nameof(ApplicationCompositionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var first = new BMSTable { playlist_id = 11, name = "First", is_bmt_output = null };
            var second = new BMSTable { playlist_id = 12, name = "Second", is_bmt_output = false };
            var third = new BMSTable { playlist_id = 13, name = "Third", is_bmt_output = true };
            string bmtPath = Path.Combine(tempDirectory, "beatoraja", "table.json");
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = true,
                BeatorajaBmtTablePath = bmtPath
            }, () => new CustomFolderOutputSettingsSnapshot(), uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second, third])
            };
            playlist.CommitBMSTableHeadersToDB([first, second, third]);
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
                new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables),
                PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
                () => playlist,
                PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
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
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, playlistUrlInstallCompletionProvider: () => Task.CompletedTask);
            AttachImmediatePlaylistPresentationRouter(workspace);
            int completedOutputCount = 0;
            playlist.BmtOutput.RequestProgressReporter = (_, running) =>
            {
                if (!running) { completedOutputCount++; }
            };

            long generationBeforeFirstAction = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.ApplyPlaylistSummaryCellActionAsync(
                [
                    new PlaylistSummaryRow { TableRef = first },
                    new PlaylistSummaryRow { TableRef = second },
                    new PlaylistSummaryRow { TableRef = second },
                    new PlaylistSummaryRow { TableRef = third }
                ],
                "IsBmtOutput",
                value: true).GetAwaiter().GetResult();

            Assert.IsNull(first.is_bmt_output);
            Assert.AreEqual(true, second.is_bmt_output);
            Assert.AreEqual(true, third.is_bmt_output);
            Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > generationBeforeFirstAction);
            Assert.AreEqual(1, completedOutputCount, "必要出力の実終端後に変更Taskを完了します。");
            long generationBeforeNoOp = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = third }],
                "IsBmtOutput",
                value: true).GetAwaiter().GetResult();
            Assert.AreEqual(generationBeforeNoOp, workspace.CurrentPlaylistSummaryDataRebuildGeneration);
            Assert.AreEqual(1, completedOutputCount, "変更のない再要求は必要出力を再実行しません。");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.playlist persistedFirst = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == first.playlist_id);
            LR2SongDBExtended.playlist persistedSecond = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == second.playlist_id);
            LR2SongDBExtended.playlist persistedThird = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == third.playlist_id);
            Assert.IsNull(persistedFirst.is_bmt_output);
            Assert.AreEqual(true, persistedSecond.is_bmt_output);
            Assert.AreEqual(true, persistedThird.is_bmt_output);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>ライブラリの並列度取得ごとに注入snapshotを再評価し、provider差替え後も初回並列度を使い続ける誤りを検出します。</summary>
    [TestMethod]
    public void LibraryEvaluatesTheInjectedOptionsProviderForEachOperation()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var databaseScope = new ApplicationDatabaseScope();
        string tempDirectory = Path.Combine(migrationWorkspace.RootDirectory, "BeMusicSeekerOptionsProvider_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        using (LR2SongDBExtended initialize = databaseScope.OpenSongWrite(songDbPath))
        {
        }
        BmsLibraryOptionsSnapshot current = new()
        {
            PendingInstallEstimateMaxParallelPackages = 3
        };
        try
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, startupRequiredFileScanReason: null, optionsSnapshotProvider: () => current, applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge, uiScheduler: new TestUiScheduler(() => null));
            Assert.AreEqual(3, library.ResolvePendingInstallEstimateParallelPackageDegree());
            current = new BmsLibraryOptionsSnapshot
            {
                PendingInstallEstimateMaxParallelPackages = 5
            };
            Assert.AreEqual(5, library.ResolvePendingInstallEstimateParallelPackageDegree());
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
