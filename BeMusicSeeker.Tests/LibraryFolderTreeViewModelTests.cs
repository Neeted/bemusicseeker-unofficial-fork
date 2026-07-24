using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
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
            () => null));
    }

    [TestMethod]
    public void DeferredRefresh_DropsRequestAfterDispatcherShutdownWithoutRetry()
    {
        Dispatcher shutdownDispatcher = null!;
        using var dispatcherReady = new ManualResetEventSlim();
        Thread dispatcherThread = new(() =>
        {
            shutdownDispatcher = Dispatcher.CurrentDispatcher;
            dispatcherReady.Set();
            shutdownDispatcher.InvokeShutdown();
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Assert.IsTrue(dispatcherReady.Wait(TimeSpan.FromSeconds(5)));
        dispatcherThread.Join();

        var owner = new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            () => shutdownDispatcher);
        int refreshRequests = 0;
        owner.CacheRefreshRequested += (_, _) => refreshRequests++;
        FieldInfo queuedField = typeof(LibraryFolderTreeViewModel)
            .GetField("deferredRefreshQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;

        owner.ScheduleDeferredRefresh(operationToken: 1);

        Assert.IsTrue(SpinWait.SpinUntil(
            () => !(bool)queuedField.GetValue(owner)!,
            TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, refreshRequests);
    }

    [TestMethod]
    public void DeferredRefresh_DropsQueuedRequestWhenDispatcherShutsDown()
    {
        Dispatcher dispatcher = null!;
        LibraryFolderTreeViewModel owner = null!;
        using var dispatcherReady = new ManualResetEventSlim();
        using var blockerEntered = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        Thread dispatcherThread = new(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            owner = new LibraryFolderTreeViewModel(
                _ => true,
                _ => new ExplorerOpenResult(),
                () => dispatcher);
            dispatcherReady.Set();
            Dispatcher.Run();
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Assert.IsTrue(dispatcherReady.Wait(TimeSpan.FromSeconds(5)));

        int refreshRequests = 0;
        int refreshCompletions = 0;
        owner.CacheRefreshRequested += (_, _) => Interlocked.Increment(ref refreshRequests);
        owner.DeferredRefreshCompleted += (_, _) => Interlocked.Increment(ref refreshCompletions);
        FieldInfo queuedField = typeof(LibraryFolderTreeViewModel)
            .GetField("deferredRefreshQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;

        dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            blockerEntered.Set();
            releaseBlocker.Wait();
        }));
        Assert.IsTrue(blockerEntered.Wait(TimeSpan.FromSeconds(5)));

        using var refreshOperationPosted = new ManualResetEventSlim();
        DispatcherHookEventHandler operationPosted = (_, args) =>
        {
            if (args.Operation.Priority == DispatcherPriority.Background)
            {
                refreshOperationPosted.Set();
            }
        };
        dispatcher.Hooks.OperationPosted += operationPosted;

        owner.ScheduleDeferredRefresh(operationToken: 1);
        Assert.IsTrue(refreshOperationPosted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue((bool)queuedField.GetValue(owner)!);
        dispatcher.Hooks.OperationPosted -= operationPosted;
        dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)dispatcher.InvokeShutdown);
        releaseBlocker.Set();

        Assert.IsTrue(dispatcherThread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsFalse((bool)queuedField.GetValue(owner)!);
        Assert.AreEqual(0, refreshRequests);
        Assert.AreEqual(0, refreshCompletions);
    }

    [TestMethod]
    public void AttachedLibrary_ExposesSortedDistinctFoldersAndInvalidatesAfterSearchRootChange()
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

            var library = new BMSLibrary(songDbPath);
            library.SearchTargets = [firstRoot, secondRoot, firstRoot];
            var owner = new LibraryFolderTreeViewModel(
                _ => true,
                _ => new ExplorerOpenResult(),
                () => Dispatcher.CurrentDispatcher);
            int parentFolderPropertyChanges = 0;
            int cacheRefreshRequests = 0;
            owner.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LibraryFolderTreeViewModel.BMSParentFolderList))
                {
                    parentFolderPropertyChanges++;
                }
            };
            owner.CacheRefreshRequested += (_, _) => cacheRefreshRequests++;

            owner.AttachLibrary(library);
            Assert.IsTrue(owner.IsLibraryAttached);
            Assert.AreEqual(0, parentFolderPropertyChanges);
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

            CollectionAssert.AreEqual(
                new[] { secondRoot, replacementRoot },
                owner.BMSParentFolderList.ToArray());
            Assert.AreEqual(propertyChangesBeforeInvalidation, parentFolderPropertyChanges);
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
        var owner = new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            () => Dispatcher.CurrentDispatcher);
        int refreshRequests = 0;
        owner.CacheRefreshRequested += (_, _) => refreshRequests++;

        Assert.IsFalse(owner.IsLibraryAttached);
        owner.ApplySearchTargets(["detached"]);
        owner.InvalidateLibraryFolderCache();

        Assert.AreEqual(0, refreshRequests);
    }

    [TestMethod]
    public void OpenFolderInExplorer_ValidatesBeforeOpeningExactlyOnce()
    {
        List<string> validationPaths = [];
        List<string> openedPaths = [];
        var owner = new LibraryFolderTreeViewModel(
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
            () => Dispatcher.CurrentDispatcher);

        owner.OpenFolderInExplorer("C:\\Library");

        CollectionAssert.AreEqual(new[] { "C:\\Library" }, validationPaths);
        CollectionAssert.AreEqual(new[] { "C:\\Library" }, openedPaths);
    }

    [TestMethod]
    public void OpenFolderInExplorer_MissingFolderDoesNotInvokeExplorer()
    {
        var owner = new LibraryFolderTreeViewModel(
            _ => false,
            _ => throw new AssertFailedException("Explorer should not be invoked for a missing folder."),
            () => Dispatcher.CurrentDispatcher);

        owner.OpenFolderInExplorer("C:\\Missing");
    }

    [TestMethod]
    public void OpenFolderInExplorer_PreservesFailedExplorerResultWithoutFallback()
    {
        int openCount = 0;
        var owner = new LibraryFolderTreeViewModel(
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
            () => Dispatcher.CurrentDispatcher);

        owner.OpenFolderInExplorer("C:\\Library");

        Assert.AreEqual(1, openCount);
    }
}
