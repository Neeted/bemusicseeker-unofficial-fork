using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryFolderTreeViewModelTests
{
    [TestMethod]
    public void Constructor_RequiresLiveUiDispatcher()
    {
        Assert.ThrowsException<InvalidOperationException>(() => new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => null!)));
    }

    [TestMethod]
    public async Task DeferredRefresh_DropsRequestAfterDispatcherShutdownWithoutRetry()
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread dispatcherThread = new(() =>
        {
            try
            {
                Dispatcher shutdownDispatcher = Dispatcher.CurrentDispatcher;
                dispatcherReady.TrySetResult(shutdownDispatcher);
                shutdownDispatcher.InvokeShutdown();
            }
            catch (Exception exception)
            {
                dispatcherReady.TrySetException(exception);
            }
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Dispatcher shutdownDispatcher = await dispatcherReady.Task;
        dispatcherThread.Join();

        var owner = CreateTreeOwner(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => shutdownDispatcher));
        int refreshRequests = 0;
        owner.CacheRefreshRequested += (_, _) => refreshRequests++;
        owner.ScheduleDeferredRefresh(operationToken: 1);
        await owner.WaitForDeferredRefreshIdleAsync();
        Assert.AreEqual(0, refreshRequests);
    }

    [TestMethod]
    public async Task DeferredRefresh_DropsQueuedRequestWhenDispatcherShutsDown()
    {
        Dispatcher dispatcher = null!;
        LibraryFolderTreeViewModel owner = null!;
        var dispatcherReady = new TaskCompletionSource<(
            Dispatcher Dispatcher,
            LibraryFolderTreeViewModel Owner)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlocker = new ManualResetEventSlim();
        Thread dispatcherThread = new(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                owner = CreateTreeOwner(
                    _ => true,
                    _ => new ExplorerOpenResult(),
                    new WpfUiScheduler(() => dispatcher));
                dispatcherReady.TrySetResult((dispatcher, owner));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                dispatcherReady.TrySetException(exception);
            }
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        (dispatcher, owner) = await dispatcherReady.Task;

        int refreshRequests = 0;
        int refreshCompletions = 0;
        owner.CacheRefreshRequested += (_, _) => Interlocked.Increment(ref refreshRequests);
        owner.DeferredRefreshCompleted += (_, _) => Interlocked.Increment(ref refreshCompletions);
        dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            blockerEntered.TrySetResult(null);
            releaseBlocker.Wait();
        }));
        await blockerEntered.Task;

        var refreshOperationPosted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherHookEventHandler operationPosted = (_, args) =>
        {
            if (args.Operation.Priority == DispatcherPriority.Background)
            {
                refreshOperationPosted.TrySetResult(null);
            }
        };
        dispatcher.Hooks.OperationPosted += operationPosted;

        owner.ScheduleDeferredRefresh(operationToken: 1);
        Task refreshIdle = owner.WaitForDeferredRefreshIdleAsync();
        await refreshOperationPosted.Task;
        Assert.IsFalse(refreshIdle.IsCompleted);
        dispatcher.Hooks.OperationPosted -= operationPosted;
        dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)dispatcher.InvokeShutdown);
        releaseBlocker.Set();

        Assert.IsTrue(dispatcherThread.Join(TimeSpan.FromSeconds(5)));
        await refreshIdle;
        Assert.AreEqual(0, refreshRequests);
        Assert.AreEqual(0, refreshCompletions);
    }

    [TestMethod]
    public async Task DeferredRefresh_CoalescesToLatestOperationWithoutShellRetry()
    {
        Dispatcher dispatcher = null!;
        LibraryFolderTreeViewModel owner = null!;
        var dispatcherReady = new TaskCompletionSource<(
            Dispatcher Dispatcher,
            LibraryFolderTreeViewModel Owner)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlocker = new ManualResetEventSlim();
        var firstRefreshPosted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRefreshCompleted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread dispatcherThread = new(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                owner = CreateTreeOwner(
                    _ => true,
                    _ => new ExplorerOpenResult(),
                    new WpfUiScheduler(() => dispatcher));
                dispatcherReady.TrySetResult((dispatcher, owner));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                dispatcherReady.TrySetException(exception);
            }
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        (dispatcher, owner) = await dispatcherReady.Task;

        int refreshRequests = 0;
        int refreshCompletions = 0;
        long completedOperationToken = -1;
        owner.CacheRefreshRequested += (_, _) => Interlocked.Increment(ref refreshRequests);
        owner.DeferredRefreshCompleted += (_, args) =>
        {
            completedOperationToken = args.OperationToken;
            Interlocked.Increment(ref refreshCompletions);
            latestRefreshCompleted.TrySetResult(null);
        };

        dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            blockerEntered.TrySetResult(null);
            releaseBlocker.Wait();
        }));
        await blockerEntered.Task;

        DispatcherHookEventHandler operationPosted = (_, args) =>
        {
            if (args.Operation.Priority == DispatcherPriority.Background)
            {
                firstRefreshPosted.TrySetResult(null);
            }
        };
        dispatcher.Hooks.OperationPosted += operationPosted;

        try
        {
            owner.ScheduleDeferredRefresh(operationToken: 0);
            Task refreshIdle = owner.WaitForDeferredRefreshIdleAsync();
            await firstRefreshPosted.Task;
            owner.ScheduleDeferredRefresh(operationToken: 42);
            releaseBlocker.Set();

            await latestRefreshCompleted.Task;
            await refreshIdle;
            Assert.AreEqual(42, completedOperationToken);
            Assert.AreEqual(1, refreshCompletions);
            Assert.AreEqual(0, refreshRequests);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= operationPosted;
            releaseBlocker.Set();
            dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)dispatcher.InvokeShutdown);
            Assert.IsTrue(dispatcherThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task DeferredRefresh_StaleSourceRequestsShellReadmissionBeforeRetry()
    {
        Dispatcher dispatcher = null!;
        LibraryFolderTreeViewModel owner = null!;
        var dispatcherReady = new TaskCompletionSource<(
            Dispatcher Dispatcher,
            LibraryFolderTreeViewModel Owner)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlocker = new ManualResetEventSlim();
        var firstRefreshPosted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readmissionRequested = new TaskCompletionSource<
            LibraryFolderTreeRefreshRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReadmission = new ManualResetEventSlim();
        var latestRefreshCompleted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PerformanceInteraction interaction = PerformanceInteraction.Start("startup", 7L);
        PerformanceInteraction latestInteraction = PerformanceInteraction.Start("startup", 42L);
        Thread dispatcherThread = new(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                owner = CreateTreeOwner(
                    _ => true,
                    _ => new ExplorerOpenResult(),
                    new WpfUiScheduler(() => dispatcher));
                dispatcherReady.TrySetResult((dispatcher, owner));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                dispatcherReady.TrySetException(exception);
            }
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        (dispatcher, owner) = await dispatcherReady.Task;

        int refreshCompletions = 0;
        long completedOperationToken = -1;
        owner.CacheRefreshRequested += (_, args) =>
        {
            owner.ScheduleDeferredRefresh(args.OperationToken, args.Interaction);
            readmissionRequested.TrySetResult(args);
            if (!releaseReadmission.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Deferred refresh continuation was not released.");
            }
        };
        owner.DeferredRefreshCompleted += (_, args) =>
        {
            completedOperationToken = args.OperationToken;
            Interlocked.Increment(ref refreshCompletions);
            latestRefreshCompleted.TrySetResult(null);
        };

        _ = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            blockerEntered.TrySetResult(null);
            releaseBlocker.Wait();
        }));
        await blockerEntered.Task;

        DispatcherHookEventHandler operationPosted = (_, args) =>
        {
            if (args.Operation.Priority == DispatcherPriority.Background)
            {
                firstRefreshPosted.TrySetResult(null);
            }
        };
        dispatcher.Hooks.OperationPosted += operationPosted;

        try
        {
            owner.ScheduleDeferredRefresh(operationToken: 7, interaction: interaction);
            Task refreshIdle = owner.WaitForDeferredRefreshIdleAsync();
            await firstRefreshPosted.Task;
            owner.ScheduleDeferredRefresh(operationToken: 42, interaction: latestInteraction);
            typeof(LibraryFolderTreeViewModel)
                .GetMethod("MarkRefreshRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, null);
            releaseBlocker.Set();

            LibraryFolderTreeRefreshRequestedEventArgs request = await readmissionRequested.Task;
            Assert.AreEqual(
                LibraryFolderTreeRefreshRequestOrigin.DeferredContinuation,
                request.Origin);
            Assert.AreEqual(42, request.OperationToken);
            Assert.AreEqual(latestInteraction.InteractionId, request.Interaction.InteractionId);
            Assert.IsFalse(refreshIdle.IsCompleted);
            Assert.AreSame(refreshIdle, owner.WaitForDeferredRefreshIdleAsync());
            releaseReadmission.Set();

            await latestRefreshCompleted.Task;
            await refreshIdle;
            Assert.AreEqual(42, completedOperationToken);
            Assert.AreEqual(1, refreshCompletions);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= operationPosted;
            releaseReadmission.Set();
            releaseBlocker.Set();
            _ = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)dispatcher.InvokeShutdown);
            Assert.IsTrue(dispatcherThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task DeferredRefresh_EmitsAggregateStagesForOneInteraction()
    {
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_LibraryFolderTree_Stages_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            var stages = new ConcurrentQueue<string>();
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = []
            };
            library.SearchTargets = [tempRootPath];
            var owner = CreateTreeOwner(
                _ => true,
                _ => new ExplorerOpenResult(),
                new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                stages.Enqueue,
                stages.Enqueue);
            owner.AttachLibrary(library);
            owner.InvalidateLibraryFolderCache();

            PerformanceInteraction interaction = PerformanceInteraction.Start("startup", 17L);
            owner.ScheduleDeferredRefresh(42L, interaction);
            await owner.WaitForDeferredRefreshIdleAsync();

            string[] expectedStages =
            [
                "request_accepted",
                "worker_queued",
                "worker_started",
                "model_reader_wait_start",
                "model_reader_wait_end",
                "path_snapshot_complete",
                "cache_build_complete",
                "ui_queued",
                "ui_started",
                "ui_applied"
            ];
            string[] actual = stages.ToArray();
            foreach (string expectedStage in expectedStages)
            {
                Assert.IsTrue(
                    actual.Any(value => value.IndexOf("stage=" + expectedStage, StringComparison.Ordinal) >= 0),
                    "Missing aggregate stage: " + expectedStage);
            }
            Assert.IsTrue(actual
                .Where(value => value.IndexOf("stage=", StringComparison.Ordinal) >= 0)
                .All(value => value.IndexOf(
                "interactionId=" + interaction.InteractionId,
                StringComparison.Ordinal) >= 0));

            int entryCountBeforeIndependentRefresh = stages.Count;
            owner.InvalidateLibraryFolderCache();
            owner.ScheduleDeferredRefresh(43L);
            await owner.WaitForDeferredRefreshIdleAsync();
            string[] independentRefreshMarkers = stages
                .Skip(entryCountBeforeIndependentRefresh)
                .Where(value => value.IndexOf("stage=", StringComparison.Ordinal) >= 0)
                .ToArray();
            Assert.IsFalse(independentRefreshMarkers.Any(value => value.IndexOf(
                "interactionId=" + interaction.InteractionId,
                StringComparison.Ordinal) >= 0));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DeferredRefreshUsesConfiguredOwnerScheduler()
    {
        string queuedName = null!;
        Func<Task> queuedWork = null!;
        var owner = CreateTreeOwner(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        owner.ConfigureDeferredRefreshScheduler((name, work) =>
        {
            queuedName = name;
            queuedWork = work;
            return true;
        });

        owner.ScheduleDeferredRefresh(operationToken: 42);

        Assert.AreEqual("library_folder_tree_refresh", queuedName);
        Assert.IsNotNull(queuedWork);
    }

    [TestMethod]
    public void DeferredRefreshFailsFastWithoutOwnerScheduler()
    {
        var owner = new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.ScheduleDeferredRefresh(operationToken: 42));
    }

    [TestMethod]
    public async Task AttachedLibrary_ExposesSortedDistinctFoldersAndInvalidatesAfterSearchRootChange()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LibraryFolderTree_" + Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        string firstRoot = Path.Combine(tempRootPath, "Zeta");
        string secondRoot = Path.Combine(tempRootPath, "Alpha");
        string replacementRoot = Path.Combine(tempRootPath, "Beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(replacementRoot);
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            var library = new TestBmsLibrary(songDbPath);
            library.SearchTargets = [firstRoot, secondRoot, firstRoot];
            var firstRefreshApplied = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRefreshApplied = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = CreateTreeOwner(
                _ => true,
                _ => new ExplorerOpenResult(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            int parentFolderPropertyChanges = 0;
            int cacheRefreshRequests = 0;
            owner.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LibraryFolderTreeViewModel.BMSParentFolderList))
                {
                    parentFolderPropertyChanges++;
                    if (parentFolderPropertyChanges == 1)
                    {
                        firstRefreshApplied.TrySetResult(null);
                    }
                    else
                    {
                        secondRefreshApplied.TrySetResult(null);
                    }
                }
            };
            owner.CacheRefreshRequested += (_, args) =>
            {
                cacheRefreshRequests++;
                owner.ScheduleDeferredRefresh(args.OperationToken, args.Interaction);
            };

            owner.AttachLibrary(library);
            Assert.IsTrue(owner.IsLibraryAttached);
            await firstRefreshApplied.Task;
            CollectionAssert.AreEqual(
                new[] { secondRoot, firstRoot },
                owner.BMSParentFolderList.ToArray());
            Assert.AreEqual(library.IsWriteLockHeldInitializeBMSFiles, owner.IsWriteLockHeldInitializeBMSFiles);
            cacheRefreshRequests = 0;

            int propertyChangesBeforeInvalidation = parentFolderPropertyChanges;
            owner.ApplySearchTargets([replacementRoot, secondRoot]);
            CollectionAssert.AreEqual(
                new[] { replacementRoot, secondRoot },
                library.SearchTargets.ToArray());
            owner.InvalidateLibraryFolderCache();

            await secondRefreshApplied.Task;
            CollectionAssert.AreEqual(
                new[] { secondRoot, replacementRoot },
                owner.BMSParentFolderList.ToArray());
            Assert.IsTrue(parentFolderPropertyChanges > propertyChangesBeforeInvalidation);
            Assert.AreEqual(1, cacheRefreshRequests);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DetachedLibrary_SearchRootRuntimeIsNoOp()
    {
        var owner = CreateTreeOwner(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));
        int refreshRequests = 0;
        owner.CacheRefreshRequested += (_, _) => refreshRequests++;

        Assert.IsFalse(owner.IsLibraryAttached);
        owner.ApplySearchTargets(["detached"]);
        owner.InvalidateLibraryFolderCache();

        Assert.AreEqual(0, refreshRequests);
    }

    [TestMethod]
    public void AttachedLibrary_OwnedChartQueryPreservesPathBoundariesForBmsAndBmson()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LibraryFolderTree_OwnedChart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
            string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
            string[] bmsValues = new string[29];
            bmsValues[0] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            bmsValues[7] = bmsPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [BMSFile.FromSongTableRawValues(bmsValues)],
                BmsonSongs =
                [
                    new LR2SongDBExtended.bmson_song
                    {
                        path = bmsonPath,
                        md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                    }
                ]
            };
            var owner = CreateTreeOwner(
                _ => true,
                _ => new ExplorerOpenResult(),
                new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

            owner.AttachLibrary(library);

            Assert.IsTrue(owner.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bms")));
            Assert.IsTrue(owner.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bmson")));
            Assert.IsTrue(owner.HasOwnedChartUnderRealPath("C:\\Installed"));
            Assert.IsFalse(owner.HasOwnedChartUnderRealPath("C:\\Install"));
            Assert.IsFalse(owner.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
            Assert.IsFalse(owner.HasOwnedChartUnderRealPath(null));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void OpenFolderInExplorer_ValidatesBeforeOpeningExactlyOnce()
    {
        List<string> validationPaths = [];
        List<string> openedPaths = [];
        var owner = CreateTreeOwner(
            path =>
            {
                validationPaths.Add(path);
                return true;
            },
            path =>
            {
                openedPaths.Add(path);
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.OpenedDirectory,
                    RequestedPath = path,
                    OpenedPath = path
                };
            },
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

        owner.OpenFolderInExplorer("C:\\Library");

        CollectionAssert.AreEqual(new[] { "C:\\Library" }, validationPaths);
        CollectionAssert.AreEqual(new[] { "C:\\Library" }, openedPaths);
    }

    [TestMethod]
    public void OpenFolderInExplorer_MissingFolderDoesNotInvokeExplorer()
    {
        var owner = CreateTreeOwner(
            _ => false,
            _ => throw new AssertFailedException("Explorer should not be invoked for a missing folder."),
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

        owner.OpenFolderInExplorer("C:\\Missing");
    }

    [TestMethod]
    public void OpenFolderInExplorer_PreservesFailedExplorerResultWithoutFallback()
    {
        int openCount = 0;
        var owner = CreateTreeOwner(
            _ => true,
            path =>
            {
                openCount++;
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.Failed,
                    RequestedPath = path,
                    FailureReason = "shell_failed"
                };
            },
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

        owner.OpenFolderInExplorer("C:\\Library");

        Assert.AreEqual(1, openCount);
    }

    private static LibraryFolderTreeViewModel CreateTreeOwner(
        Func<string, bool> directoryExists,
        Func<string, ExplorerOpenResult> openDirectory,
        IUiScheduler uiScheduler,
        Action<string>? log = null,
        Action<string>? logWarning = null)
    {
        var owner = new LibraryFolderTreeViewModel(
            directoryExists,
            openDirectory,
            uiScheduler,
            log,
            logWarning);
        owner.ConfigureDeferredRefreshScheduler((_, work) =>
        {
            Task ignored = Task.Run(work);
            return true;
        });
        return owner;
    }
}
