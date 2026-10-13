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
    // BMS/BMSONの共通現在値を、同じ所属・退役条件で検査します。
    [DataRow(false)]
    [DataRow(true)]
    public void PackageLifecycleOwner_CommittedCurrentChartsStayLocalAndReleaseRetiredEntries(bool bmson)
    {
        WithTemporarySongDb(songDbPath =>
        {
            int membershipNotifications = 0;
            var owner = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
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
                work.GetAwaiter().GetResult();
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

    /// <summary>受理済み保守workerへ追加要求が合流しても各要求のinteractionとversionで開始・終端を公開し、先行tokenの流用や未完了状態の残留を検出します。</summary>
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



    /// <summary>dot別名の既存保留path衝突はDB保存とcollection公開前に拒否し、同じ元pathの二重所有を検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
    [TestMethod]
    public void ApplyPendingPackageMutationDelta_RejectsCanonicalDuplicateBeforeDurableMutation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using (var preparationDb = new LR2SongDBExtended(songDbPath))
            {
                preparationDb.CreateTable<LR2SongDBExtended.install>();
            }
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
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    /// <summary>同一packageを候補へ二回渡す要求はDB保存とcollection公開前に拒否し、参照重複による二重所有を検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
    [TestMethod]
    public void ApplyPendingPackageMutationDelta_RejectsRepeatedCandidatePackageReferenceBeforeDurableMutation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using (var preparationDb = new LR2SongDBExtended(songDbPath))
            {
                preparationDb.CreateTable<LR2SongDBExtended.install>();
            }
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
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    /// <summary>実DB保存の失敗は保留collectionと元pathを変更せず、保存前の正本置換を検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
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

    /// <summary>再編成先が非対象の既存保留pathと衝突する場合は全元packageとDB行を保ち、拒否前の削除・置換を検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
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
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            CollectionAssert.AreEquivalent(
                new[] { firstSource.path, secondSource.path, existingPackage.path },
                verifySongDb.Table<ChartPackage>().Select(package => package.path).ToArray());
        });
    }

    /// <summary>collection変更scope内では新collectionを適用しても公開せず、scope終端で一回だけ通知する順序違反を検出します。</summary>
    [TestMethod]
    public void PackageLifecycleOwner_SetPendingPackagesPublishesAfterCollectionMutationScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            int propertyChangedCount = 0;
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
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

    /// <summary>共有UIキュー保持中に両集合状態を確定してscopeを返し、先行通知・診断先の失敗後も後続通知と実公開Taskの正常終端を保つことを確認します。</summary>
    /// <remarks>実ownerの公開操作と同priorityのdrainを捕捉して全Taskを回収し、同期UI待ち・後続通知の欠落・診断故障による公開Taskの失敗を検出します。共有キューを保持するため並列実行しません。</remarks>
    [DoNotParallelize]
    [DataTestMethod]
    // falseは診断成功、trueは元通知原因を受け取った診断先も失敗する条件です。
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackageLifecycleOwner_QueuedScopeReturnsBeforeUiPublicationAndObservesSubscriberFailure(bool diagnosticThrows)
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierTests_" + Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        var laneEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLane = new ManualResetEventSlim();
        var publicationFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IUiScheduledOperation? lane = null;
        IUiScheduledOperation? drain = null;
        PublicationTrackingUiScheduler? publicationScheduler = null;
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(songDbPath, []);
            Exception? observedFailure = null;
            int pendingNotificationCount = 0;
            int installedNotificationCount = 0;
            int diagnosticCount = 0;
            var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
            lane = scheduler.Schedule(() =>
            {
                laneEntered.TrySetResult();
                releaseLane.Wait();
            }, UiSchedulePriority.Normal);
            await TestUiDispatcherHost.AwaitNotificationAsync(laneEntered.Task, lane.Completion, "package-lifecycle.ui-gate");
            publicationScheduler = new PublicationTrackingUiScheduler(scheduler);
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath), publicationScheduler,
                propertyName =>
                {
                    if (propertyName == "ChartPackagesPending")
                    {
                        pendingNotificationCount++;
                        throw new InvalidOperationException("collection subscriber failed");
                    }
                    if (propertyName == "ChartPackagesInstalled")
                    {
                        installedNotificationCount++;
                    }
                },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                exception =>
                {
                    diagnosticCount++;
                    observedFailure = exception;
                    publicationFailed.TrySetResult();
                    if (diagnosticThrows)
                    {
                        throw new InvalidOperationException("collection diagnostic failed");
                    }
                });
            ObservableCollection<ChartPackage> replacement = CreatePackageCollection([
                new ChartPackage { path = "C:\\Pending\\Queued", delete_parent = false }
            ]);
            ObservableCollection<ChartPackage> installedReplacement = CreatePackageCollection([
                new ChartPackage { path = "C:\\Installed\\Queued", delete_parent = false }
            ]);
            using (owner.BeginCollectionMutationScope(queuePublication: true))
            {
                owner.SetPendingPackages(replacement);
                owner.SetInstalledPackages(installedReplacement);
            }
            IUiScheduledOperation? publication = publicationScheduler.PublicationOperation;
            drain = scheduler.Schedule(() => { }, UiSchedulePriority.Normal);
            Assert.IsNotNull(publication);
            Assert.IsTrue(publication.IsAccepted);
            Assert.IsFalse(publication.IsCompleted);
            Assert.AreSame(replacement, owner.PendingPackages);
            Assert.AreSame(installedReplacement, owner.InstalledPackages);
            Assert.AreEqual(0, pendingNotificationCount);
            Assert.AreEqual(0, installedNotificationCount);
            releaseLane.Set();
            await TestUiDispatcherHost.AwaitNotificationAsync(publicationFailed.Task, publication.Completion, "package-lifecycle.subscriber-failure");
            await publication.Completion;
            await drain.Completion;
            Assert.AreEqual(1, pendingNotificationCount);
            Assert.AreEqual(1, installedNotificationCount);
            Assert.AreEqual(1, diagnosticCount);
            Assert.AreSame(replacement, owner.PendingPackages);
            Assert.AreSame(installedReplacement, owner.InstalledPackages);
            Assert.IsNotNull(observedFailure);
            Assert.IsInstanceOfType<InvalidOperationException>(observedFailure);
            Assert.AreEqual("collection subscriber failed", observedFailure.Message);
        }
        catch (Exception failure) { primaryFailure = failure; }
        finally
        {
            releaseLane.Set();
            try
            {
                var started = new List<Task>();
                if (lane != null) { started.Add(lane.Completion); }
                if (publicationScheduler?.PublicationOperation is IUiScheduledOperation publication) { started.Add(publication.Completion); }
                if (drain != null) { started.Add(drain.Completion); }
                await Task.WhenAll(started);
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure == null) { primaryFailure = cleanupFailure; }
                else { primaryFailure.Data["PackagePublicationCleanupFailure"] = cleanupFailure.ToString(); }
            }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure == null) { primaryFailure = cleanupFailure; }
                else { primaryFailure.Data["PackagePublicationFixtureCleanupFailure"] = cleanupFailure.ToString(); }
            }
        }
        if (primaryFailure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw(); }
    }

    /// <summary>既存UI境界へ全呼出しを転送し、ownerが投稿した実公開操作だけをテストの終端待機用に捕捉します。</summary>
    private sealed class PublicationTrackingUiScheduler(IUiScheduler scheduler) : IUiScheduler
    {
        internal IUiScheduledOperation? PublicationOperation { get; private set; }

        public bool IsAvailable => scheduler.IsAvailable;

        public bool CanExecuteInline => scheduler.CanExecuteInline;

        public bool CheckAccess() => scheduler.CheckAccess();

        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            IUiScheduledOperation operation = scheduler.Schedule(action, priority);
            PublicationOperation = operation;
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => scheduler.Invoke(action, priority);

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => scheduler.Invoke(action, priority);

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => scheduler.InvokeAsync(action, priority);

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => scheduler.InvokeAsync(action, priority);
    }

    /// <summary>受理された公開操作が取消された場合はsubscriberを呼ばず取消失敗を報告し、受理だけで成功扱いする誤りを検出します。</summary>
    [TestMethod]
    public void PackageLifecycleOwner_QueuedScopeReportsAcceptedPublicationCancellation()
    {
        int unexpectedNotificationCount = 0;
        WithTemporarySongDb(delegate (string songDbPath)
        {
            Exception? observedFailure = null;
            var owner = new PackageLifecycleOwner(
                new BmsLibraryDbGateway(songDbPath),
                new CanceledScheduleUiScheduler(),
                _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount),
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
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>削除対象の実install行を除いて残るpackageを一回公開し、DBと保留collectionの不整合を検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
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
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    /// <summary>case-only別install行を完全一致で二件とも削除し、物理path比較へ潰して一行を残す誤りを検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
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
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

    /// <summary>実DB削除が失敗すると保留collectionと通知数を保ち、未保存の削除を正本へ公開する誤りを検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
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

    /// <summary>実DB削除が失敗した部分package変更では全entryを保持し、DB失敗前に一部entryを剪定する誤りを検出します。</summary>
    /// <remarks>実SQLiteの保存・拒否・失敗と保留collectionの反映を接続し、保存前の公開を迂回しません。</remarks>
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
