using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryStateApplierTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPackageLifecycleTests
{
    [TestMethod]
    public void PackageLifecycleOwner_SetPendingPackagesPublishesAfterCollectionMutationScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            int propertyChangedCount = 0;
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                (_, _) => { },
                _ => { },
                _ => propertyChangedCount++,
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                _ => { });
            ObservableCollection<ChartPackage> replacement = CreatePackageCollection([
                new ChartPackage { path = "C:\\Pending\\Replacement", delete_parent = false }
            ]);

            using (owner.BeginCollectionMutationScope())
            {
                owner.SetPendingPackages(replacement);

                Assert.AreSame(replacement, owner.PendingPackages);
                Assert.AreEqual(0, propertyChangedCount);
            }

            Assert.AreEqual(1, propertyChangedCount);
            Assert.AreSame(replacement, owner.PendingPackages);
        });
    }

    [TestMethod]
    public void PackageLifecycleOwner_QueuedScopeReturnsBeforeUiPublicationAndObservesSubscriberFailure()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var laneEntered = new ManualResetEventSlim();
            using var releaseLane = new ManualResetEventSlim();
            using var publicationFailed = new ManualResetEventSlim();
            Exception observedFailure = null;
            int propertyChangedCount = 0;
            var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
            scheduler.Schedule(() =>
            {
                laneEntered.Set();
                releaseLane.Wait();
            });
            Assert.IsTrue(laneEntered.Wait(TimeSpan.FromSeconds(5)));
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                scheduler,
                (_, _) => { },
                _ => { },
                _ =>
                {
                    propertyChangedCount++;
                    throw new InvalidOperationException("collection subscriber failed");
                },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                exception =>
                {
                    observedFailure = exception;
                    publicationFailed.Set();
                });
            ObservableCollection<ChartPackage> replacement = CreatePackageCollection([
                new ChartPackage { path = "C:\\Pending\\Queued", delete_parent = false }
            ]);

            using (owner.BeginCollectionMutationScope(queuePublication: true))
            {
                owner.SetPendingPackages(replacement);
            }

            Assert.AreSame(replacement, owner.PendingPackages);
            Assert.AreEqual(0, propertyChangedCount);
            releaseLane.Set();
            Assert.IsTrue(publicationFailed.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, propertyChangedCount);
            Assert.IsInstanceOfType<InvalidOperationException>(observedFailure);
            Assert.AreEqual("collection subscriber failed", observedFailure.Message);
        });
    }

    [TestMethod]
    public void PackageLifecycleOwner_QueuedScopeReportsAcceptedPublicationCancellation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            Exception observedFailure = null;
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                new CanceledScheduleUiScheduler(),
                (_, _) => { },
                _ => { },
                _ => Assert.Fail("Canceled publication must not invoke the subscriber."),
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                exception => observedFailure = exception);

            using (owner.BeginCollectionMutationScope(queuePublication: true))
            {
                owner.SetPendingPackages(CreatePackageCollection([
                    new ChartPackage { path = "C:\\Pending\\Canceled", delete_parent = false }
                ]));
            }

            Assert.IsInstanceOfType<OperationCanceledException>(observedFailure);
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_UpdatesPendingCollectionAndDeletesInstallRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedPackage = new ChartPackage
            {
                path = "C:\\Pending\\Removed",
                delete_parent = false
            };
            var remainingPackage = new ChartPackage
            {
                path = "C:\\Pending\\Remaining",
                delete_parent = false
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(removedPackage, typeof(LR2SongDBExtended.install));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([removedPackage, remainingPackage]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.ApplyPendingPackageMutationDelta(new PendingPackageMutationDelta
            {
                HasChanges = true,
                RemainingPackages = [remainingPackage],
                InstallPathsToDelete = [removedPackage.path]
            });

            Assert.AreEqual(1, pendingPackages.Count);
            Assert.AreSame(remainingPackage, pendingPackages.Single());
            Assert.AreEqual(1, callbacks.PendingPackagesSetCount);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_DeletesCaseVariantInstallRowsWithoutCollapsingKeys()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var packageUpper = new ChartPackage
            {
                path = "C:\\Pending\\CaseVariant",
                delete_parent = false
            };
            var packageLower = new ChartPackage
            {
                path = "c:\\pending\\casevariant",
                delete_parent = false
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(packageUpper, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(packageLower, typeof(LR2SongDBExtended.install));
            }

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([packageUpper, packageLower]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.ApplyPendingPackageMutationDelta(new PendingPackageMutationDelta
            {
                HasChanges = true,
                RemainingPackages = [],
                InstallPathsToDelete = [packageUpper.path, packageLower.path]
            });

            Assert.AreEqual(0, pendingPackages.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_DurableDeleteFailureLeavesPendingCollectionUnchanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var remainingPackage = new ChartPackage
            {
                path = "C:\\Pending\\Remaining",
                delete_parent = false
            };
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([remainingPackage]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                Path.Combine(songDbPath, "missing", "song.db"),
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);

            Assert.ThrowsException<SQLite.SQLiteException>(() => applier.ApplyPendingPackageMutationDelta(new PendingPackageMutationDelta
            {
                HasChanges = true,
                RemainingPackages = [],
                InstallPathsToDelete = ["C:\\Pending\\Removed"]
            }));

            Assert.AreEqual(1, pendingPackages.Count);
            Assert.AreSame(remainingPackage, pendingPackages.Single());
            Assert.AreEqual(0, callbacks.PendingPackagesSetCount);
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_DurableDeleteFailureLeavesPartialPackageEntriesUnchanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile keepFile = new()
            {
                path = "C:\\Pending\\Package\\keep.bms"
            };
            TestableBmsFile removedFile = new()
            {
                path = "C:\\Pending\\Package\\removed.bms"
            };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([keepFile, removedFile]);
            package.path = "C:\\Pending\\Package";
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([package]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                Path.Combine(songDbPath, "missing", "song.db"),
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);
            var delta = new PendingPackageMutationDelta
            {
                HasChanges = true,
                RemainingPackages = [package],
                InstallPathsToDelete = [package.path]
            };
            delta.EntryMutations.Add(new PendingPackageEntryMutation(package, [package.ChartEntries[0]]));

            Assert.ThrowsException<SQLite.SQLiteException>(() => applier.ApplyPendingPackageMutationDelta(delta));

            Assert.AreEqual(2, package.ChartEntries.Count);
            Assert.AreSame(package, pendingPackages.Single());
            Assert.AreEqual(0, callbacks.PendingPackagesSetCount);
        });
    }
}
