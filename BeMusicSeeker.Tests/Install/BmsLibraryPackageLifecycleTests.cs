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
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryStateApplierTestSupport;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPackageLifecycleTests
{
    /// <summary>局所確定値の反映、複数所属、旧購読の解除と未具体化列の非I/Oを同じownerで確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PackageLifecycleOwner_CommittedCurrentChartsStayLocalAndReleaseRetiredEntries(bool bmson)
    {
        WithTemporarySongDb(songDbPath =>
        {
            int membershipNotifications = 0;
            var owner = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), (_, _) => { }, _ => { }, _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => membershipNotifications++, _ => { });
            ChartFile before = ChartTestValues.Empty(bmson ? ChartFileKind.Bmson : ChartFileKind.Bms) with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\before",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Sha256 = new string('a', 64),
                RawTitle = "before",
                Title = "before",
                Level = 1,
                Mode = 7
            };
            var first = PackageChartEntry.FromChart(before);
            var second = PackageChartEntry.FromChart(before);
            first.SetInstallDestinationPathOnly("C:\\Destination");
            first.SetSearchingStatus(true);
            var unrelated = PackageChartEntry.FromChart(before with { Token = new OwnedChartToken() });
            var pending = PackageChartEntry.FromChart(before with { Token = null });
            var firstPackage = ChartPackage.FromChartEntries([first]);
            var secondPackage = ChartPackage.FromChartEntries([first, second]);
            var background = ChartPackage.FromChartEntries([unrelated]);
            string lazyDirectory = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "lazy");
            Directory.CreateDirectory(lazyDirectory);
            File.WriteAllText(Path.Combine(lazyDirectory, "chart.bms"), "#TITLE lazy\n#BPM 120\n");
            var lazy = new ChartPackage { path = lazyDirectory };
            var oldCollection = new ObservableCollection<ChartPackage>([firstPackage, secondPackage, background, lazy]);
            owner.SetInstalledPackages(oldCollection);
            owner.ReplacePendingPackages([ChartPackage.FromChartEntries([pending])]);
            int notifications = 0;
            membershipNotifications = 0;
            System.ComponentModel.PropertyChangedEventHandler firstChanged = (_, _) =>
            {
                notifications++;
                var work = Task.Run(() => owner.SetInstalledPackages(owner.InstalledPackages));
                work.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            };
            System.ComponentModel.PropertyChangedEventHandler secondChanged = (_, _) => notifications++;
            var packageNames = new List<string?>();
            System.ComponentModel.PropertyChangedEventHandler packageChanged = (_, args) => packageNames.Add(args.PropertyName);
            first.PropertyChanged += firstChanged;
            second.PropertyChanged += secondChanged;
            firstPackage.PropertyChanged += packageChanged;
            try
            {
                ChartFile changed = before with
                {
                    Path = "C:\\Library\\after",
                    Md5 = new string('b', 32),
                    Sha256 = new string('b', 64),
                    RawTitle = "after",
                    Title = "after",
                    Level = 13,
                    Difficulty = 4,
                    Mode = 14
                };
                var facts = new List<InstalledChartCurrentChange>();
                Action publish = owner.PrepareCommittedChartApplication([changed], facts);
                Assert.AreEqual(0, packageNames.Count);
                Assert.AreEqual(2, facts.Count);
                Assert.AreSame(before.Token, facts[0].Before.Token);
                Assert.AreEqual(before.Path, facts[0].Before.Path);
                Assert.AreEqual(before.Title, facts[0].Before.Title);
                Assert.AreSame(changed, facts[0].Current);
                Assert.IsTrue(facts.Single(fact => fact.Entry == first).Packages.Contains(firstPackage));
                Assert.AreEqual(2, owner.LastCommittedChartEntryVisitCount);
                Assert.AreEqual(0, notifications);
                foreach (PackageChartEntry entry in new[] { first, second })
                {
                    Assert.AreEqual(changed.Path, entry.Chart.Path);
                    Assert.AreEqual(changed.Md5, entry.Chart.Md5);
                    Assert.AreEqual(changed.Sha256, entry.Chart.Sha256);
                    Assert.AreEqual(13, entry.Chart.Level);
                    Assert.AreEqual(14, entry.Chart.Mode);
                    Assert.AreSame(before.Token, entry.Chart.Token);
                }
                Assert.AreEqual("C:\\Destination", first.Chart.InstallDestination);
                Assert.IsTrue(first.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
                Assert.AreSame(first, firstPackage.ChartEntries.Single());
                Assert.AreSame(second, secondPackage.ChartEntries[1]);
                Assert.AreEqual("C:\\Library\\before", before.Path);
                Assert.AreEqual("before", before.RawTitle);
                Assert.AreEqual(before.Path, unrelated.Chart.Path);
                Assert.AreEqual(before.Path, pending.Chart.Path);
                Assert.AreEqual(0, lazy.CaptureMaterializedChartEntries().Count);
                publish();
                Assert.AreEqual(2, notifications);
                Assert.AreEqual("after", firstPackage.DisplayTitle);
                Assert.IsTrue(packageNames.All(name => name == nameof(ChartPackage.DisplayTitle)));
                var paths = new LibraryPackageReferenceFacts(installedPackagePathChanges:
                    [new LibraryInstalledPackagePathChange { Package = firstPackage, NewPath = @"C:\After" }]);
                packageNames.Clear();
                BmsLibraryStateApplyResult pathResult = owner.ApplyPackageReferenceFacts(paths);
                Assert.AreEqual(0, packageNames.Count);
                pathResult.PublishPackagePaths();
                CollectionAssert.AreEqual(new[] { nameof(ChartPackage.path), nameof(ChartPackage.DisplayTitle) }, packageNames);
                Assert.AreEqual(0, membershipNotifications);
                packageNames.Clear();
                owner.PrepareCommittedChartApplication([changed with { Title = "title-only", RawTitle = "title-only" }])();
                Assert.AreEqual("title-only", firstPackage.DisplayTitle);
                Assert.IsTrue(packageNames.All(name => name == nameof(ChartPackage.DisplayTitle)));
                firstPackage.PropertyChanged -= packageChanged;
                notifications = 2;
                owner.SetInstalledPackages(new ObservableCollection<ChartPackage>(oldCollection));
                owner.RemoveInstalledPackages([firstPackage]);
                Action secondPublication = owner.PrepareCommittedChartApplication([changed with { RawTitle = "second" }]);
                Assert.AreEqual(2, owner.LastCommittedChartEntryVisitCount, "同entryの別所属を一所属離脱で退役させません。");
                secondPublication();
                Assert.AreEqual(4, notifications);
                Action retiredPublication = owner.PrepareCommittedChartApplication([changed with { RawTitle = "retiring" }]);
                owner.ClearInstalledPackages();
                retiredPublication();
                Assert.AreEqual(4, notifications, "最後の所属を離脱したentryへ遅延通知しません。");
                oldCollection.Add(firstPackage);
                owner.PrepareCommittedChartApplication([changed with { RawTitle = "detached" }])();
                Assert.AreEqual(0, owner.LastCommittedChartEntryVisitCount);
                Assert.AreEqual("retiring", first.Chart.RawTitle);
                owner.ReplaceInstalledPackages([firstPackage]);
                owner.PrepareCommittedChartApplication([changed with { RawTitle = "rejoined" }])();
                Assert.AreEqual(1, owner.LastCommittedChartEntryVisitCount);
                Assert.AreEqual("rejoined", first.Chart.RawTitle);
                Assert.AreEqual("retiring", second.Chart.RawTitle);
                Assert.AreEqual(0, lazy.CaptureMaterializedChartEntries().Count);
            }
            finally
            {
                owner.ClearInstalledPackages();
                owner.ReplacePendingPackages([]);
                first.PropertyChanged -= firstChanged;
                second.PropertyChanged -= secondChanged;
                firstPackage.PropertyChanged -= packageChanged;
            }
        });
    }

    [TestMethod]
    public void InstallableMaintenance_RealAcceptanceAndReusedWorkerPublishEachFeatureRequestOrigin()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath);
            long token = 11;
            Func<Task>? scheduledWork = null;
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            MethodInfo? queue = typeof(BMSLibrary).GetMethod("QueueDeferredInstallableMaintenance", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queue);
            library.StartupProgressRequestFactory = (name, version) => new(5, token, name, version);
            library.StartupRequestProgressReporter = (request, running) =>
            {
                execution.Add((request, running));
                if (running && request.Version == 1)
                {
                    token = 22;
                    queue.Invoke(library, ["second", 0L, null]);
                }
            };
            library.AttachStartupRequestProgressSources();
            library.StartupBackgroundTaskScheduler = (_, _, _, work) => { scheduledWork = work; return true; };
            queue.Invoke(library, ["first", 0L, null]);
            Assert.IsNotNull(scheduledWork);
            scheduledWork().GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[]
            {
                (new OperationProgressRequest(5, 11, "installable_maintenance", 1), true),
                (new OperationProgressRequest(5, 11, "installable_maintenance", 1), false),
                (new OperationProgressRequest(5, 22, "installable_maintenance", 2), true),
                (new OperationProgressRequest(5, 22, "installable_maintenance", 2), false)
            }, execution);
            Assert.AreEqual(2, library.InstallableMaintenanceDeferredRequestedVersion);
            Assert.AreEqual(2, library.InstallableMaintenanceDeferredCompletedVersion);
            Assert.IsFalse(library.InstallableMaintenanceDeferredRunning);
        });
    }

    [TestMethod]
    public void PackageLifecycleOwner_PendingOperationAdmissionIsExclusiveAndReleases()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                (_, _) => { },
                _ => { },
                _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                _ => { });

            Assert.IsTrue(owner.TryEnterPendingOperation(out IDisposable firstLease));
            try
            {
                Assert.IsFalse(owner.TryEnterPendingOperation(out IDisposable competingLease));
                Assert.IsNull(competingLease);
            }
            finally
            {
                firstLease.Dispose();
            }

            Assert.IsTrue(owner.TryEnterPendingOperation(out IDisposable releasedLease));
            releasedLease.Dispose();
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_RejectsCanonicalDuplicateBeforeDurableMutation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var existingPackage = new ChartPackage
            {
                path = "C:\\Pending\\Existing",
                delete_parent = false
            };
            var duplicatePackage = new ChartPackage
            {
                path = "C:\\Pending\\Existing\\.",
                delete_parent = false
            };
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([existingPackage]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                songDbPath,
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);

            Assert.ThrowsException<InvalidOperationException>(() => applier.ApplyPendingPackageMutationDelta(
                new PendingPackageMutationDelta
                {
                    HasChanges = true,
                    RemainingPackages = [existingPackage]
                },
                packagesToAdd: [duplicatePackage],
                installRowsToUpsert: [duplicatePackage]));

            Assert.AreEqual(1, pendingPackages.Count);
            Assert.AreSame(existingPackage, pendingPackages[0]);
            Assert.AreEqual("C:\\Pending\\Existing", existingPackage.path);
            Assert.AreEqual(0, callbacks.PendingPackagesSetCount);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_RejectsRepeatedCandidatePackageReferenceBeforeDurableMutation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var package = new ChartPackage
            {
                path = "C:\\Pending\\Repeated",
                delete_parent = false
            };
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                songDbPath,
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);

            Assert.ThrowsException<InvalidOperationException>(() => applier.ApplyPendingPackageMutationDelta(
                new PendingPackageMutationDelta
                {
                    HasChanges = true,
                    RemainingPackages = [package, package]
                },
                installRowsToUpsert: [package]));

            Assert.AreEqual(0, pendingPackages.Count);
            Assert.AreEqual(0, callbacks.PendingPackagesSetCount);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    [TestMethod]
    public void ApplyPendingPackageMutationDelta_DurableFailureLeavesLivePackagePathUnchanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var livePackage = new ChartPackage
            {
                path = "C:\\Pending\\Live\\.",
                delete_parent = false
            };
            var detachedPackage = new ChartPackage
            {
                path = "C:\\Pending\\Detached",
                delete_parent = false
            };
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([livePackage]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                Path.Combine(songDbPath, "missing", "song.db"),
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);

            Assert.ThrowsException<SQLite.SQLiteException>(() => applier.ApplyPendingPackageMutationDelta(
                new PendingPackageMutationDelta
                {
                    HasChanges = true,
                    RemainingPackages = [livePackage]
                },
                packagesToAdd: [detachedPackage],
                installRowsToUpsert: [detachedPackage]));

            Assert.AreEqual("C:\\Pending\\Live\\.", livePackage.path);
            Assert.AreEqual(1, pendingPackages.Count);
            Assert.AreSame(livePackage, pendingPackages.Single());
            Assert.AreEqual(0, callbacks.PendingPackagesSetCount);
        });
    }

    [TestMethod]
    public void ReplacePendingPackagesWithRegroupedPackage_RejectsCanonicalCollisionBeforeDbMutation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var firstSource = new ChartPackage
            {
                path = "C:\\Pending\\First",
                delete_parent = false
            };
            var secondSource = new ChartPackage
            {
                path = "C:\\Pending\\Second",
                delete_parent = false
            };
            var existingPackage = new ChartPackage
            {
                path = "C:\\Pending\\Existing",
                delete_parent = false
            };
            var regroupedPackage = new ChartPackage
            {
                path = "C:\\Pending\\Existing\\.",
                delete_parent = false
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(firstSource, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(secondSource, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(existingPackage, typeof(LR2SongDBExtended.install));
            }

            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                (_, _) => { },
                _ => { },
                _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                _ => { });
            owner.ReplacePendingPackages([firstSource, secondSource, existingPackage]);

            Assert.ThrowsException<InvalidOperationException>(() => owner.ReplacePendingPackagesWithRegroupedPackage(
                [firstSource, secondSource],
                regroupedPackage));

            CollectionAssert.AreEqual(
                new[] { firstSource, secondSource, existingPackage },
                owner.PendingPackages.ToArray());
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            CollectionAssert.AreEquivalent(
                new[] { firstSource.path, secondSource.path, existingPackage.path },
                verifySongDb.Table<ChartPackage>().Select(package => package.path).ToArray());
        });
    }

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
            Exception? observedFailure = null;
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
            Assert.AreEqual("collection subscriber failed", observedFailure!.Message);
        });
    }

    [TestMethod]
    public void PackageLifecycleOwner_QueuedScopeReportsAcceptedPublicationCancellation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            Exception? observedFailure = null;
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

            List<ChartFile> libraryFiles = [];
            List<ChartFile> bmsonSongs = [];
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
            ChartFile keepFile = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = "C:\\Pending\\Package\\keep.bms"
            };
            ChartFile removedFile = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = "C:\\Pending\\Package\\removed.bms"
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
