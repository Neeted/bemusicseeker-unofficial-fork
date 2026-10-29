using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryInitializationInstallTests
{
    /// <summary>正常群・alias・正常不存在と、重複群または孤立fileのreadlock失敗が混在しても、全旧行・bool・既存一覧を保持します。完成した一部の公開やmissingだけの先行削除を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReloadInstallTable_IncompleteRestoreKeepsAllRowsAndExistingCollections(bool overlapping)
    {
        WithTemporaryLr2SongDb((root, database) =>
        {
            string good = Path.Combine(root, "Good");
            string goodChild = Path.Combine(good, "Child");
            Directory.CreateDirectory(goodChild);
            string goodChart = Path.Combine(good, "a.bms");
            File.WriteAllText(goodChart, CreateValidBmsText("A"), Encoding.ASCII);
            File.WriteAllText(Path.Combine(goodChild, "child.bms"), CreateValidBmsText("C"), Encoding.ASCII);
            string bad = Path.Combine(root, "Bad");
            string child = Path.Combine(bad, "Child");
            Directory.CreateDirectory(child);
            string locked = Path.Combine(bad, "locked.bms");
            File.WriteAllText(locked, CreateValidBmsText("Locked"), Encoding.ASCII);
            File.WriteAllText(Path.Combine(child, "readable.bms"), CreateValidBmsText("Readable"), Encoding.ASCII);
            string missing = Path.Combine(root, "Missing");
            string[] rawPaths = overlapping
                ? [good, goodChild, Path.Combine(good, "."), missing, bad, child]
                : [good, goodChild, Path.Combine(good, "."), missing, locked];
            ExecuteSongDbFixtureTransaction(database, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                for (int index = 0; index < rawPaths.Length; index++)
                { db.Insert(new LR2SongDBExtended.install { path = rawPaths[index], delete_parent = index % 2 == 0 }); }
            });
            Dictionary<string, byte[]> bytes = Directory.GetFiles(root, "*.bms", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var gateway = new BmsLibraryDbGateway(database);
            var oldRows = gateway.LoadInstallPackages().ToDictionary(row => row.path, row => row.delete_parent, StringComparer.Ordinal);
            int publications = 0;
            var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), _ => publications++,
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            var oldPending = new ChartPackage { path = good, delete_parent = true };
            var oldInstalled = new ChartPackage { path = Path.Combine(root, "Installed") };
            owner.ReplacePendingPackages([oldPending]);
            owner.ReplaceInstalledPackages([oldInstalled]);
            object previousPending = owner.PendingPackages;
            object previousInstalled = owner.InstalledPackages;
            publications = 0;
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                PendingPackageRestoreException failure = Assert.ThrowsException<PendingPackageRestoreException>(() =>
                    owner.ReloadInstallTableWithoutPublishing(new BmsLibraryInitializationService(), gateway, [], _ => false, out _));
                Assert.IsInstanceOfType<IOException>(failure.InnerException);
                Assert.AreSame(previousPending, owner.PendingPackages);
                Assert.AreSame(previousInstalled, owner.InstalledPackages);
                Assert.AreSame(oldPending, owner.PendingPackages.Single());
                Assert.AreSame(oldInstalled, owner.InstalledPackages.Single());
                Assert.AreEqual(0, publications);
                List<ChartPackage> current = gateway.LoadInstallPackages();
                Assert.AreEqual(oldRows.Count, current.Count);
                foreach (ChartPackage row in current) { Assert.AreEqual(oldRows[row.path], row.delete_parent); }
            }
            foreach ((string path, byte[] value) in bytes) { CollectionAssert.AreEqual(value, File.ReadAllBytes(path)); }
        });
    }

    /// <summary>不正保存pathの既存Normalizerによる実失敗も、原因を保持して全体失敗にします。空白などの判定不能行や正常不存在だけを先に消すことを禁止します。通常入力の受理規則へ新しい構文検査は追加しません。</summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void ReloadInstallTable_InvalidStoredPathKeepsEveryRowAndExistingCollection(string invalidPath)
    {
        WithTemporaryLr2SongDb((root, database) =>
        {
            string good = Path.Combine(root, "Good");
            Directory.CreateDirectory(good);
            string chart = Path.Combine(good, "a.bms");
            File.WriteAllText(chart, CreateValidBmsText("A"), Encoding.ASCII);
            byte[] bytes = File.ReadAllBytes(chart);
            string[] rawPaths = [good, Path.Combine(root, "Missing"), invalidPath];
            ExecuteSongDbFixtureTransaction(database, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                foreach (string path in rawPaths) { db.Insert(new LR2SongDBExtended.install { path = path, delete_parent = true }); }
            });
            var gateway = new BmsLibraryDbGateway(database);
            var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            var existing = new ChartPackage { path = good };
            owner.ReplacePendingPackages([existing]);
            object previous = owner.PendingPackages;
            PendingPackageRestoreException failure = Assert.ThrowsException<PendingPackageRestoreException>(() =>
                owner.ReloadInstallTableWithoutPublishing(new BmsLibraryInitializationService(), gateway, [], _ => false, out _));
            Assert.IsInstanceOfType<ArgumentException>(failure.InnerException);
            Assert.AreSame(previous, owner.PendingPackages);
            Assert.AreSame(existing, owner.PendingPackages.Single());
            CollectionAssert.AreEquivalent(rawPaths, gateway.LoadInstallPackages().Select(row => row.path).ToArray());
            Assert.IsTrue(gateway.LoadInstallPackages().All(row => row.delete_parent));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(chart));
        });
    }

    /// <summary>起動登録整理の最初のINSERT直後のDB失敗を既存どおり公開前に伝播し、旧全行と空の公開集合を保持します。失敗を旧表示で隠して正常起動する経路を作りません。</summary>
    [TestMethod]
    public void ReloadInstallTable_NormalizationDatabaseFailureKeepsRowsAndStopsBeforePublication()
    {
        WithTemporaryLr2SongDb((root, database) =>
        {
            string parent = Path.Combine(root, "Parent");
            string child = Path.Combine(parent, "Child");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(parent, "a.bms"), CreateValidBmsText("A"), Encoding.ASCII);
            File.WriteAllText(Path.Combine(child, "c.bms"), CreateValidBmsText("C"), Encoding.ASCII);
            ExecuteSongDbFixtureTransaction(database, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.Insert(new LR2SongDBExtended.install { path = parent, delete_parent = true });
                db.Insert(new LR2SongDBExtended.install { path = child });
                db.Execute("CREATE TRIGGER fail_startup_normalization AFTER INSERT ON install BEGIN SELECT RAISE(ABORT, 'startup-normalization'); END;");
            });
            var gateway = new BmsLibraryDbGateway(database);
            int publications = 0;
            var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), _ => publications++,
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            PendingPackageRestoreException failure = Assert.ThrowsException<PendingPackageRestoreException>(() =>
                owner.ReloadInstallTableWithoutPublishing(new BmsLibraryInitializationService(), gateway, [], _ => false, out _));
            Exception cause = failure.InnerException ?? throw new AssertFailedException("元のDB原因がありません。");
            Assert.IsInstanceOfType<SQLite.SQLiteException>(cause);
            StringAssert.Contains(cause.Message, "startup-normalization");
            Assert.AreEqual(0, owner.PendingPackages.Count);
            Assert.AreEqual(0, publications);
            CollectionAssert.AreEquivalent(new[] { parent, child }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
            Assert.IsTrue(gateway.LoadInstallPackages().Single(package => package.path == parent).delete_parent);
        });
    }
    /// <summary>明示Resetはinstall全行だけを一括削除します。正常・alias・不存在・読取不能と実管理入力を含め、元bytes・導入済曲・他表・導入済一覧を保ちます。最後のDELETE後abortでも全旧行と既存一覧を維持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResetPendingPackages_ChangesOnlyInstallAfterAtomicSuccess(bool abortLastDelete)
    {
        WithTemporaryLr2SongDb((root, database) =>
        {
            string managed = TempDirectoryPublisher.Get("pending-restore-reset-model");
            try
            {
                string borrowed = Path.Combine(root, "Borrowed");
                Directory.CreateDirectory(borrowed);
                string chart = Path.Combine(borrowed, "locked.bms");
                File.WriteAllText(chart, CreateValidBmsText("Borrowed"), Encoding.ASCII);
                File.WriteAllText(Path.Combine(managed, "managed.bms"), CreateValidBmsText("Managed"), Encoding.ASCII);
                File.WriteAllText(Path.Combine(managed, "readme.txt"), "保持する同梱物", new UTF8Encoding(false));
                string installedChart = Path.Combine(root, "installed.bms");
                File.WriteAllText(installedChart, CreateValidBmsText("Installed"), Encoding.ASCII);
                string[] keys = [borrowed, Path.Combine(borrowed, "."), chart, Path.Combine(root, "Missing"), managed, "", "   "];
                ExecuteSongDbFixtureTransaction(database, db =>
                {
                    db.CreateTable<LR2SongDBExtended.install>();
                    db.CreateTable<LR2SongDB.song>();
                    db.InsertOrReplace(new LR2SongDB.song { path = installedChart, hash = "kept-owned" });
                    db.Execute("CREATE TABLE reset_keep (value TEXT)");
                    db.Execute("INSERT INTO reset_keep(value) VALUES (?)", "kept");
                    foreach (string key in keys) { db.Insert(new LR2SongDBExtended.install { path = key, delete_parent = key == managed }); }
                    if (abortLastDelete)
                    {
                        db.Execute("CREATE TRIGGER fail_reset AFTER DELETE ON install WHEN (SELECT COUNT(*) FROM install) = 0 BEGIN SELECT RAISE(ABORT, 'reset-install'); END;");
                    }
                });
                var paths = new List<string> { chart, installedChart, Path.Combine(managed, "managed.bms"), Path.Combine(managed, "readme.txt") };
                var bytes = paths.ToDictionary(path => path, File.ReadAllBytes);
                var gateway = new BmsLibraryDbGateway(database);
                int publications = 0;
                int? rowsAtPublication = null;
                var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), property =>
                {
                    if (property == "ChartPackagesPending")
                    {
                        publications++;
                        rowsAtPublication = gateway.LoadInstallPackages().Count;
                    }
                }, packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
                var pending = new ChartPackage { path = borrowed };
                var installed = new ChartPackage { path = Path.GetDirectoryName(installedChart) };
                owner.ReplacePendingPackages([pending]);
                owner.ReplaceInstalledPackages([installed]);
                object previous = owner.PendingPackages;
                object previousInstalled = owner.InstalledPackages;
                publications = 0;
                rowsAtPublication = null;
                using (var held = new FileStream(chart, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (abortLastDelete)
                    {
                        PendingPackageRestoreException failure = Assert.ThrowsException<PendingPackageRestoreException>(() => owner.ResetPendingPackagesWithoutPublishing(gateway));
                        Exception cause = failure.InnerException ?? throw new AssertFailedException("元のDB原因がありません。");
                        Assert.IsInstanceOfType<SQLite.SQLiteException>(cause);
                        StringAssert.Contains(cause.Message, "reset-install");
                        Assert.AreSame(previous, owner.PendingPackages);
                        Assert.AreEqual(0, publications);
                        Assert.IsNull(rowsAtPublication);
                        CollectionAssert.AreEquivalent(keys, gateway.LoadInstallPackages().Select(row => row.path).ToArray());
                        Assert.IsTrue(gateway.LoadInstallPackages().Single(row => row.path == managed).delete_parent);
                    }
                    else
                    {
                        Action publication = owner.ResetPendingPackagesWithoutPublishing(gateway);
                        Assert.AreEqual(0, publications);
                        publication();
                        Assert.AreEqual(0, owner.PendingPackages.Count);
                        Assert.AreEqual(1, publications);
                        Assert.AreEqual(0, rowsAtPublication);
                        Assert.AreEqual(0, gateway.LoadInstallPackages().Count);
                    }
                    Assert.AreSame(previousInstalled, owner.InstalledPackages);
                    Assert.AreSame(installed, owner.InstalledPackages.Single());
                    using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
                    Assert.IsNotNull(verify.Find<LR2SongDB.song>(installedChart));
                    Assert.AreEqual("kept", verify.ExecuteScalar<string>("SELECT value FROM reset_keep"));
                }
                foreach ((string path, byte[] value) in bytes) { CollectionAssert.AreEqual(value, File.ReadAllBytes(path)); }
                Assert.IsTrue(Directory.Exists(managed));
            }
            finally { Assert.IsTrue(TempDirectoryPublisher.TryDeleteManagedPath(managed), "このtestが作った管理rootだけを通常寿命の終端で回収します。"); }
        });
    }

    /// <summary>DB確定後の集合通知失敗を専用復元失敗へ戻しません。確定済みcanonical登録と完成一覧を保ち、Retryによる確定済み試行の再実行を防ぎます。</summary>
    [TestMethod]
    public void ReloadInstallTable_CommittedPublicationFailureIsNotRestoreFailure()
    {
        WithTemporaryLr2SongDb((root, database) =>
        {
            string pending = Path.Combine(root, "Pending");
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(pending, "a.bms"), CreateValidBmsText("A"), Encoding.ASCII);
            ExecuteSongDbFixtureTransaction(database, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.Insert(new LR2SongDBExtended.install { path = Path.Combine(pending, ".") });
            });
            var gateway = new BmsLibraryDbGateway(database);
            var expected = new InvalidOperationException("committed publication failure");
            var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), _ => throw expected,
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            owner.ReloadInstallTableWithoutPublishing(new BmsLibraryInitializationService(), gateway, [], _ => false, out Action publication);
            InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() => publication());
            Assert.AreSame(expected, failure);
            Assert.AreEqual(pending, gateway.LoadInstallPackages().Single().path);
            Assert.AreEqual(pending, owner.PendingPackages.Single().path);
        });
    }

    /// <summary>実Libraryの今回files段階を完了してからRestore/Resetの必須通知を保持します。DBと完成集合は確定済みでもTaskと今回Readinessは未完了で、model snapshotと独立DB操作は完了できることを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PendingRestoreStage_AwaitsRequiredPublicationBeforeReadiness(bool reset)
        => VerifyPendingPublicationStageAsync(reset, fail: false, reject: false);

    /// <summary>実通知Actionの購読者faultとInvokeAsync受付rejectを区別し、確定済みDB・集合を戻さず通常のTask失敗を伝えます。今回Readyを成功にせず、専用復元失敗へ再分類しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PendingRestoreStage_PropagatesPublicationFailureAfterCommit(bool reject)
        => VerifyPendingPublicationStageAsync(reset: false, fail: true, reject: reject);

    private static async Task VerifyPendingPublicationStageAsync(bool reset, bool fail, bool reject)
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string root = scope.DirectoryPath;
        string songDb = scope.SongDbPath;
        string installed = Path.Combine(root, "Library", "Owned");
        string pending = Path.Combine(root, "Pending");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(pending);
        string ownedChart = Path.Combine(installed, "owned.bms");
        string pendingChart = Path.Combine(pending, "pending.bms");
        File.WriteAllText(ownedChart, CreateValidBmsText("Owned"), Encoding.ASCII);
        File.WriteAllText(pendingChart, CreateValidBmsText("Pending"), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(installed, "sound.wav"), [1]);
        byte[] originalBytes = File.ReadAllBytes(pendingChart);
        string alias = Path.Combine(pending, ".");
        ExecuteSongDbFixtureTransaction(songDb, db =>
        {
            db.CreateTable<LR2SongDBExtended.install>();
            db.Insert(new LR2SongDBExtended.install { path = alias, delete_parent = true });
        });
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true };
        var expected = new InvalidOperationException(reject ? "required-publication-rejected" : "required-subscriber-fault");
        var scheduler = new PendingPublicationGateUiScheduler(reject ? expected : null);
        var library = new TestBmsLibrary(songDb, null, null, null, new RecordingDialogService(), scheduler, () => options,
            CapturedChartFileScanner.FromFixture([ownedChart], new Dictionary<string, IEnumerable<string>> { [installed] = ["sound.wav"] }, [installed]),
            settings: TestBmsFactory.CreateRootSettings([Path.Combine(root, "Library")]));
        using var gate = new SemaphoreSlim(1, 1);
        var parent = new StartupLibraryInitializationWorkflowOwner(gate);
        StartupRequiredOperationLease? accepted = null;
        Task? stage = null;
        Task? probes = null;
        Action? scoreNotification = null;
        int notifications = 0;
        bool readyAtNotification = false;
        try
        {
            accepted = parent.AcquireRequiredOperation(library.OperationAdmission, library.Lr2Synchronization.PlaylistOperationAdmission);
            LibraryFileInitializationResult files = library.InitializeStartup([], null, PerformanceInteraction.Start("pending-publication-model"),
                accepted.Capability, leapYearRepairApproval: new(songDb), repairNotificationObserver: _ => { },
                scoreNotificationObserver: notification => scoreNotification = notification);
            // 既存private状態を読取観測するだけで、Ready専用の製品test APIや手動Markを追加しません。
            PackageLifecycleOwner lifecycle = typeof(BMSLibrary).GetField("packageLifecycleOwner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(library)
                as PackageLifecycleOwner ?? throw new AssertFailedException("既存の保留Readiness所有者を取得できません。");
            StartupInstallReadinessState readiness = lifecycle.StartupReadiness;
            Assert.IsTrue(readiness.CatalogLoaded);
            Assert.IsTrue(readiness.DestinationResourceIndexReady);
            Assert.IsFalse(readiness.PendingPackagesRestored);
            Assert.IsFalse(readiness.InstallReady);
            Assert.IsTrue(library.BmsCharts.Any(chart => chart.Path == ownedChart));
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.ChartPackagesPending)) { return; }
                notifications++;
                readyAtNotification |= readiness.PendingPackagesRestored || readiness.InstallReady;
                if (fail && !reject) { throw expected; }
            };
            scheduler.Armed = true;
            stage = reset ? library.ResetPendingPackagesUnderAdmissionAsync(accepted.Capability)
                : library.RestorePendingPackagesUnderAdmissionAsync(accepted.Capability, files.Options);
            await TestUiDispatcherHost.AwaitNotificationAsync(scheduler.Reached.Task, stage, "pending-required-publication");
            Assert.IsFalse(stage.IsCompleted);
            Assert.IsFalse(readiness.PendingPackagesRestored);
            Assert.IsFalse(readiness.InstallReady);
            Assert.AreEqual(0, notifications);
            var gateway = new BmsLibraryDbGateway(songDb);
            probes = Task.WhenAll(
                Task.Run(() =>
                {
                    Assert.AreEqual(reset ? 0 : 1, library.GetPendingBmsFormatChartFilesSnapshot().Count);
                    Assert.IsNotNull(library.CaptureNormalLibraryChartSnapshot());
                }),
                Task.Run(() =>
                {
                    List<ChartPackage> rows = gateway.LoadInstallPackages();
                    Assert.AreEqual(reset ? 0 : 1, rows.Count);
                    if (!reset) { Assert.AreEqual(pending, rows.Single().path); Assert.IsTrue(rows.Single().delete_parent); }
                    gateway.ExecuteSongDbTransaction(db => db.Execute("CREATE TABLE publication_probe (value TEXT)"));
                }));
            await probes;
            Assert.IsFalse(stage.IsCompleted);
            scheduler.Release.TrySetResult();
            if (fail)
            {
                InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => stage);
                Assert.AreSame(expected, failure);
                Assert.IsFalse(readiness.PendingPackagesRestored);
                Assert.IsFalse(readiness.InstallReady);
                Assert.AreEqual(reject ? 0 : 1, notifications);
            }
            else
            {
                await stage;
                Assert.IsTrue(readiness.PendingPackagesRestored);
                Assert.IsTrue(readiness.InstallEstimationReady);
                Assert.IsTrue(readiness.InstallReady);
                Assert.AreEqual(1, notifications);
            }
            Assert.IsFalse(readyAtNotification);
            Assert.AreEqual(reset ? 0 : 1, library.ChartPackagesPending.Count);
            CollectionAssert.AreEquivalent(reset ? Array.Empty<string>() : new[] { pending }, gateway.LoadInstallPackages().Select(row => row.path).ToArray());
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(pendingChart));
        }
        finally
        {
            scheduler.Release.TrySetResult();
            try
            {
                try { if (probes != null) { await probes; } }
                finally { if (stage != null) { try { await stage; } catch when (fail) { } } }
            }
            finally
            {
                accepted?.Dispose();
                try { scoreNotification?.Invoke(); }
                finally { library.RequestShutdown("pending-publication-model"); }
            }
        }
    }

    /// <summary>このfixtureの必須InvokeAsyncだけを保持します。本物Actionは通常TestUiSchedulerへ渡し、拒否条件だけを明示faultとして返します。</summary>
    private sealed class PendingPublicationGateUiScheduler(Exception? rejected) : IUiScheduler
    {
        private readonly TestUiScheduler inner = new(() => null);
        internal bool Armed { get; set; }
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable => inner.IsAvailable;
        public bool CanExecuteInline => inner.CanExecuteInline;
        public bool CheckAccess() => inner.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Schedule(action, priority);
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => Armed ? InvokeRequiredAsync(action, priority) : inner.InvokeAsync(action, priority);
        private async Task InvokeRequiredAsync(Action action, UiSchedulePriority priority)
        {
            Reached.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            if (rejected != null) { throw rejected; }
            await inner.InvokeAsync(action, priority).ConfigureAwait(false);
        }
    }

}
