using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>prefixの実property/bulk入口と実SQLite・詳細source/viewだけを接続する、独立したfixtureです。</summary>
internal sealed class PlaylistPrefixCommitTestFixture : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistPrefixCommitTestFixture), Guid.NewGuid().ToString("N"));
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
    internal PlaylistPrefixCommitTestFixture(bool enableBmtOutput = false)
    {
        Directory.CreateDirectory(directory);
        SongDb = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
        PlaylistPersistenceRepository.EnsureSchema(SongDb);
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
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
        {
            values.OperationModeLR2DB = false;
            values.EnablePlaylistUrlCompletion = false;
            values.EnableBeatorajaBmtOutput = enableBmtOutput;
            values.BeatorajaBmtTablePath = bmtDirectory;
        });
        Library = new TestBmsLibrary(SongDb, settings: settings);
        Playlist = MainWindowViewModelTestFactory.CreatePlaylist(SongDb, settings, library: Library);
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
        Workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _, playlistStoreProvider: () => Playlist,
            playlistLibraryProvider: () => Library, propertySaveService: Service, dispatchPresentation: action => action());

        Workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
        Workspace.PlaylistSyncProgressChanged += (_, _) => { };
        Workspace.SetDetailDataSource(new PlaylistDetailDataSource(Library, Playlist, new MainChartRowProjectionOwner()));
        Workspace.RequestDetailSelection(Table);
        Workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
        Workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        { ColumnsName = nameof(PlaylistDetailRow.Folder), Direction = ListSortDirection.Ascending });
        Workspace.IsPlaylistDetailViewActive = true;
    }

    internal Task Refresh() => Workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
        MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected, false, default);
    internal void Execute(string sql) { using var db = new LR2SongDBExtended(SongDb); db.Execute(sql); }
    public async ValueTask DisposeAsync()
    {
        Workspace.CancelDetailBuilds();
        await Workspace.WaitForDetailBuildIdleAsync();
        await Workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        await Workspace.WaitForPlaylistReloadCleanupIdleAsync();
        Workspace.Dispose(); Playlist.RequestShutdown("playlist_prefix_test"); Library.RequestShutdown("playlist_prefix_test");
        Directory.Delete(directory, recursive: true);
    }
}
