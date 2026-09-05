using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartFolderRenameTests
{

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StopAsync_DrainsInFlightFolderRename(bool finalizationFails)
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            var file = CreateTestableBmsFile(chartPath);
            var dialogs = new FileDbReportRecordingDialogs();
            TestBmsLibrary library;
            if (finalizationFails)
            {
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, libraryRoot);
                library = new TestBmsLibrary(
                    songDbPath,
                    () => lr2Config,
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
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
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
            library.BMSFiles = [file];
            ChartFile chart = ChartFileProjection.FromBmsFile(file);
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
                    releaseSuppression.Wait(TimeSpan.FromSeconds(10));
                }
            };

            Task renameTask = owner.RenameChartFolderAsync(request, destinationFolder);
            Assert.IsTrue(suppressionEntered.Wait(TimeSpan.FromSeconds(10)));

            using var stopStarted = new ManualResetEventSlim();
            Task stopTask = StartLongRunningAsync(async delegate
            {
                stopStarted.Set();
                await owner.StopAsync();
            });
            Assert.IsTrue(stopStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));
            Assert.IsFalse(renameTask.IsCompleted, "The rename must still be in flight while shutdown begins draining it.");

            releaseSuppression.Set();
            stopTask.GetAwaiter().GetResult();
            if (finalizationFails)
            {
                Exception renameFailure = null;
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
        });
    }

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
            var file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
            };
            RenameChartFolderRequest request = CreateRenameRequest(file);
            var table = new MainChartListViewModel();
            int displayRefreshCount = 0;
            table.DisplayRefreshRequested += (_, _) => Interlocked.Increment(ref displayRefreshCount);
            var synchronizer = new ChartFileOperationSynchronizer();
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
                Exception renameFailure = null;
                try
                {
                    renameTask.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    renameFailure = exception;
                }
                Assert.IsNotNull(renameFailure, "The busy gate failure must remain observable on the rename task.");
                Assert.IsInstanceOfType(renameFailure, typeof(InvalidOperationException));
                Assert.AreEqual(1, dialogs.Messages.Count, "The failed rename must notify through the mutation dialog route.");
                Assert.IsTrue(Directory.Exists(sourceDirectory));
                Assert.IsFalse(Directory.Exists(Path.Combine(libraryRoot, "busy-destination")));
                Assert.AreEqual(0, Volatile.Read(ref displayRefreshCount));
            }
            finally
            {
                incumbent.Dispose();
                owner.StopAsync().GetAwaiter().GetResult();
            }
        });
    }


    [TestMethod]
    public void FolderRenames_SerializeMutationsWithoutWaitingForRefreshDrain()
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
            var firstFile = CreateTestableBmsFile(firstChartPath);
            var secondFile = CreateTestableBmsFile(secondChartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [firstFile, secondFile]
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
                }));
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Action catchUp;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                catchUp = pendingActions.Dequeue();
            }
            catchUp();
            actionQueued.Reset();

            Task firstRename = owner.RenameChartFolderAsync(firstRequest, "first-destination");
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Task secondRename = owner.RenameChartFolderAsync(secondRequest, "second-destination");
            Assert.IsTrue(firstRename.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(secondRename.Wait(TimeSpan.FromSeconds(10)));
            firstRename.GetAwaiter().GetResult();
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
            var file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
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
                }));
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Action catchUp;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                catchUp = pendingActions.Dequeue();
            }
            catchUp();
            actionQueued.Reset();

            Task renameTask = owner.RenameChartFolderAsync(request, "queued-destination");
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FolderRename_Lr2FinalizationFailureDoesNotApplySuccessRefresh(bool reporterThrows)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "workflow-source");
            string destinationDirectory = Path.Combine(libraryRoot, "PackFinalizationFailure");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectory, "chart.bms");
            string lr2RootPath = Path.Combine(libraryRoot, "LR2beta3");
            var dialogs = new FileDbReportRecordingDialogs();
            if (reporterThrows) dialogs.MessageFailure = new IOException("terminal report failed");
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            bool reportAfterRelease = false;
            bool modelLeaseReleased = false;
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE workflow finalization failure\r\n");
            try
            {
                var file = CreateTestableBmsFile(chartPath);
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, libraryRoot);
                var library = new TestBmsLibrary(
                    songDbPath,
                    () => lr2Config,
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
                    BMSFiles = [file]
                };
                dialogs.OnMessage = () =>
                {
                    bool gateReleased = gate.TryEnter(out IDisposable releasedGate);
                    reportAfterRelease = !activity.IsActive && gateReleased;
                    if (gateReleased) releasedGate.Dispose();
                    using LibraryFileMutationLease lease = library.TryBeginLibraryFileMutation("rename_report_probe");
                    modelLeaseReleased = lease != null;
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                var table = new MainChartListViewModel();
                int normalRefreshApplyCount = 0;
                table.DisplayRefreshRequested += (_, _) => { };
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
                Exception renameFailure = null;
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
                Assert.AreEqual(destinationChartPath, file.path);
                Assert.AreEqual(0, Volatile.Read(ref normalRefreshApplyCount));
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages, "Canonical reporting suppresses the lower receipt-backed dialog.");
                Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages[0].Icon);
                Assert.IsTrue(reportAfterRelease);
                Assert.IsTrue(modelLeaseReleased);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "forced durable finalization failure");
                StringAssert.Contains(renameFailure!.ToString(), "forced durable finalization failure");
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(chartPath));
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
