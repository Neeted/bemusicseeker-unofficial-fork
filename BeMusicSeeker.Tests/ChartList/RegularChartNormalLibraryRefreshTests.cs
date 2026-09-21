using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
public sealed class RegularChartNormalLibraryRefreshTests
{

    [TestMethod]
    public void AttachNormalLibraryRefreshSource_CatchesUpOnceAndSuppressesDuplicate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\catch-up.bms")];
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            RegularMaterializedChartListApplyResult materialized = owner.TryApplyMaterialized(
                CreateMaterializedApplyRequest(
                [
                    LibraryChartRow.FromChartFile(CreateSourceRow("Old", "old.bms").Chart)
                ]));
            Assert.IsTrue(materialized.WasCommitted);
            Assert.IsTrue(owner.HasFolderRows);
            var applied = new List<NormalLibraryRefreshAppliedEventArgs>();
            owner.NormalLibraryRefreshApplied += (_, args) => applied.Add(args);
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);

                Assert.AreEqual(1, applied.Count);
                Assert.AreEqual("normal_library_refresh", applied[0].Reason);
                Assert.IsTrue(applied[0].NotificationBatch.NotifiesBmsFiles);
                Assert.IsTrue(applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsTrue(owner.SourceGeneration > 0);
                Assert.AreEqual(
                    applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged),
                    owner.WarningGeneration > 0);
                Assert.AreEqual(
                    applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged)
                        && !applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged),
                    owner.InstallDestinationGeneration > 0);
                Assert.IsFalse(owner.HasFolderRows);
                Assert.AreEqual(1, applied.Count);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_AppliesPublishedStorageReplacement()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            using var applied = new ManualResetEventSlim();
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.NotifiesBmsFiles)
                {
                    Interlocked.Increment(ref appliedCount);
                    applied.Set();
                }
            };
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\published.bms")];

                Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(10)));
                Assert.AreEqual(1, Volatile.Read(ref appliedCount));
                Assert.AreEqual(1, Volatile.Read(ref appliedCount));
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void StopAsync_DrainsInFlightNormalLibraryRefreshApplication()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            using var applyEntered = new ManualResetEventSlim();
            using var releaseApply = new ManualResetEventSlim();
            using var stopStarted = new ManualResetEventSlim();
            using var stopCompleted = new ManualResetEventSlim();
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (!args.NotificationBatch.NotifiesBmsFiles)
                {
                    return;
                }
                Interlocked.Increment(ref appliedCount);
                applyEntered.Set();
                Assert.IsTrue(releaseApply.Wait(TimeSpan.FromSeconds(10)));
            };

            Task mutationTask = StartLongRunning(() =>
            {
                library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\in-flight.bms")];
            });
            Assert.IsTrue(applyEntered.Wait(TimeSpan.FromSeconds(10)));

            Task stopTask = StartLongRunningAsync(async delegate
            {
                stopStarted.Set();
                await owner.StopAsync();
                stopCompleted.Set();
            });
            Assert.IsTrue(stopStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(stopCompleted.IsSet);

            releaseApply.Set();
            Task.WaitAll(mutationTask, stopTask);
            Assert.AreEqual(1, Volatile.Read(ref appliedCount));
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_InvalidatesMaintenanceDependency()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "maintenance.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n");
            TestableBmsFile file = CreateTestableBmsFile(chartPath);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            });
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
            };
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            long maintenanceGeneration = owner.MaintenanceGeneration;
            using var applied = new ManualResetEventSlim();
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
                {
                    applied.Set();
                }
            };
            try
            {
                MaintenanceWorkflowResult result = library.RescanResourceHealthCharts(
                    [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

                Assert.IsTrue(result.HasUpdates);
                Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(10)));
                Assert.AreEqual(maintenanceGeneration + 1, owner.MaintenanceGeneration);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_MarshalsApplyToExecutionLane()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\queued.bms")];
            var pendingActions = new Queue<Action>();
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(pendingActions.Enqueue));
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, _) => appliedCount++;
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);

                Assert.AreEqual(1, pendingActions.Count);
                Assert.AreEqual(0, appliedCount);
                Assert.AreEqual(0L, owner.SourceGeneration);

                pendingActions.Dequeue()();

                Assert.AreEqual(1, appliedCount);
                Assert.IsTrue(owner.SourceGeneration > 0);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_DoesNotWaitForUiWhileCatalogWriterHeld()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            var uiScheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
            using var notificationPublished = new ManualResetEventSlim();
            using var applied = new ManualResetEventSlim();

            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                uiScheduler);
            owner.AttachNormalLibraryRefreshSource(library);
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.NotifiesBmsFiles)
                {
                    applied.Set();
                }
            };

            ReaderWriterLockSlimWrapper catalogWriteGate = GetCatalogStorageRowsWriteGate(library);
            Task producer;
            bool notificationPublishedWhileWriterHeld;
            bool uiReachedCatalogReader;
            using (catalogWriteGate.GetWriterGuard())
            {
                producer = StartLongRunning(() =>
                {
                    PublishNormalLibraryRefreshResetNotification(
                        library,
                        notifiesBmsFiles: true,
                        notifiesBmsonSongs: false);
                    notificationPublished.Set();
                });
                notificationPublishedWhileWriterHeld =
                    notificationPublished.Wait(TimeSpan.FromSeconds(10));
                uiReachedCatalogReader = SpinWait.SpinUntil(
                    () => catalogWriteGate.WaitingReadCount > 0,
                    TimeSpan.FromSeconds(10));
            }
            try
            {
                Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(10)));
                Assert.IsTrue(
                    notificationPublishedWhileWriterHeld,
                    "The producer waited synchronously for the UI lane while a catalog writer was held.");
                Assert.IsTrue(
                    uiReachedCatalogReader,
                    "The dedicated UI lane did not reach the catalog snapshot reader.");
                producer.GetAwaiter().GetResult();
                Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(10)));
                owner.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void StopAsync_QueuedNormalLibraryRefreshBecomesNoOp()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\queued-stop.bms")];
            var pendingActions = new Queue<Action>();
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(pendingActions.Enqueue));
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, _) => appliedCount++;

            owner.AttachNormalLibraryRefreshSource(library);
            Assert.AreEqual(1, pendingActions.Count);

            Task stopTask = owner.StopAsync();
            Assert.IsFalse(stopTask.IsCompleted);
            pendingActions.Dequeue()();
            stopTask.GetAwaiter().GetResult();

            Assert.AreEqual(0, appliedCount);
            Assert.AreEqual(0L, owner.SourceGeneration);
        });
    }


    [TestMethod]
    public void StopAsync_AcceptedButAbortedNormalLibraryRefreshDoesNotHang()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [CreateTestableBmsFile("C:\\Charts\\aborted-refresh.bms")]
            };
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new AbortingUiScheduler());

            owner.AttachNormalLibraryRefreshSource(library);

            Assert.IsTrue(owner.StopAsync().Wait(TimeSpan.FromSeconds(10)));
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_ApplyFailureDoesNotBlockLaterVersion()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            EventHandler<NormalLibraryRefreshAppliedEventArgs> failingHandler =
                (_, _) => throw new InvalidOperationException("injected refresh apply failure");
            owner.NormalLibraryRefreshApplied += failingHandler;

            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\failed-refresh.bms")];
            owner.NormalLibraryRefreshApplied -= failingHandler;

            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.NotifiesBmsFiles)
                {
                    appliedCount++;
                }
            };
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\recovered-refresh.bms")];

            Assert.AreEqual(1, appliedCount);
        });
    }
}
