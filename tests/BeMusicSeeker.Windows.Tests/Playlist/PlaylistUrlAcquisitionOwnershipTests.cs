using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>URLの取得判断と実workspaceから導入・表示への配送、受理されなかった入力の回収を確認します。</summary>
/// <remarks>取得は応答portで固定し、転送とpath caseの代表では実HTTPも使います。実stagingを使い、Windowとcompiled購読の接続はMainWindowの代表へ分担します。所有移転の代表では実ownerを、保留の代表では実推定・SQLite・FSまで通します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    private readonly List<PlaylistWorkspaceTestPorts.OwnedPlaylistStore> ownedPlaylistStores = [];
    // インスタンスはcaseごとに分かれ、未回収TaskとDBを次caseの所有へ移しません。
    private readonly List<Task> ownedHttpTasks = [];

    /// <summary>methodのcleanupで残った元HTTP処理がすべて終端した場合だけDBを回収します。外側で期限を更新して待ち直しません。</summary>
    [TestCleanup]
    public void CleanupOwnedPlaylistStores()
    {
        Assert.IsFalse(ownedHttpTasks.Any(task => !task.IsCompleted),
            "HTTP所有処理が未回収のため、依存するplaylist DBを保持します。");
        var failures = new List<Exception>();
        for (int index = ownedPlaylistStores.Count - 1; index >= 0; index--)
        {
            try { ownedPlaylistStores[index].Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0) { throw new AggregateException(failures); }
        ownedPlaylistStores.Clear();
        ownedHttpTasks.Clear();
    }

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">同じappが使うプレイリストのDB所有者。</param>
    private PlaylistWorkspaceViewModel CreateWorkspace(ApplicationDatabaseScope databaseScope,
        Action<Action> dispatch,
        Func<PlaylistUrlAcquisitionOptionsSnapshot>? optionsProvider = null,
        PlaylistUrlAcquisitionWorkflow? acquisitionWorkflow = null,
        Func<IReadOnlyList<string>, bool>? installSink = null,
        Action<Uri>? browserSink = null,
        Action? treeExpansionSink = null,
        IUiDialogService? dialogService = null,
        Func<Action, Task>? presentationScheduler = null,
        PlaylistExternalPackageLookupService? externalLookupService = null,
        Func<Task>? installCompletion = null, Action<BMSPlaylist>? captureStore = null,
        Func<DroppedInstallBatchRequest, bool>? ownedInstallSink = null)
    {
        PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedPlaylistStore =
            PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore(databaseScope);
        ownedPlaylistStores.Add(ownedPlaylistStore);
        captureStore?.Invoke(ownedPlaylistStore.Store);
        Func<Action, Task> urlPresentationScheduler = presentationScheduler
            ?? (action =>
            {
                dispatch(action);
                return Task.CompletedTask;
            });
        Action actualTreeExpansionSink = treeExpansionSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlInstallTreeExpansionSink;
        var workspace = new PlaylistWorkspaceViewModel(
            dispatch,
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            acquisitionWorkflow ?? PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
            externalLookupService ?? PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
            optionsProvider ?? PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            ownedInstallSink ?? (installSink == null ? PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink : request => installSink(Array.AsReadOnly(request.Paths))),
            browserSink ?? PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
            () => ownedPlaylistStore.Store,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, urlPresentationScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck, dialogService ?? UndeclaredDialogs.Create(), playlistUrlInstallCompletionProvider: installCompletion);
        workspace.PlaylistUrlInstallTreeExpansionRequested += actualTreeExpansionSink;
        return workspace;
    }


    /// <summary>単体・一括・APIの実取得と実導入・推定を通し、保留の正本、DB行、bytesを取得終端後も保ち、後の明示導入を成立させます。</summary>
    /// <remarks>HTTPとAPI候補だけを固定します。空TITLEによる実推定の保留を必須とし、推定結果を注入しません。管理sessionの初期化を共有するため非並列です。</remarks>
    [TestMethod]
    [DataRow("single")]
    [DataRow("url")]
    [DataRow("api")]
    [DoNotParallelize]
    public async Task DownloadedChart_RealEstimationRetainsPendingBytesForLaterManualInstall(string ingress)
    {
        await using var fixture = new PackageInstallComponentTestSupport.AutomaticContinuationFixture(databaseScope, true, false);
        await fixture.InitializeAsync();
        int estimationApplied = 0;
        fixture.Observer.Applied = () => Interlocked.Increment(ref estimationApplied);
        string staging = TempDirectoryPublisher.Get("url-pending");
        try
        {
            var gateway = new RecordingPlaylistUrlDownloadGateway(staging);
            Uri[] uris = [new("https://example.invalid/first.bms"), new("https://example.invalid/second.bms")];
            byte[][] contents = [.. Enumerable.Range(0, 2).Select(index => Encoding.ASCII.GetBytes(
                "#TITLE\n#ARTIST\n#BPM 120\n#PLAYLEVEL 1\n#WAV01 sound.wav\n#00111:01\n#GENRE distinct-" + index + "\n"))];
            for (int index = 0; index < uris.Length; index++)
            {
                int captured = index;
                gateway.AddResponse(uris[index], () => new AppHttpResponse(uris[captured], new MemoryStream(contents[captured], writable: false)));
            }
            string[] md5s = ["11111111111111111111111111111111", "22222222222222222222222222222222"];
            PlaylistWorkspaceViewModel workspace = CreateWorkspace(databaseScope, action => action(),
                () => new PlaylistUrlAcquisitionOptionsSnapshot { ScanBmsFilesOnStartup = true, AutoInstall = true },
                new PlaylistUrlAcquisitionWorkflow(gateway, _ => { }),
                ownedInstallSink: fixture.Owner.TryEnqueue,
                externalLookupService: new PlaylistExternalPackageLookupService([new BatchHandoffLookupProvider(md5s.Zip(uris).ToDictionary(pair => pair.First, pair => pair.Second))]),
                dialogService: new RecordingPlaylistUrlDialogService(),
                installCompletion: fixture.Owner.WaitForIdleAsync);
            if (ingress == "single") { await workspace.RunSinglePlaylistUrlAsync(uris[0]); }
            else if (ingress == "url") { await workspace.RunPlaylistUrlActionAsync(uris.Select(uri => CreatePlaylistUrlRow(uri.AbsoluteUri, null)), false); }
            else { await workspace.RunPlaylistExternalPackageLookupAsync(md5s.Select(CreatePlaylistExternalPackageRow)); }
            await fixture.Owner.WaitForIdleAsync();

            Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
            int expectedCount = ingress == "single" ? 1 : 2;
            ChartPackage[] pending = [.. fixture.Library.ChartPackagesPending];
            Assert.AreEqual(1, pending.Length, "同じ取得先の単体譜面は一つのパッケージとして保留します。");
            Assert.AreEqual(expectedCount, pending.Sum(package => package.ChartEntries.Count));
            Assert.IsTrue(estimationApplied > 0, "実推定の結果適用を通過して保留へ残ることを必須とします。");
            Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
                Assert.IsTrue(string.Equals(pending.Single().path, db.Table<LR2SongDBExtended.install>().Single().path, StringComparison.OrdinalIgnoreCase));
            }
            foreach (PackageChartEntry entry in pending.SelectMany(package => package.ChartEntries))
            {
                Assert.IsTrue(string.IsNullOrEmpty(entry.Chart.InstallDestination));
                string source = entry.Chart.Path;
                int index = string.Equals(Path.GetFileName(source), "first.bms", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                Assert.IsTrue(File.Exists(source), "取得Taskとowner実idleの後も保留実体を保持します。");
                CollectionAssert.AreEqual(contents[index], File.ReadAllBytes(source));
                entry.ApplyInstallDestinationMetadata(fixture.Destination, "Song", "");
            }
            PendingInstallBatchResult result = fixture.Library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(pending);
            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
            Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly())
            {
                foreach (Uri uri in uris.Take(expectedCount))
                {
                    string destination = Path.Combine(fixture.Destination, Path.GetFileName(uri.AbsolutePath));
                    Assert.IsTrue(File.Exists(destination));
                    Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
                    Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => string.Equals(chart.Path, destination, StringComparison.OrdinalIgnoreCase)));
                    int index = Array.IndexOf(uris, uri);
                    CollectionAssert.AreEqual(contents[index], File.ReadAllBytes(destination));
                }
            }
        }
        finally
        {
            await fixture.Owner.WaitForIdleAsync();
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
    }
}
