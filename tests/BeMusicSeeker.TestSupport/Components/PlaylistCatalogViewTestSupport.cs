using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using PlaylistCatalogViewChangeTests = BeMusicSeeker.TestSupport.Components.PlaylistCatalogViewTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistCatalogViewTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly TestWorkspace ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        private readonly ApplicationDatabaseScope databaseScope = new();
        /// <summary>このfixtureの接続とモデルが共有するDB所有者です。</summary>
        internal ApplicationDatabaseScope DatabaseScope => databaseScope;

        private readonly string directory;
        private readonly Queue<Action> deliveries = new();
        private readonly PropertyChangedEventHandler? notification;
        private readonly RegularChartListOwner? regularOwner;
        internal ScoreDeliveryUiScheduler DeliveryScheduler { get; } = new();
        internal BMSScore[] OracleScores { get; } = [];
        internal TaskCompletionSource DeliveryRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? RequestedLibraryDelivery { get; private set; }
        internal int DeliveryRequests { get; private set; }
        internal event Action<int>? LibraryDeliveryRequested;
        internal bool SkipWorkspaceDelivery { get; set; }
        internal bool FailWorkspaceDelivery { get; set; }
        private int handled;
        private readonly bool bmson;
        internal string SongDb { get; }
        internal TestBmsLibrary Library { get; }
        internal TestBmsPlaylist Playlist { get; }
        internal PlaylistWorkspaceViewModel Workspace { get; }
        internal MainChartListViewModel MainTable { get; }
        internal bool PauseDelivery { get; set; }
        internal bool BatchNotificationsOnDelivery { get; set; }
        private bool batchNotificationQueued;

        /// <summary>所属・hash索引を確立するentries取込み前に表と未登録entryを準備し、初回捕捉も同じ正規保存値から開始します。</summary>
        /// <param name="modelUiScheduler">L/Pモデル通知だけの配送境界。ScoreDeliveryとentry適用の独立した待機境界は維持します。</param>
        internal Fixture(int count, bool bmson = false, string? firstFolder = null,
            bool regularDelivery = false, bool oracleScores = false, bool connectDelivery = true,
            Action<LR2SongDBExtended>? initializeEntryWriteConnection = null, Action<BMSTable, List<BMSTableEntry>>? prepareTable = null,
            Func<Action, Task>? entryDeliveryScheduler = null, IUiScheduler? modelUiScheduler = null)
        {
            try
            {
                directory = Path.Combine(ownedWorkspace.RootDirectory, nameof(PlaylistCatalogViewChangeTests), Guid.NewGuid().ToString("N"));
                this.bmson = bmson;
                Directory.CreateDirectory(directory);
                SongDb = Path.Combine(directory, "song.db");
                using (LR2SongDBExtended db = databaseScope.OpenSongWrite(SongDb)) { }
                PlaylistPersistenceRepository.EnsureSchema(databaseScope, SongDb);
                var charts = new List<ChartFile>();
                for (int index = 0; index < count; index++)
                {
                    string path = Path.Combine(directory, index + (bmson ? ".bmson" : ".bms"));
                    Write(path, index == 0 ? "A target" : "B background " + index.ToString("D3"));
                    ChartFileSnapshot input = ChartFileContentReader.ReadSnapshot(path);
                    charts.Add(bmson ? BmsonChartFileParser.ParseSnapshot(input) : BmsChartFileParser.ParseSnapshot(input));
                }
                Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
                string? scorePath = oracleScores ? Path.Combine(directory, "score.db") : null;
                if (scorePath != null)
                {
                    using var db = new SQLite.SQLiteConnection(scorePath);
                    db.RunInTransaction(() =>
                    {
                        db.CreateTable<LR2ScoreDB.player>();
                        db.CreateTable<LR2ScoreDB.score>();
                        for (int index = 0; index < count; index++)
                        { db.Insert(new LR2ScoreDB.score { hash = charts[index].Md5, great = index switch { 0 => 10, 1 => 30, 2 => 20, _ => 1 }, totalnotes = 100 }); }
                    });
                }
                Library = modelUiScheduler == null
                    ? new TestBmsLibrary(databaseScope, SongDb, null, scorePath, startupRequiredFileScanReason: null, settings: settings)
                    : new TestBmsLibrary(databaseScope, SongDb, null, scorePath, null, null, modelUiScheduler, settings: settings);
                Library.BmsCharts = bmson ? [] : charts;
                Library.BmsonCharts = bmson ? charts : [];
                new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), SongDb).UpsertSongs(charts);
                if (oracleScores) { Library.InitializeScoresOnly(null); }
                if (oracleScores)
                {
                    var byHash = Library.GetBMSScores().ToDictionary(score => score.hash);
                    OracleScores = charts.Select(chart => byHash[chart.Md5]).ToArray();
                    for (int index = 0; index < count; index++)
                    { OracleScores[index].stddevVal = index switch { 0 => 10, 1 => 30, 2 => 20, _ => 100 + index }; }
                }
                Playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(Library), SongDb,
                    () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
                    playlistUrlCompletionOptionsProvider: () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
                    beatorajaBmtOptionsProvider: () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
                    initializeEntryWriteConnection: initializeEntryWriteConnection, uiScheduler: modelUiScheduler);
                var table = new BMSTable
                {
                    playlist_id = 4188,
                    name = "catalog view",
                    is_external_sync = false,
                    Folder_order = firstFolder == null ? [] : ["background", firstFolder]
                };
                var entries = charts.Select((chart, index) => new BMSTableEntry
                {
                    md5 = chart.Md5,
                    sha256 = chart.Sha256,
                    title = "entry " + index,
                    artist = "entry artist",
                    memo = "entry memo",
                    level = index == 0 ? 2.5 : 3,
                    folder = firstFolder == null ? string.Empty : index < (oracleScores ? 3 : 1) ? firstFolder : "background"
                }).ToList();
                prepareTable?.Invoke(table, entries);
                table.entries = entries;
                Playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                Playlist.CommitBMSTableWithEntriesToDB(table);
                Workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _, playlistStoreProvider: () => Playlist,
                    playlistLibraryProvider: () => Library,
                    dispatchPresentation: action => action(), restoreUiApplyScheduler: entryDeliveryScheduler);
                Workspace.SetDetailDataSource(new PlaylistDetailDataSource(Library, Playlist, new MainChartRowProjectionOwner()));
                Workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
                MainTable = (MainChartListViewModel)(typeof(PlaylistWorkspaceViewModel).GetField("detailMainChartList",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Workspace) ?? throw new InvalidOperationException());
                Workspace.RequestDetailSelection(table);
                Workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
                Workspace.InitializePlaylistDetailSort(new ChartListSortParameters { ColumnsName = nameof(PlaylistDetailRow.Title), Direction = ListSortDirection.Ascending });
                if (regularDelivery)
                {
                    RegularChartListOwner owner = RegularChartListOwnerTestSupport.CreateOwner(MainTable, Workspace, action => action(),
                        DeliveryScheduler);
                    regularOwner = owner;
                    owner.NormalLibraryRefreshApplied += (_, args) =>
                    {
                        if (FailWorkspaceDelivery) { throw new InvalidOperationException("fixture fanout failure"); }
                        if (!SkipWorkspaceDelivery) { Workspace.ApplyLibraryChanges(Library, args.NotificationBatch); }
                    };
                    if (connectDelivery)
                    {
                        Workspace.SetLibraryChangeDelivery((library, cancellationToken) =>
                        {
                            Task delivery = owner.DeliverPendingLibraryChangesAsync(library, cancellationToken);
                            RequestedLibraryDelivery = delivery;
                            DeliveryRequests++;
                            LibraryDeliveryRequested?.Invoke(DeliveryRequests);
                            DeliveryRequested.TrySetResult();
                            return delivery;
                        });
                    }
                    owner.AttachNormalLibraryRefreshSource(Library);
                    return;
                }
                handled = Library.NormalLibraryRefreshNotificationVersion;
                notification = (_, args) =>
                {
                    if (args.PropertyName != "NormalLibraryRefreshNotificationVersion") { return; }
                    if (BatchNotificationsOnDelivery)
                    {
                        if (!batchNotificationQueued)
                        {
                            batchNotificationQueued = true;
                            deliveries.Enqueue(() =>
                            {
                                batchNotificationQueued = false;
                                NormalLibraryRefreshNotificationBatch pending = Library.GetNormalLibraryRefreshNotificationsAfter(handled);
                                handled = pending.LatestVersion;
                                Workspace.ApplyLibraryChanges(Library, pending);
                            });
                        }
                        return;
                    }
                    NormalLibraryRefreshNotificationBatch batch = Library.GetNormalLibraryRefreshNotificationsAfter(handled);
                    handled = batch.LatestVersion;
                    Action deliver = () => Workspace.ApplyLibraryChanges(Library, batch);
                    if (PauseDelivery) { deliveries.Enqueue(deliver); } else { deliver(); }
                };
                Library.PropertyChanged += notification;

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

        internal void ChangeScore(int index, int value)
        {
            OracleScores[index].great = value;
            OracleScores[index].stddevVal = value;
        }

        /// <summary>有効な実DBから対象の記録だけを消し、本番のscore-only再読込みで「記録なし」を公開します。</summary>
        internal void RemoveScoreAndReload(int index)
        {
            using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "score.db")))
            { db.Execute("DELETE FROM score WHERE hash = ?", OracleScores[index].hash); }
            Library.InitializeScoresOnly(null);
        }

        internal Task GetLibraryDelivery() => regularOwner?.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None)
            ?? throw new InvalidOperationException("Real delivery owner was not attached.");

        internal async Task DrainLibraryChanges()
        {
            if (regularOwner == null) { throw new InvalidOperationException("Real delivery owner was not attached."); }
            Task completion = regularOwner.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None);
            if (!completion.IsCompleted)
            {
                ScheduledScoreOperation next = await DeliveryScheduler.NextAsync();
                next.Run(); next.Complete();
            }
            await completion;
        }

        internal void WriteFirst(string title) => Write(Path.Combine(directory, "0" + (bmson ? ".bmson" : ".bms")), title);

        private void Write(string path, string title)
        {
            string content = bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"" + title + "\",\"artist\":\"fixture artist\",\"init_bpm\":120,\"mode_hint\":\"beat-7k\",\"resolution\":240},\"sound_channels\":[{\"name\":\"missing.wav\",\"notes\":[{\"x\":1,\"y\":240,\"l\":0}]}]}"
                : "#PLAYER 1\r\n#BPM 120\r\n#TITLE " + title + "\r\n#ARTIST fixture artist\r\n#WAV01 missing.wav\r\n#00111:01\r\n";
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        internal void Deliver() { while (deliveries.Count > 0) { deliveries.Dequeue()(); } }
        internal bool DeliverOne()
        {
            if (deliveries.Count == 0) { return false; }
            deliveries.Dequeue()(); return true;
        }
        public async ValueTask DisposeAsync()
        {
            Workspace.DetailWorkObserver = null;
            Library.PlaylistReferenceWorkObserver = null;
            if (notification != null) { Library.PropertyChanged -= notification; }
            Deliver();
            Workspace.CancelDetailBuilds();
            await Workspace.WaitForDetailBuildIdleAsync();
            if (regularOwner != null)
            {
                SkipWorkspaceDelivery = true; FailWorkspaceDelivery = false;
                Task stopped = regularOwner.StopAsync();
                DeliveryScheduler.AbortOutstanding();
                while (DeliveryScheduler.TryTake(out ScheduledScoreOperation? operation)) { operation.Run(); operation.Complete(); }
                await stopped;
                regularOwner.Dispose();
            }
            DeliveryScheduler.Dispose();
            Workspace.Dispose();
            Playlist.RequestShutdown("playlist_catalog_view_test");
            Library.RequestShutdown("playlist_catalog_view_test");
            Directory.Delete(directory, recursive: true); ownedWorkspace.Dispose();
        }
    }

}
