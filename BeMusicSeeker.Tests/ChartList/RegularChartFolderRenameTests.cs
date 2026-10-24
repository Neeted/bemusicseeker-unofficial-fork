using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>通常一覧の実folder rename、共有受付、失敗通知と停止時の収束を検証します。</summary>
/// <remarks>実FS・SQLite・LR2終端を用い、物理成功後の終端失敗と表示通知の分担を確認します。compiled編集入口はMainWindowの混在選択caseが分担します。</remarks>
[TestClass]
public sealed class RegularChartFolderRenameTests
{

    /// <summary>実libraryの管理領域交差は共有Pの競合を再生停止前に拒否し、物理・DBを保持して予約せず、解放後の新しい明示要求だけを成功させることを確認します。</summary>
    [TestMethod]
    public void FolderRename_ManagedPlaylistBusyRejectsBeforePlaybackStopAndFreshRequestSucceeds()
    {

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
            var library = new TestBmsLibrary(songDbPath, null, null, null, new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => null!), () => options)
            { BmsCharts = [chart], BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs([chart]);
            var playback = new NoOpChartMutationPlaybackPort();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
                chartFileOperations: library.OperationAdmission, mutationDialogs: new FileDbReportRecordingDialogs(), mutationPlayback: playback);
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable held));
            try
            {
                InvalidOperationException busy = Assert.ThrowsException<InvalidOperationException>(() =>
                    owner.RenameChartFolderAsync(CreateRenameRequest(chart), "Moved").GetAwaiter().GetResult());
                StringAssert.Contains(busy.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
                Assert.AreEqual(0, playback.StopCount);
                Assert.IsTrue(File.Exists(path));
                Assert.IsFalse(Directory.Exists(destination));
                Assert.IsFalse(library.OperationAdmission.IsActive);
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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

    /// <summary>終端抑止解除をgateで保持した実renameを停止が待ち、成功と永続終端失敗の両方で物理変更・通知回数・元失敗を回収して途中終了を検出します。</summary>
    [TestMethod]
    // falseは正常終端、trueはfolder INSERT triggerによる永続終端失敗です。
    [DataRow(false)]
    [DataRow(true)]
    public void StopAsync_DrainsInFlightFolderRename(bool finalizationFails)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "rename-source");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string destinationFolder = finalizationFails
                ? "PackFinalizationFailure"
                : "rename-destination";
            string destinationDirectory = Path.Combine(libraryRoot, destinationFolder);
            string destinationChartPath = Path.Combine(destinationDirectory, "chart.bms");
            string lr2RootPath = Path.Combine(libraryRoot, "LR2beta3");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE shutdown\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var dialogs = new FileDbReportRecordingDialogs();
            TestBmsLibrary library;
            if (finalizationFails)
            {
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, libraryRoot);
                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                library = new TestBmsLibrary(
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    null,
                    dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    });
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }
            }
            else
            {
                library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    null,
                    dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            }
            library.BmsCharts = [file];
            ChartFile chart = (file);
            var target = new ChartOperationTarget(
                chart,
                playlistEntry: null,
                ChartOperationSourceScope.Library,
                isOwned: true,
                isPending: false,
                isPlaylistMissing: false,
                ChartOperationCapabilities.MoveInLibrary);
            Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));

            int appliedCount = 0;
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                chartFileOperations: library.OperationAdmission,
                mutationDialogs: dialogs);
            owner.AttachNormalLibraryRefreshSource(library);
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.HasRefreshNotification)
                {
                    Interlocked.Increment(ref appliedCount);
                }
            };
            using var suppressionEntered = new ManualResetEventSlim();
            using var releaseSuppression = new ManualResetEventSlim();
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (!args.IsSuppressed)
                {
                    suppressionEntered.Set();
                    releaseSuppression.Wait();
                }
            };

            Task renameTask = owner.RenameChartFolderAsync(request, destinationFolder);
            Task? stopTask = null;
            try
            {
                suppressionEntered.Wait();

                using var stopStarted = new ManualResetEventSlim();
                stopTask = StartLongRunningAsync(async delegate
                {
                    stopStarted.Set();
                    await owner.StopAsync();
                });
                stopStarted.Wait();
                Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));
                Assert.IsFalse(renameTask.IsCompleted, "The rename must still be in flight while shutdown begins draining it.");

                releaseSuppression.Set();
                stopTask.GetAwaiter().GetResult();
                if (finalizationFails)
                {
                    Exception? renameFailure = null;
                    try
                    {
                        renameTask.GetAwaiter().GetResult();
                    }
                    catch (Exception exception)
                    {
                        renameFailure = exception;
                    }
                    Assert.IsNotNull(renameFailure, "The in-flight durable finalization failure must remain observable on the rename task.");
                    StringAssert.Contains(renameFailure!.ToString(), "forced durable finalization failure");
                }
                else
                {
                    renameTask.GetAwaiter().GetResult();
                }
                Assert.IsFalse(Directory.Exists(sourceDirectory));
                Assert.IsTrue(Directory.Exists(destinationDirectory));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(
                    finalizationFails ? 0 : 1,
                    Volatile.Read(ref appliedCount),
                    finalizationFails
                        ? "A durable finalization failure must not apply a success refresh."
                        : "The mutation notification must apply once after the gate is released.");
                Assert.AreEqual(finalizationFails ? 1 : 0, dialogs.Messages.Count);
            }
            finally
            {
                releaseSuppression.Set();
                try
                {
                    renameTask.GetAwaiter().GetResult();
                }
                catch when (finalizationFails)
                {
                    // 本文が元の永続終端失敗を検査済みで、ここでは開始Taskを回収します。
                }
                finally
                {
                    stopTask?.GetAwaiter().GetResult();
                }
            }
        });
    }

    /// <summary>共有LがBusyなら案内一回で物理変更と表示更新を行わず、解放だけでは再実行せず次の明示renameだけを成功させることを確認します。</summary>
    [TestMethod]
    public void FolderRename_FailsFastWhenSharedChartFileGateIsBusyWithoutRefresh()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "busy-source");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE busy\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
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

    /// <summary>管理領域外のrenameはplaylist通信とP受付が稼働中でも実ファイルを移し、無関係なPによる操作拒否を検出します。</summary>
    [TestMethod]
    public void FolderRename_RemainsAllowedWhilePlaylistCommunicationIsActive()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "playlist-communication-source");
            string destinationDirectory = Path.Combine(libraryRoot, "playlist-communication-destination");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE playlist communication\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [file]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
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


    /// <summary>最初のrename終端後は未処理の表示更新を待たず次のrenameを実行し、二つの物理移動と通知集約を確認して描画待ちによる受付停滞を検出します。</summary>
    [TestMethod]
    public void ExplicitFolderRenames_AfterFirstTerminalDoNotWaitForDisplayRefreshDrain()
    {
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
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [firstFile, secondFile]
            };
            RenameChartFolderRequest firstRequest = CreateRenameRequest(firstFile);
            RenameChartFolderRequest secondRequest = CreateRenameRequest(secondFile);
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

            Task firstRename = owner.RenameChartFolderAsync(firstRequest, "first-destination");
            actionQueued.Wait();
            firstRename.GetAwaiter().GetResult();
            Task secondRename = owner.RenameChartFolderAsync(secondRequest, "second-destination");
            secondRename.GetAwaiter().GetResult();

            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "first-destination")));
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "second-destination")));
            Action coalescedRefresh;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                coalescedRefresh = pendingActions.Dequeue();
            }
            coalescedRefresh();
            owner.StopAsync().GetAwaiter().GetResult();
        });
    }


    /// <summary>renameの表示更新をexecution laneに保留すると停止は未完了となり、予約処理後にrenameと停止を回収して未処理通知を残した終了を検出します。</summary>
    [TestMethod]
    public void FolderRename_StopAsyncDrainsQueuedRefresh()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "queued-source");
            string chartPath = Path.Combine(sourceDirectory, "queued.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE queued\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
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
            Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));

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

    /// <summary>実LR2終端SQL失敗では物理・譜面DBの成功分を保持して成功更新を出さず、通知失敗の有無によらず元SQL失敗と一回通知、作業解放後も保持するLを確認します。</summary>
    [TestMethod]
    // falseは案内成功、trueは案内経路もIOExceptionを投げる条件です。
    [DataRow(false)]
    [DataRow(true)]
    public void FolderRename_Lr2FinalizationFailureDoesNotApplySuccessRefresh(bool reporterThrows)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "workflow-source");
            string destinationDirectory = Path.Combine(libraryRoot, "PackFinalizationFailure");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectory, "chart.bms");
            string lr2RootPath = Path.Combine(libraryRoot, "LR2beta3");
            var dialogs = new FileDbReportRecordingDialogs();
            if (reporterThrows)
            {
                dialogs.MessageFailure = new IOException("terminal report failed");
            }

            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            bool reportAfterRelease = false;
            bool logicalAdmissionHeldDuringNotification = false;
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE workflow finalization failure\r\n");
            try
            {
                ChartFile file = CreateTestableBmsFile(chartPath);
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, libraryRoot);
                var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
                var library = new TestBmsLibrary(
                    songDbPath,
                    lr2ConfigOwner,
                    null,
                    null,
                    dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    })
                {
                    BmsCharts = [file]
                };
                gate = library.OperationAdmission;
                dialogs.OnMessage = () =>
                {
                    bool gateReleased = gate.TryEnter(out IDisposable releasedGate);
                    reportAfterRelease = !activity.IsActive && !gateReleased;
                    if (gateReleased)
                    {
                        releasedGate.Dispose();
                    }

                    logicalAdmissionHeldDuringNotification = library.OperationAdmission.IsActive;
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                var table = new MainChartListViewModel();
                int normalRefreshApplyCount = 0;
                using RegularChartListOwner owner = CreateOwner(
                    table,
                    CreateWorkspaceForOwner(),
                    action => action(),
                    chartFileOperations: gate,
                    mutationDialogs: dialogs,
                    chartMutationActivity: activity);
                owner.AttachNormalLibraryRefreshSource(library);
                owner.NormalLibraryRefreshApplied += (_, args) =>
                {
                    if (args.NotificationBatch.HasRefreshNotification)
                    {
                        Interlocked.Increment(ref normalRefreshApplyCount);
                    }
                };
                RenameChartFolderRequest request = CreateRenameRequest(file);

                Task renameTask = owner.RenameChartFolderAsync(request, "PackFinalizationFailure");
                Exception? renameFailure = null;
                try
                {
                    renameTask.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    renameFailure = exception;
                }
                Assert.IsNotNull(renameFailure, "The durable finalization failure must remain observable on the rename task.");

                Assert.IsFalse(Directory.Exists(sourceDirectory));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(chartPath, file.Path);
                Assert.AreEqual(destinationChartPath, library.BmsCharts[0].Path);
                Assert.AreEqual(0, Volatile.Read(ref normalRefreshApplyCount));
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages, "Canonical reporting suppresses the lower receipt-backed dialog.");
                Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages[0].Icon);
                Assert.IsTrue(reportAfterRelease);
                Assert.IsTrue(logicalAdmissionHeldDuringNotification);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "forced durable finalization failure");
                StringAssert.Contains(renameFailure!.ToString(), "forced durable finalization failure");
                using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                    Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(chartPath));
                }
                owner.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                if (Directory.Exists(Path.Combine(libraryRoot, "LR2beta3")))
                {
                    Directory.Delete(Path.Combine(libraryRoot, "LR2beta3"), recursive: true);
                }
                if (Directory.Exists(sourceDirectory))
                {
                    Directory.Delete(sourceDirectory, recursive: true);
                }
                if (Directory.Exists(destinationDirectory))
                {
                    Directory.Delete(destinationDirectory, recursive: true);
                }
            }
        });
    }
}
