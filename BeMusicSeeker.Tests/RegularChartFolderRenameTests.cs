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
    public void StopAsync_DrainsInFlightFolderRename()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "rename-source");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE shutdown\r\n");
            var file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
            };
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
                action => action());
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

            owner.RenameChartFolderAsync(request, "rename-destination");
            Assert.IsTrue(suppressionEntered.Wait(TimeSpan.FromSeconds(10)));

            using var stopStarted = new ManualResetEventSlim();
            Task stopTask = StartLongRunningAsync(async delegate
            {
                stopStarted.Set();
                await owner.StopAsync();
            });
            Assert.IsTrue(stopStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));

            releaseSuppression.Set();
            stopTask.GetAwaiter().GetResult();
            Assert.IsFalse(Directory.Exists(sourceDirectory));
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "rename-destination")));
            Assert.AreEqual(1, Volatile.Read(ref appliedCount), "The mutation notification must apply once after the gate is released.");
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
            using RegularChartListOwner owner = CreateOwner(
                table,
                CreateWorkspaceForOwner(),
                action => action(),
                chartFileOperations: synchronizer);
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(synchronizer.TryEnter(out IDisposable incumbent));
            try
            {
                Task renameTask = owner.RenameChartFolderAsync(request, "busy-destination");
                renameTask.GetAwaiter().GetResult();
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
}
