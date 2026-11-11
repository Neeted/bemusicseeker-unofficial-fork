using System.Collections.ObjectModel;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>prefixの実property/bulk入口と実SQLite・詳細source/viewだけを接続する、独立したfixtureです。</summary>
internal sealed class PlaylistPrefixCommitTestFixture : IAsyncDisposable
{
    private readonly TestWorkspace ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
    private readonly ApplicationDatabaseScope databaseScope = new();
    /// <summary>このfixtureの接続とモデルが共有するDB所有者です。</summary>
    internal ApplicationDatabaseScope DatabaseScope => databaseScope;

    private readonly string directory;
    internal string SongDb { get; }
    internal TestBmsPlaylist Playlist { get; }
    internal TestBmsLibrary Library { get; }
    internal BMSTable Table { get; }
    internal PlaylistPropertySaveService Service { get; }
    internal PlaylistWorkspaceViewModel Workspace { get; }
    /// <summary>外部属性取得が実際に読み込む、このfixture専用のheaderです。</summary>
    internal string HeaderPath { get; }
    /// <summary>必要BMT出力の実I/O故障を起こす、このfixture専用の台帳です。</summary>
    internal string BmtManifestPath { get; }

    /// <summary>実DB・正本・表示を接続し、必要な場合だけBMT出力先も有効にします。</summary>
    /// <param name="enableBmtOutput">BMT終端保証のケースで専用台帳と固定出力名を準備する場合はtrue。</param>
    /// <param name="modelUiScheduler">モデル通知の配送境界。省略時のWindows callerは既存Dispatcherを維持します。</param>
    /// <param name="playlistWorkspaceDialogService">プロパティ画面の確認など、workspaceが依頼する表示の窓口。省略時は未宣言の要求を検出します。</param>
    internal PlaylistPrefixCommitTestFixture(
        bool enableBmtOutput = false,
        IUiScheduler? modelUiScheduler = null,
        IUiDialogService? playlistWorkspaceDialogService = null)
    {
        try
        {
            directory = Path.Combine(ownedWorkspace.RootDirectory, nameof(PlaylistPrefixCommitTestFixture), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SongDb = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, SongDb);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            HeaderPath = header;
            string bmtDirectory = Path.Combine(directory, "bmt");
            BmtManifestPath = Path.Combine(bmtDirectory, BmtTableExportService.ManifestFileName);
            if (enableBmtOutput)
            {
                Directory.CreateDirectory(bmtDirectory);
                File.WriteAllText(BmtManifestPath, "{\"files\":[],\"playlists\":{}}");
            }
            File.WriteAllText(header, "{\"name\":\"Prefix\",\"symbol\":\"PF\",\"compat_prefix\":\"Q\",\"folder_sort_key\":\"\",\"folder_sort_ascending\":true,\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"source\",\"level\":\"1\"}]");
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.EnablePlaylistUrlCompletion = false;
                values.EnableBeatorajaBmtOutput = enableBmtOutput;
                values.BeatorajaBmtTablePath = bmtDirectory;
            });
            Library = modelUiScheduler == null
                ? new TestBmsLibrary(databaseScope, SongDb, settings: settings)
                : new TestBmsLibrary(databaseScope, SongDb, null, null, null, new UnexpectedBmsLibraryDialogService(), modelUiScheduler, settings: settings);
            Playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(Library), SongDb,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
                playlistUrlCompletionOptionsProvider: () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
                beatorajaBmtOptionsProvider: () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings), uiScheduler: modelUiScheduler);
            Table = new BMSTable
            {
                playlist_id = 7492,
                name = "Prefix",
                symbol = "PF",
                output_dir = enableBmtOutput ? "Fixed" : null,
                is_bmt_output = enableBmtOutput ? true : null,
                compat_prefix = "P",
                Page_url = new Uri(header),
                Header_url = new Uri(header),
                Data_url = new Uri(data),
                is_external_sync = false,
                last_update = new DateTime(2026, 1, 1),
                Folder_order = ["Q3", "X", "P2", "P1"],
                entries = [.. new[] { "P1", "P2", "X", "Q3" }.Select((folder, index) => new BMSTableEntry
            { md5 = (index + 1).ToString("x32"), title = "entry " + index, folder = folder, memo = "current " + index })]
            };
            Playlist.BMSTables = new ObservableCollection<BMSTable>([Table]);
            Playlist.CommitBMSTableWithEntriesToDB(Table);
            Service = new PlaylistPropertySaveService(() => Playlist, () => Library, () => null!, () => new CustomFolderOutputSettingsSnapshot());
            Workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _, playlistStoreProvider: () => Playlist,
                playlistLibraryProvider: () => Library, propertySaveService: Service, dispatchPresentation: action => action(),
                playlistWorkspaceDialogService: playlistWorkspaceDialogService);

            Workspace.PlaylistSyncProgressChanged += (_, _) => { };
            Workspace.SetDetailDataSource(new PlaylistDetailDataSource(Library, Playlist, new MainChartRowProjectionOwner()));
            Workspace.RequestDetailSelection(Table);
            Workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
            Workspace.InitializePlaylistDetailSort(new ChartListSortParameters
            { ColumnsName = nameof(PlaylistDetailRow.Folder), Direction = ListSortDirection.Ascending });
            Workspace.IsPlaylistDetailViewActive = true;

        }
        catch (Exception primaryFailure)
        {
            try { ownedWorkspace.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(primaryFailure, cleanupFailure); }
            throw;
        }
    }

    internal Task Refresh() => Workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
        MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected, false, default);
    internal void Execute(string sql) { using LR2SongDBExtended db = databaseScope.OpenSongWrite(SongDb); db.Execute(sql); }
    public async ValueTask DisposeAsync()
    {
        Workspace.CancelDetailBuilds();
        await Workspace.WaitForDetailBuildIdleAsync();
        await Workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        await Workspace.WaitForPlaylistReloadCleanupIdleAsync();
        Workspace.Dispose(); Playlist.RequestShutdown("playlist_prefix_test"); Library.RequestShutdown("playlist_prefix_test");
        Directory.Delete(directory, recursive: true); ownedWorkspace.Dispose();
    }
}
