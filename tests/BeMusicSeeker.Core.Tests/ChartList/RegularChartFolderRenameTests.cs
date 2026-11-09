using System.Collections.ObjectModel;
using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
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
            var libraryDialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(root)),
                null, null, libraryDialogs, new TestUiScheduler(() => null!), () => options)
            { BmsCharts = [chart], BmsonCharts = [] };
            var mutationDialogs = new FileDbReportRecordingDialogs();
            bool busyRequestedAfterAdmissionRelease = false;
            mutationDialogs.OnMessage = () =>
            {
                bool free = library.OperationAdmission.TryEnter(out IDisposable? lease);
                lease?.Dispose();
                busyRequestedAfterAdmissionRelease = free;
            };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs([chart]);
            var playback = new NoOpChartMutationPlaybackPort();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
                chartFileOperations: library.OperationAdmission, mutationDialogs: mutationDialogs, mutationPlayback: playback);
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable held));
            try
            {
                InvalidOperationException busy = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    owner.RenameChartFolderAsync(CreateRenameRequest(chart), "Moved").GetAwaiter().GetResult());
                StringAssert.Contains(busy.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
                Assert.AreEqual(1, mutationDialogs.Messages.Count(message => message.MessageBoxText.Contains(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, StringComparison.Ordinal)),
                    "物理処理の前の拒否の理由は、所有者の窓口へ一件だけ依頼する。");
                Assert.IsTrue(busyRequestedAfterAdmissionRelease, "依頼の時点で論理Lは解放済み。");
                Assert.AreEqual(0, libraryDialogs.ModelMessages, "モデルの窓口へ届けない。");
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

    /// <summary>
    /// 保証：重複表示中の実改名で必須の変更配送が失敗しても、抑制解除より前に開始した重複準備の終端まで待ってからLを解放し、
    /// 先行失敗がないので配送の失敗を改名の結果にします。準備はグループの変更通知（準備側スレッド、配送要求の前）で保留します。
    /// 検出する誤り：配送の失敗で準備の待機を省くこと（準備の終端前のL解放）、配送失敗時に準備を開始しないこと、
    /// 配送の失敗を準備側の結果で置き換えること。
    /// </summary>
    /// <remarks>
    /// 正しい実装は保留中に外から見える事象を起こさないため、改名が準備を待っていることは時間によらず観測できません。
    /// 保留中に一度だけ置く短い観測窓は「保留中にLが解放されないこと」の否定観測で、配送が失敗すると準備の待機を飛ばす実装の検出だけに使い、
    /// 成功の推定には使いません。正しい実装は窓の長さだけ待ちます。終端は窓の後に保留を解除してから、通知とTaskの終端で待ちます。
    /// </remarks>
    [TestMethod]
    public void FolderRename_RequiredDeliveryFailureStillAwaitsDuplicatePreparation()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException("一時DBの親ディレクトリがありません。");
            string firstDirectory = Directory.CreateDirectory(Path.Combine(libraryRoot, "first")).FullName;
            string secondDirectory = Directory.CreateDirectory(Path.Combine(libraryRoot, "second")).FullName;
            string firstPath = Path.Combine(firstDirectory, "chart.bms");
            string secondPath = Path.Combine(secondDirectory, "chart.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#TITLE delivery failure\r\n");
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#TITLE delivery failure\r\n");
            ChartFile first = CreateTestableBmsFile(firstPath);
            ChartFile second = CreateTestableBmsFile(secondPath);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot())
            { BmsCharts = [first, second], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs([first, second]);
            var scheduler = new RejectingUiScheduler(new TestUiScheduler(() => null));
            var busyDialogs = new FileDbReportRecordingDialogs();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
                scheduler, chartFileOperations: library.OperationAdmission, mutationDialogs: new FileDbReportRecordingDialogs());
            owner.AttachNormalLibraryRefreshSource(library);
            owner.MaintenanceNavigationPresentationRequested += (_, _) => { };
            owner.ConfigureDuplicateNavigationDialogs(busyDialogs);
            using var gateRelease = new ManualResetEventSlim();
            var gateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int gateEnabled = 0, gateHits = 0;
            Task? gatedPreparation = null;
            bool? preparationTerminalAtAdmissionRelease = null;
            var admissionReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 改名中に非nullのグループで DuplicateChartGroups を通知するのは準備の検索だけなので、最初の受信で準備を保留します。
            library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(BMSLibrary.DuplicateChartGroups) || Volatile.Read(ref gateEnabled) == 0
                    || library.DuplicateChartGroups == null || Interlocked.Increment(ref gateHits) != 1)
                { return; }
                gatedPreparation = owner.PrepareDuplicateDataAsync();
                gateReached.TrySetResult();
                gateRelease.Wait();
            };
            library.OperationAdmission.AvailabilityChanged += (_, _) =>
            {
                if (Volatile.Read(ref gateEnabled) == 0 || library.OperationAdmission.IsActive || preparationTerminalAtAdmissionRelease.HasValue)
                { return; }
                Task? preparation = gatedPreparation;
                if (preparation != null) { preparationTerminalAtAdmissionRelease = preparation.IsCompleted; }
                admissionReleased.TrySetResult();
            };
            Task? renameTask = null;
            try
            {
                Assert.IsTrue(owner.NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected).GetAwaiter().GetResult(),
                    "前提：変更前の重複選択を受理できません。");
                Assert.IsNotNull(library.DuplicateChartGroups, "前提：変更前の重複結果が準備されていません。");
                Volatile.Write(ref gateEnabled, 1);
                scheduler.Rejecting = true;
                renameTask = owner.RenameChartFolderAsync(CreateRenameRequest(first), "renamed");
                TestUiDispatcherHost.AwaitNotificationAsync(gateReached.Task, renameTask, "folder rename duplicate preparation gate")
                    .GetAwaiter().GetResult();

                Assert.IsTrue(scheduler.RejectionCount > 0, "前提：必須の変更配送が失敗していません。");
                Assert.IsNotNull(gatedPreparation);
                Assert.IsFalse(gatedPreparation.IsCompleted, "保留中の準備が登録されていません。");
                // 誤実装の検出窓です。正しい実装では改名もL解放も起きず、窓の長さだけ待ちます。
                Task.WhenAny(renameTask, admissionReleased.Task, Task.Delay(TimeSpan.FromMilliseconds(500))).GetAwaiter().GetResult();
                Assert.IsFalse(admissionReleased.Task.IsCompleted, "保留中の準備の終端前にLを解放しました。");
                Assert.IsFalse(renameTask.IsCompleted, "配送の失敗で準備を待たずに改名が終わりました。");
                Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected), "準備の終端前にLを解放しました。");
                rejected?.Dispose();

                scheduler.Rejecting = false;
                gateRelease.Set();
                InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => renameTask.GetAwaiter().GetResult());
                Assert.AreEqual(RejectingUiScheduler.Reason, failure.Message, "改名の結果が必須配送の失敗ではありません。");
                Assert.IsTrue(preparationTerminalAtAdmissionRelease, "準備の終端前にLを解放しました。");
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsNotNull(library.DuplicateChartGroups, "準備の完了後に重複結果がありません。");
                Assert.AreEqual(0, busyDialogs.Messages.Count);
                Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "renamed")));
            }
            finally
            {
                scheduler.Rejecting = false;
                gateRelease.Set();
                try { renameTask?.GetAwaiter().GetResult(); } catch (Exception) { /* 表明済みの改名失敗だけを回収します。 */ }
                owner.StopAsync().GetAwaiter().GetResult();
                library.RequestShutdown("folder-rename-delivery-failure");
            }
        });
    }

    /// <summary>有効化中だけ予約を未受理で返し、必須の変更配送の失敗を本番の受付経路で起こします。他の操作は内側へ委譲します。</summary>
    private sealed class RejectingUiScheduler(IUiScheduler inner) : IUiScheduler
    {
        internal const string Reason = "test rejected the library change delivery";
        private int rejecting;
        private int rejectionCount;

        internal bool Rejecting
        {
            get => Volatile.Read(ref rejecting) != 0;
            set => Volatile.Write(ref rejecting, value ? 1 : 0);
        }

        internal int RejectionCount => Volatile.Read(ref rejectionCount);

        public bool IsAvailable => inner.IsAvailable;

        public bool CanExecuteInline => inner.CanExecuteInline;

        public bool CheckAccess() => inner.CheckAccess();

        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            if (!Rejecting) { return inner.Schedule(action, priority); }
            Interlocked.Increment(ref rejectionCount);
            return new RejectedOperation();
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);

        private sealed class RejectedOperation : IUiScheduledOperation
        {
            public bool IsAccepted => false;

            public bool IsCompleted => true;

            public bool IsAborted => false;

            public string RejectionReason => Reason;

            public Task Completion => Task.CompletedTask;

            public void Abort()
            {
            }
        }
    }

}
