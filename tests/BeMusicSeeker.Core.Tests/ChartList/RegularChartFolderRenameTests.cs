using System.Collections.ObjectModel;
using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧の実folder rename、共有受付、失敗通知と停止時の収束を検証します。</summary>
/// <remarks>実FS・SQLite・LR2終端を用い、物理成功後の終端失敗と表示通知の分担を確認します。compiled編集入口はMainWindowの混在選択caseが分担します。</remarks>
[TestClass]
public sealed class RegularChartFolderRenameTests
{

    /// <summary>実VMの編集通知で原文一致・正規化後一致を受付前に終え、Busy中もUnicodeと既存空白名を保持し、活動・抑止・停止・通知・DB更新を始めません。</summary>
    [TestMethod]
    [DataRow("Pack😀", "Pack😀", false)]
    [DataRow("Pack😀", "Pack😀", true)]
    [DataRow(" song", " song", false)]
    [DataRow(" song", " song", true)]
    [DataRow("song", " song ", false)]
    [DataRow("song", " song ", true)]
    public void FolderEdit_UnchangedInputHasNoEffectsBeforeAdmission(string folder, string input, bool busy)
    {
        var scope = new ApplicationDatabaseScope();
        WithTemporarySongDb(database =>
        {
            string root = Path.GetDirectoryName(database) ?? throw new InvalidOperationException();
            string directory = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
            string path = Path.Combine(directory, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1\n#TITLE No change\n");
            ChartFile chart = CreateTestableBmsFile(path);
            var options = new BmsLibraryOptionsSnapshot { UseOnlyShiftJISChars = true };
            var library = new TestBmsLibrary(scope, database, null, null, null, new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => null), () => options)
            { BmsCharts = [chart], BmsonCharts = [] };
            var gateway = new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), database);
            gateway.UpsertSongs([chart]);
            var table = new MainChartListViewModel();
            var dialogs = new FileDbReportRecordingDialogs();
            var playback = new NoOpChartMutationPlaybackPort();
            var activity = new ChartMutationActivityOwner();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(), action => action(),
                chartFileOperations: library.OperationAdmission, mutationDialogs: dialogs, chartMutationActivity: activity, mutationPlayback: playback);
            owner.AttachNormalLibraryRefreshSource(library);
            var row = LibraryChartRow.FromChartFile(library.BmsCharts.Single());
            table.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
            Task? completed = null;
            table.CellEditBeginningRequested += (_, args) => args.Accepted = owner.CanBeginCellEdit(args.Context);
            table.CellEditEndedRequested += (_, args) => completed = owner.CompleteCellEdit(args);
            IDisposable? held = null;
            if (busy) { Assert.IsTrue(library.OperationAdmission.TryEnter(out held)); }
            int availability = 0, activities = 0, suppressions = 0, refreshes = 0;
            library.OperationAdmission.AvailabilityChanged += (_, _) => availability++;
            activity.ActivityChanged += (_, _) => activities++;
            owner.RefreshSuppressionChanged += (_, _) => suppressions++;
            table.DisplayRefreshRequested += (_, _) => refreshes++;
            int revision = library.CatalogRevision;
            try
            {
                Assert.IsTrue(table.TryBeginCellEdit(row, nameof(LibraryChartRow.Folder)));
                table.NotifyCellEditStarted(row, nameof(LibraryChartRow.Folder));
                table.RequestCellEditEnded(row, nameof(LibraryChartRow.Folder), input, true, folder);
                Assert.IsNotNull(completed);
                Assert.AreSame(Task.CompletedTask, completed);
                completed.GetAwaiter().GetResult();
                Assert.AreEqual(0, availability); Assert.AreEqual(0, activities); Assert.AreEqual(0, suppressions);
                Assert.AreEqual(0, refreshes); Assert.AreEqual(0, playback.StopCount); Assert.AreEqual(0, dialogs.Messages.Count);
                Assert.AreEqual(revision, library.CatalogRevision);
                Assert.AreEqual(path, library.BmsCharts.Single().Path);
                Assert.IsTrue(Directory.EnumerateDirectories(root).Contains(directory, StringComparer.Ordinal));
                using LR2SongDBExtended db = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(path, db.Table<LR2SongDB.song>().Single().path);
            }
            finally { held?.Dispose(); owner.StopAsync().GetAwaiter().GetResult(); library.RequestShutdown("folder-no-op"); }
        });
    }

    /// <summary>実libraryの管理領域交差は共有Pの競合を再生停止前に拒否し、物理・DBを保持して予約せず、解放後の新しい明示要求だけを成功させることを確認します。</summary>
    [TestMethod]
    public void FolderRename_ManagedPlaylistBusyRejectsBeforePlaybackStopAndFreshRequestSucceeds()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string managed = Path.Combine(root, "Managed");
            string source = Path.Combine(managed, "Source");
            string destination = Path.Combine(managed, "Moved");
            Directory.CreateDirectory(source);
            string path = Path.Combine(source, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1\n#TITLE Managed\n");
            ChartFile chart = CreateTestableBmsFile(path);
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2CustomFolderOutputBaseDir = managed };
            var library = new TestBmsLibrary(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(root)),
                null, null, new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null!), () => options)
            { BmsCharts = [chart], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs([chart]);
            var playback = new NoOpChartMutationPlaybackPort();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
                chartFileOperations: library.OperationAdmission, mutationDialogs: new FileDbReportRecordingDialogs(), mutationPlayback: playback);
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable held));
            try
            {
                InvalidOperationException busy = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    owner.RenameChartFolderAsync(CreateRenameRequest(chart), "Moved").GetAwaiter().GetResult());
                StringAssert.Contains(busy.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
                Assert.AreEqual(0, playback.StopCount);
                Assert.IsTrue(File.Exists(path));
                Assert.IsFalse(Directory.Exists(destination));
                Assert.IsFalse(library.OperationAdmission.IsActive);
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                { Assert.IsNotNull(db.Find<LR2SongDB.song>(path)); Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(destination, "chart.bms"))); }
            }
            finally { held.Dispose(); }
            Assert.IsTrue(File.Exists(path), "P解放だけで拒否要求を予約実行しません。");
            owner.RenameChartFolderAsync(CreateRenameRequest(chart), "Moved").GetAwaiter().GetResult();
            Assert.AreEqual(1, playback.StopCount);
            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "chart.bms")));
            Assert.IsFalse(library.OperationAdmission.IsActive);
            owner.StopAsync().GetAwaiter().GetResult();
        });
    }

    /// <summary>共有LがBusyなら案内一回で物理変更と表示更新を行わず、解放だけでは再実行せず次の明示renameだけを成功させることを確認します。</summary>
    [TestMethod]
    public void FolderRename_FailsFastWhenSharedChartFileGateIsBusyWithoutRefresh()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "busy-source");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE busy\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [file]
            };
            RenameChartFolderRequest request = CreateRenameRequest(file);
            var table = new MainChartListViewModel();
            int displayRefreshCount = 0;
            table.DisplayRefreshRequested += (_, _) => Interlocked.Increment(ref displayRefreshCount);
            ChartFileOperationSynchronizer synchronizer = library.OperationAdmission;
            var dialogs = new FileDbReportRecordingDialogs();
            using RegularChartListOwner owner = CreateOwner(
                table,
                CreateWorkspaceForOwner(),
                action => action(),
                chartFileOperations: synchronizer,
                mutationDialogs: dialogs);
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(synchronizer.TryEnter(out IDisposable incumbent));
            try
            {
                Task renameTask = owner.RenameChartFolderAsync(request, "busy-destination");
                renameTask.GetAwaiter().GetResult();
                Assert.AreEqual(1, dialogs.Messages.Count, "The failed rename must notify through the mutation dialog route.");
                Assert.IsTrue(Directory.Exists(sourceDirectory));
                Assert.IsFalse(Directory.Exists(Path.Combine(libraryRoot, "busy-destination")));
                Assert.AreEqual(0, Volatile.Read(ref displayRefreshCount));
                incumbent.Dispose();
                Assert.IsTrue(Directory.Exists(sourceDirectory), "拒否した要求を後で自動実行しません。");
                owner.RenameChartFolderAsync(request, "busy-destination").GetAwaiter().GetResult();
                Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "busy-destination")));
            }
            finally
            {
                incumbent.Dispose();
                owner.StopAsync().GetAwaiter().GetResult();
            }
        });
    }

    /// <summary>予約名を含む入力でも、受付前の無作用判定と実計画は同じ一回の正規化結果を使い、二重正規化で元と同じ名前へ畳まれることなく一回の正規化結果へ改名することを確認します。</summary>
    [TestMethod]
    public void FolderRename_ReservedNameInputNormalizesOnceBetweenJudgementAndPlan()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "song");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE reserved\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath) { BmsCharts = [file] };
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
                chartFileOperations: library.OperationAdmission, mutationDialogs: new FileDbReportRecordingDialogs());
            owner.AttachNormalLibraryRefreshSource(library);
            const string input = "CON. song";
            string once = library.NormalizeAutoRenameFolderName(input);
            Assert.AreNotEqual("song", once);
            owner.RenameChartFolderAsync(CreateRenameRequest(file), input).GetAwaiter().GetResult();
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, once)));
            Assert.IsFalse(Directory.Exists(sourceDirectory));
            owner.StopAsync().GetAwaiter().GetResult();
        });
    }

    /// <summary>管理領域外のrenameはplaylist通信とP受付が稼働中でも実ファイルを移し、無関係なPによる操作拒否を検出します。</summary>
    [TestMethod]
    public void FolderRename_RemainsAllowedWhilePlaylistCommunicationIsActive()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "playlist-communication-source");
            string destinationDirectory = Path.Combine(libraryRoot, "playlist-communication-destination");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE playlist communication\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [file]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            playlist.IsPlaylistUpdating = true;
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable playlistAdmission));
            try
            {
                using RegularChartListOwner owner = CreateOwner(
                    new MainChartListViewModel(),
                    CreateWorkspaceForOwner(),
                    action => action(),
                    chartFileOperations: library.OperationAdmission,
                    mutationDialogs: new FileDbReportRecordingDialogs());
                owner.AttachNormalLibraryRefreshSource(library);

                Task renameTask = owner.RenameChartFolderAsync(
                    CreateRenameRequest(file),
                    "playlist-communication-destination");
                renameTask.GetAwaiter().GetResult();

                Assert.IsFalse(Directory.Exists(sourceDirectory));
                Assert.IsTrue(Directory.Exists(destinationDirectory));
                Assert.IsTrue(File.Exists(Path.Combine(destinationDirectory, "chart.bms")));
                owner.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                playlistAdmission.Dispose();
                playlist.IsPlaylistUpdating = false;
            }
        });
    }


    /// <summary>実改名の物理・DB確定後も現在行の必要配送まではLを保持し、確定したパスとFolderの表示後に次の明示改名を受け付けます。</summary>
    [TestMethod]
    public void FolderRenames_WaitForCurrentRowsBeforeReleasingAdmission()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string firstSourceDirectory = Path.Combine(libraryRoot, "first-source");
            string secondSourceDirectory = Path.Combine(libraryRoot, "second-source");
            Directory.CreateDirectory(firstSourceDirectory);
            Directory.CreateDirectory(secondSourceDirectory);
            string firstChartPath = Path.Combine(firstSourceDirectory, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectory, "second.bms");
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE first\r\n");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE second\r\n");
            ChartFile firstFile = CreateTestableBmsFile(firstChartPath);
            ChartFile secondFile = CreateTestableBmsFile(secondChartPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [firstFile, secondFile]
            };
            RenameChartFolderRequest firstRequest = CreateRenameRequest(firstFile);
            RenameChartFolderRequest secondRequest = CreateRenameRequest(secondFile);
            var pendingActions = new Queue<Action>();
            using var actionQueued = new ManualResetEventSlim();
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(
                table,
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(action =>
                {
                    lock (pendingActions)
                    {
                        pendingActions.Enqueue(action);
                    }
                    actionQueued.Set();
                }), chartFileOperations: library.OperationAdmission);
            owner.AttachNormalLibraryRefreshSource(library);
            actionQueued.Wait();
            Action catchUp;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                catchUp = pendingActions.Dequeue();
            }
            catchUp();
            owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None).GetAwaiter().GetResult();
            actionQueued.Reset();
            owner.ApplyMainLibraryView(new ChartListRefreshRoute(ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected, MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false, includeBmsonRows: false), library, null, null, false, Stopwatch.StartNew());
            LibraryChartRow firstRow = table.Rows[0] as LibraryChartRow ?? throw new AssertFailedException("最初の改名対象がありません。");
            LibraryChartRow secondRow = table.Rows[1] as LibraryChartRow ?? throw new AssertFailedException("次の改名対象がありません。");

            Task firstRename = owner.RenameChartFolderAsync(firstRequest, "first-destination");
            actionQueued.Wait();
            Assert.IsFalse(firstRename.IsCompleted);
            Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected)); rejected?.Dispose();
            Action firstDelivery;
            lock (pendingActions) { firstDelivery = pendingActions.Dequeue(); }
            firstDelivery();
            firstRename.GetAwaiter().GetResult();
            Assert.AreEqual(Path.Combine(libraryRoot, "first-destination", "first.bms"), firstRow.path);
            Assert.AreEqual("first-destination", firstRow.Folder);
            actionQueued.Reset();
            Task secondRename = owner.RenameChartFolderAsync(secondRequest, "second-destination");
            actionQueued.Wait();

            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "first-destination")));
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "second-destination")));
            Action coalescedRefresh;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                coalescedRefresh = pendingActions.Dequeue();
            }
            coalescedRefresh();
            secondRename.GetAwaiter().GetResult();
            Assert.AreEqual(Path.Combine(libraryRoot, "second-destination", "second.bms"), secondRow.path);
            owner.StopAsync().GetAwaiter().GetResult();
        });
    }


    /// <summary>renameの表示更新をexecution laneに保留すると停止は未完了となり、予約処理後にrenameと停止を回収して未処理通知を残した終了を検出します。</summary>
    [TestMethod]
    public void FolderRename_StopAsyncDrainsQueuedRefresh()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "queued-source");
            string chartPath = Path.Combine(sourceDirectory, "queued.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE queued\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [file]
            };
            RenameChartFolderRequest request = CreateRenameRequest(file);
            var pendingActions = new Queue<Action>();
            using var actionQueued = new ManualResetEventSlim();
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(action =>
                {
                    lock (pendingActions)
                    {
                        pendingActions.Enqueue(action);
                    }
                    actionQueued.Set();
                }), chartFileOperations: library.OperationAdmission);
            owner.AttachNormalLibraryRefreshSource(library);
            actionQueued.Wait();
            Action catchUp;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                catchUp = pendingActions.Dequeue();
            }
            catchUp();
            owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None).GetAwaiter().GetResult();
            actionQueued.Reset();

            Task renameTask = owner.RenameChartFolderAsync(request, "queued-destination");
            actionQueued.Wait();
            Task stopTask = owner.StopAsync();
            Assert.IsFalse(stopTask.IsCompleted);

            Action pendingAction;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                pendingAction = pendingActions.Dequeue();
            }
            pendingAction();
            stopTask.GetAwaiter().GetResult();
            renameTask.GetAwaiter().GetResult();
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "queued-destination")));
        });
    }

}
